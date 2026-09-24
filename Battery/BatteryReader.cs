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
    DrawState Draw,
    bool Draining,       // plugged in, yet the battery has been going down for a few seconds: the charger can't keep up
    bool JustPlugged,    // plugged in a moment ago and the battery hasn't settled on a direction yet
    bool Settling)       // plugged or unplugged a moment ago and the battery still reports the old direction: its watts are stale
{
    public double? Health => FullMwh is > 0 && DesignMwh is > 0 ? Math.Min(1.0, (double)FullMwh.Value / DesignMwh.Value) : null;   // a fresh full charge can read above design; that is still "100 %"
    public double WattsIn => Watts is > 0 ? Watts.Value : 0;
    public double WattsOut => Watts is < 0 ? -Watts.Value : 0;
}

/// <summary>Reads the battery and the processor meter, and hands both to the interpreter. Fresh on every call.</summary>
public static class BatteryReader
{
    /// <summary>Design-time override for the shown percent. Set to null to use the real battery.</summary>
    public static double? DebugPercent = null;

    private static readonly BatteryInterpreter _interpreter = new(new DrawModel());
    private static (BatteryDriver.Status Status, BatteryDriver.Info Info)? _raw;

    public static Reading? Read()
    {
        var raw = BatteryDriver.Read();
        _raw = raw;
        bool tooWeak = false;
        if (raw is { } r && (r.Status.PowerState & BatteryDriver.PowerOnLine) != 0)
        {
            // Windows' own verdict on the charger: "Inadequate" means it can't cover what the laptop is drawing.
            try { tooWeak = Windows.System.Power.PowerManager.PowerSupplyStatus == Windows.System.Power.PowerSupplyStatus.Inadequate; } catch { }
        }
        return _interpreter.Interpret(DateTime.UtcNow, raw, BatteryDriver.WindowsPercent(), BatteryDriver.WindowsHoursLeft(), tooWeak, SystemPower.Watts(), DebugPercent);
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
        try { sb.AppendLine($"Power supply: {Windows.System.Power.PowerManager.PowerSupplyStatus}"); } catch { }
        sb.Append(_interpreter.Describe());
        sb.AppendLine($"Processor meter: {SystemPower.Description}");
        sb.AppendLine($"Memory: managed {GC.GetTotalMemory(false) / 1048576.0:0.0} MB · after full collections {GC.GetGCMemoryInfo(GCKind.FullBlocking).HeapSizeBytes / 1048576.0:0.0} MB · collections {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)} · process {Environment.WorkingSet / 1048576.0:0} MB");
        return sb.ToString();

        static string Cap(uint mwh) => mwh == BatteryDriver.UnknownCapacity ? "unknown" : mwh + " mWh";
    }
}

/// <summary>
/// Turns raw driver numbers into a Reading. No Windows calls in here, so every laptop's quirks can be replayed
/// against it in a test: firmware that reports no rate, a rate with the wrong sign, a flat zero while held at a
/// charge limit, a driver that fails for a moment around sleep or a plug change, a rate that lags the plug.
/// </summary>
public sealed class BatteryInterpreter
{
    private const double GraceSeconds = 5;       // a driver that fails for this long is a blip, not "no battery"
    private const double DrainingSeconds = 5;    // plugged in and going down for this long before calling it draining
    private const double ChargingHoldSeconds = 6;   // a lone zero or dip mid-charge must not flick the line to "holding"; longer than DrainingSeconds, so a real drain goes straight from charging to draining
    private const double PlugSettleSeconds = 6;  // after plugging in, the rate can still point the old way for a moment
    private const double ZeroGraceSeconds = 12;  // and right after a plug change or a start it can read 0 before the first real rate

    // Slopes for laptops whose firmware reports no rate: energy in mWh, and percent for the ones
    // that don't even report energy. Both restart when the charger comes or goes.
    private readonly RateEstimator _energySlope = new();
    private readonly RateEstimator _percentSlope = new();
    private readonly DrawModel _draw;
    private bool? _onAc;
    private DateTime _plugChangedAt = DateTime.MinValue;
    private DateTime _firstReadAt = DateTime.MinValue;
    private DateTime _lastChargingAt = DateTime.MinValue;
    private DateTime? _negativeSince;
    private bool _rateSeenCharging, _rateSeenDischarging;   // this firmware has reported a rate in that direction, so its zeros there are real
    private Reading? _last;
    private DateTime? _failingSince;

    public BatteryInterpreter(DrawModel draw) => _draw = draw;

    public Reading? Interpret(DateTime now, (BatteryDriver.Status Status, BatteryDriver.Info Info)? raw, double? windowsPercent, double? windowsHours, bool tooWeak, double? cpu, double? debugPercent = null)
    {
        if (raw is not var (status, info))
        {
            // Around sleep, resume or a plug change the driver can fail a read or two; keep showing the last reading
            // for a few seconds rather than flashing "No battery found". A real absence stays absent.
            _failingSince ??= now;   // counted from the first failure, not the last success: after sleep that was hours ago
            if (_last is not null && (now - _failingSince.Value).TotalSeconds < GraceSeconds)
                return _last;
            return _last = null;
        }

        bool relative = (info.Capabilities & BatteryDriver.CapacityRelative) != 0;
        int? full = info.FullMwh is not (BatteryDriver.UnknownCapacity or 0) && !relative ? (int)Math.Min(info.FullMwh, int.MaxValue) : null;
        int? design = info.DesignMwh is not (BatteryDriver.UnknownCapacity or 0) && !relative ? (int)Math.Min(info.DesignMwh, int.MaxValue) : null;
        int? remaining = status.CapacityMwh != BatteryDriver.UnknownCapacity && !relative ? (int)Math.Min(status.CapacityMwh, int.MaxValue) : null;

        // Windows' own percent (the taskbar number), falling back to the Wh ratio. Batteries trickle the last
        // fraction for ages and some never report "full", so anything from 99.3 % up simply reads 100.
        double? ratio = remaining is { } rem && full is { } fl ? Math.Clamp(100.0 * rem / fl, 0, 100) : null;
        double percent = windowsPercent is >= 0 and <= 100 ? windowsPercent.Value : ratio ?? 0;
        if (ratio >= 99.3)
            percent = 100;
        if (debugPercent is { } fake)
            percent = fake;

        bool onAc = (status.PowerState & BatteryDriver.PowerOnLine) != 0;
        bool flagCharging = (status.PowerState & BatteryDriver.Charging) != 0;
        bool flagDischarging = (status.PowerState & BatteryDriver.Discharging) != 0;
        if (_onAc != onAc)
        {
            if (_onAc is not null) _plugChangedAt = now;
            _onAc = onAc;
            _energySlope.Reset();
            _percentSlope.Reset();
            _negativeSince = null;
        }

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
            if (driver > 0) _rateSeenCharging = true; else _rateSeenDischarging = true;
        }
        // A flat zero while the battery says it's charging or discharging, from firmware that has never reported a
        // rate in that direction, is not a measurement: it doesn't report the rate (HP ships plenty with it switched
        // off in the BIOS). Treat it as unknown. Firmware that has reported one means its zero: a charge limit holding.
        // On battery the laptop always draws something, so a 0 there is never a measurement: the battery hasn't measured
        // the new direction yet (the first seconds after unplugging), or it never reports at all.
        bool zeroUnproven = driver == 0 && (!onAc || (flagCharging && !_rateSeenCharging) || (flagDischarging && !_rateSeenDischarging));
        bool driverUseless = driver is null || zeroUnproven;
        // A zero like that in the first seconds after a plug change or a start is usually just the battery not having
        // measured the new direction yet, not firmware that never reports: show it as settling, not "measuring".
        if (_firstReadAt == DateTime.MinValue) _firstReadAt = now;
        DateTime sinceChange = _plugChangedAt > _firstReadAt ? _plugChangedAt : _firstReadAt;
        bool zeroSettling = zeroUnproven && (now - sinceChange).TotalSeconds < ZeroGraceSeconds;

        // The fallback: the slope of the level over time.
        if (remaining is { } mwh) _energySlope.Feed(now, mwh);
        _percentSlope.Feed(now, percent);

        double? watts;
        WattsSource source;
        if (!driverUseless) { watts = driver; source = WattsSource.Driver; }
        else if (remaining is not null && _energySlope.PerHour is { } perHour) { watts = perHour / 1000.0; source = WattsSource.Estimated; }
        else if (remaining is not null) { watts = null; source = WattsSource.Measuring; }
        else { watts = null; source = WattsSource.Unavailable; }

        // at 100 the trickle isn't "charging"; without a rate, the flag is all there is
        bool chargingNow = onAc && flagCharging && percent < 100 && (watts is > 0 || (watts is null && source != WattsSource.Driver));
        if (chargingNow) _lastChargingAt = now;

        // Draining on the charger only once it has kept going down for a few seconds: right after plugging in, the
        // battery can still report the discharge it was doing a moment ago.
        if (onAc && watts is < 0) _negativeSince ??= now; else _negativeSince = null;
        bool draining = onAc && _negativeSince is { } since && (now - since).TotalSeconds >= DrainingSeconds;
        bool charging = chargingNow || (onAc && percent < 100 && !draining && (now - _lastChargingAt).TotalSeconds < ChargingHoldSeconds);
        bool recentPlug = _plugChangedAt != DateTime.MinValue && (now - _plugChangedAt).TotalSeconds < PlugSettleSeconds;
        bool justPlugged = onAc && !charging && !draining && recentPlug;
        // plugged in a moment ago and the battery still says 0: it hasn't started charging yet, so 0 isn't its answer
        bool settling = (recentPlug && (onAc ? watts is < 0 && !draining || watts == 0 : watts is > 0)) || (zeroSettling && watts is null);

        // The laptop's own draw, and from it the charger's output: learned on battery, estimated on the charger.
        _draw.Feed(now, onAc, watts, source is WattsSource.Driver or WattsSource.Estimated, cpu);

        _failingSince = null;
        return _last = new Reading(percent, watts, source, onAc, charging, remaining, full, design, info.Cycles is > 0 and < 100000 ? (int)info.Cycles : null, windowsHours,
            status.VoltageMv is not (BatteryDriver.UnknownVoltage or 0) and < 100000 ? status.VoltageMv / 1000.0 : null, tooWeak, _percentSlope.PerHour,
            _draw.CpuWatts, _draw.LaptopWatts, _draw.ChargerWatts, _draw.State, draining, justPlugged, settling);
    }

    public string Describe()
    {
        var sb = new System.Text.StringBuilder();
        if (_last is { } r)
            sb.AppendLine($"Shown: {r.Percent:0}% · {(r.Watts is { } w ? $"{w:+0.0;-0.0} W" : "— W")} ({r.Source}) · slope {_energySlope.PerHour?.ToString("0") ?? "none"} mWh/h · {_percentSlope.PerHour?.ToString("0.0") ?? "none"} %/h · rate seen {(_rateSeenCharging ? "charging " : "")}{(_rateSeenDischarging ? "discharging" : "")}");
        sb.AppendLine($"Draw model: {_draw.State} · cpu {_draw.CpuWatts?.ToString("0.0") ?? "—"} W · overhead {_draw.Overhead?.ToString("0.0") ?? "—"} W from {_draw.LearnedSeconds:0} s on battery · laptop {_draw.LaptopWatts?.ToString("0.0") ?? "—"} W · charger {_draw.ChargerWatts?.ToString("0.0") ?? "—"} W");
        return sb.ToString();
    }
}
