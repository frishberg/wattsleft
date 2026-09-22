namespace BatteryChecker.Battery;

/// <summary>Where the watts came from, so the screen can say so.</summary>
public enum WattsSource
{
    Driver,        // the battery reported its rate
    Estimated,     // the battery reports no rate; worked out from how the level moves
    Measuring,     // same, but the level hasn't moved enough yet to say
    Unavailable    // this battery reports neither a rate nor a capacity in watt-hours
}

/// <summary>One live look at the battery. Watts is + into the battery, - out of it.</summary>
public sealed record Reading(
    double Percent,
    double? Watts,
    WattsSource Source,
    bool OnAc,
    bool Charging,
    int? RemainingMwh,
    int? FullMwh,
    int? DesignMwh,
    int? Cycles,
    double? WindowsHoursLeft,
    double? Volts,
    bool ChargerTooWeak,
    double? PercentPerHour,
    double? CpuWatts,
    double? LaptopWatts,
    double? ChargerWatts,
    DrawState Draw)
{
    public double? Health => FullMwh is > 0 && DesignMwh is > 0 ? Math.Min(1.0, (double)FullMwh.Value / DesignMwh.Value) : null;   // a fresh full charge can read above design; that is still "100 %"
    public double WattsIn => Watts is > 0 ? Watts.Value : 0;
    public double WattsOut => Watts is < 0 ? -Watts.Value : 0;
    /// <summary>Plugged in, yet the battery is still going down: the laptop wants more than the charger gives.</summary>
    public bool Draining => OnAc && Watts is < 0;
}

/// <summary>Turns a raw driver read into a Reading. Fresh on every call.</summary>
public static class BatteryReader
{
    /// <summary>Design-time override for the shown percent. Set to null to use the real battery.</summary>
    public static double? DebugPercent = null;

    // Slopes for laptops whose firmware reports no rate: energy in mWh, and percent for the ones
    // that don't even report energy. Both restart when the charger comes or goes.
    private static readonly RateEstimator _energySlope = new();
    private static readonly RateEstimator _percentSlope = new();
    private static bool? _slopeOnAc;
    private static (BatteryDriver.Status Status, BatteryDriver.Info Info)? _raw;
    private static Reading? _last;
    private static readonly DrawModel _draw = new();

    public static Reading? Read()
    {
        if (BatteryDriver.Read() is not var (status, info))
        {
            _raw = null;
            return _last = null;
        }
        _raw = (status, info);
        var now = DateTime.UtcNow;

        bool relative = (info.Capabilities & BatteryDriver.CapacityRelative) != 0;
        int? full = info.FullMwh != BatteryDriver.UnknownCapacity && !relative ? (int)info.FullMwh : null;
        int? design = info.DesignMwh != BatteryDriver.UnknownCapacity && !relative ? (int)info.DesignMwh : null;
        int? remaining = status.CapacityMwh != BatteryDriver.UnknownCapacity && !relative ? (int)status.CapacityMwh : null;

        // Windows' own percent (the taskbar number), falling back to the Wh ratio. Batteries trickle the last
        // fraction for ages and some never report "full", so anything from 99.3 % up simply reads 100.
        double? ratio = status.CapacityMwh != BatteryDriver.UnknownCapacity && info.FullMwh is > 0 and not BatteryDriver.UnknownCapacity
            ? Math.Clamp(100.0 * status.CapacityMwh / info.FullMwh, 0, 100) : null;
        double percent = BatteryDriver.WindowsPercent() ?? ratio ?? 0;
        if (ratio >= 99.3)
            percent = 100;
        if (DebugPercent is { } fake)
            percent = fake;

        bool onAc = (status.PowerState & BatteryDriver.PowerOnLine) != 0;
        bool flagCharging = (status.PowerState & BatteryDriver.Charging) != 0;
        bool flagDischarging = (status.PowerState & BatteryDriver.Discharging) != 0;

        // What the battery says its rate is, if it says anything sensible.
        double? driver = status.RateMw != BatteryDriver.UnknownRate && !relative ? status.RateMw / 1000.0 : null;
        if (driver is not null && Math.Abs(driver.Value) > 500)
            driver = null; // some firmware reports garbage; better to say "unknown" than lie
        // ACPI gives the rate as a magnitude and the driver is meant to add the sign from the state; a few
        // firmwares get that backwards, so when the state is unambiguous, the state wins.
        if (driver is { } d && d != 0)
        {
            if (flagDischarging && !flagCharging && d > 0) driver = -d;
            else if (flagCharging && !flagDischarging && d < 0) driver = -d;
        }
        // A flat zero while the battery says it's charging or discharging is not a measurement, it's a firmware
        // that doesn't report the rate (HP ships plenty with it switched off in the BIOS). Treat it as unknown.
        bool driverUseless = driver is null || (driver == 0 && (flagCharging || flagDischarging));

        // The fallback: the slope of the level over time.
        if (_slopeOnAc != onAc)
        {
            _energySlope.Reset();
            _percentSlope.Reset();
            _slopeOnAc = onAc;
        }
        if (remaining is { } mwh) _energySlope.Feed(now, mwh);
        _percentSlope.Feed(now, percent);

        double? watts;
        WattsSource source;
        if (!driverUseless) { watts = driver; source = WattsSource.Driver; }
        else if (remaining is not null && _energySlope.PerHour is { } perHour) { watts = perHour / 1000.0; source = WattsSource.Estimated; }
        else if (remaining is not null) { watts = null; source = WattsSource.Measuring; }
        else { watts = null; source = WattsSource.Unavailable; }

        bool charging = flagCharging && percent < 100 && (watts is > 0 || (watts is null && source != WattsSource.Driver));   // at 100 the trickle isn't "charging"

        // Windows' own verdict on the charger: "Inadequate" means it can't cover what the laptop is drawing.
        bool tooWeak = onAc && Windows.System.Power.PowerManager.PowerSupplyStatus == Windows.System.Power.PowerSupplyStatus.Inadequate;

        // The laptop's own draw, and from it the charger's output: learned on battery, estimated on the charger.
        _draw.Feed(now, onAc, watts, source is WattsSource.Driver or WattsSource.Estimated, SystemPower.Watts());

        return _last = new Reading(percent, watts, source, onAc, charging, remaining, full, design, info.Cycles > 0 ? (int)info.Cycles : null, BatteryDriver.WindowsHoursLeft(),
            status.VoltageMv != BatteryDriver.UnknownVoltage ? status.VoltageMv / 1000.0 : null, tooWeak, _percentSlope.PerHour,
            _draw.CpuWatts, _draw.LaptopWatts, _draw.ChargerWatts, _draw.State);
    }

    /// <summary>Everything raw, as text, for a bug report from a laptop that shows something odd.</summary>
    public static string Diagnostics()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Watt's Left diagnostics · {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Windows {Environment.OSVersion.Version} · {(Settings.IsPackaged ? "Store" : "exe")} · {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            sb.AppendLine($"Machine: {key?.GetValue("SystemManufacturer")} {key?.GetValue("SystemProductName")} · BIOS {key?.GetValue("BIOSVersion")}");
        }
        catch { }
        if (_raw is not var (s, i))
        {
            sb.AppendLine("Battery: none found");
            return sb.ToString();
        }
        sb.AppendLine($"Batteries: {BatteryDriver.Count}");
        sb.AppendLine($"Capabilities: 0x{i.Capabilities:X8}{((i.Capabilities & BatteryDriver.CapacityRelative) != 0 ? " (relative units)" : "")}");
        sb.AppendLine($"Design: {Cap(i.DesignMwh)} · Full: {Cap(i.FullMwh)} · Cycles: {i.Cycles}");
        sb.AppendLine($"PowerState: 0x{s.PowerState:X}{((s.PowerState & BatteryDriver.PowerOnLine) != 0 ? " AC" : "")}{((s.PowerState & BatteryDriver.Charging) != 0 ? " charging" : "")}{((s.PowerState & BatteryDriver.Discharging) != 0 ? " discharging" : "")}{((s.PowerState & BatteryDriver.Critical) != 0 ? " critical" : "")}");
        sb.AppendLine($"Capacity: {Cap(s.CapacityMwh)} · Voltage: {(s.VoltageMv == BatteryDriver.UnknownVoltage ? "unknown" : s.VoltageMv + " mV")} · Rate: {(s.RateMw == BatteryDriver.UnknownRate ? "unknown" : s.RateMw + " mW")}");
        sb.AppendLine($"Windows percent: {BatteryDriver.WindowsPercent()?.ToString() ?? "unknown"} · Windows hours left: {BatteryDriver.WindowsHoursLeft()?.ToString("0.00") ?? "none"}");
        sb.AppendLine($"Power supply: {Windows.System.Power.PowerManager.PowerSupplyStatus}");
        if (_last is { } r)
            sb.AppendLine($"Shown: {r.Percent:0}% · {(r.Watts is { } w ? $"{w:+0.0;-0.0} W" : "— W")} ({r.Source}) · slope {_energySlope.PerHour?.ToString("0") ?? "none"} mWh/h · {_percentSlope.PerHour?.ToString("0.0") ?? "none"} %/h");
        sb.AppendLine($"Processor meter: {SystemPower.Description}");
        sb.AppendLine($"Draw model: {_draw.State} · cpu {_draw.CpuWatts?.ToString("0.0") ?? "—"} W · overhead {_draw.Overhead?.ToString("0.0") ?? "—"} W from {_draw.LearnedSeconds:0} s on battery · laptop {_draw.LaptopWatts?.ToString("0.0") ?? "—"} W · charger {_draw.ChargerWatts?.ToString("0.0") ?? "—"} W");
        return sb.ToString();

        static string Cap(uint mwh) => mwh == BatteryDriver.UnknownCapacity ? "unknown" : mwh + " mWh";
    }
}
