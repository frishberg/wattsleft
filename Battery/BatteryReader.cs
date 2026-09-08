namespace BatteryChecker.Battery;

/// <summary>One live look at the battery. Watts is + into the battery, - out of it.</summary>
public sealed record Reading(
    double Percent,
    double? Watts,
    bool OnAc,
    bool Charging,
    int? RemainingMwh,
    int? FullMwh,
    int? DesignMwh,
    int? Cycles,
    double? WindowsHoursLeft,
    double? Volts)
{
    public double? Health => FullMwh is > 0 && DesignMwh is > 0 ? Math.Min(1.0, (double)FullMwh.Value / DesignMwh.Value) : null;   // a fresh full charge can read above design; that is still "100 %"
    public double WattsIn => Watts is > 0 ? Watts.Value : 0;
    public double WattsOut => Watts is < 0 ? -Watts.Value : 0;
}

/// <summary>Turns a raw driver read into a Reading. Fresh on every call.</summary>
public static class BatteryReader
{
    /// <summary>Design-time override for the shown percent. Set to null to use the real battery.</summary>
    public static double? DebugPercent = null;

    public static Reading? Read()
    {
        if (BatteryDriver.Read() is not var (status, info))
            return null;

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

        double? watts = status.RateMw != BatteryDriver.UnknownRate && !relative ? status.RateMw / 1000.0 : null;
        if (watts is not null && Math.Abs(watts.Value) > 500)
            watts = null; // some firmware reports garbage; better to say "unknown" than lie

        bool onAc = (status.PowerState & BatteryDriver.PowerOnLine) != 0;
        bool charging = (status.PowerState & BatteryDriver.Charging) != 0 && watts is > 0 && percent < 100;   // at 100 the trickle isn't "charging"

        return new Reading(percent, watts, onAc, charging, remaining, full, design, info.Cycles > 0 ? (int)info.Cycles : null, BatteryDriver.WindowsHoursLeft(),
            status.VoltageMv != BatteryDriver.UnknownVoltage ? status.VoltageMv / 1000.0 : null);
    }
}
