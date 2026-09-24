namespace BatteryChecker.Battery;

/// <summary>Whether the laptop's draw can be estimated while plugged in.</summary>
public enum DrawState
{
    Unavailable,   // this machine doesn't expose a processor power meter
    Calibrating,   // it does, but the rest-of-machine cost is still a typical guess or only partly learned: rough
    Ready          // learned from a couple of minutes on battery
}

/// <summary>
/// The Joulemeter idea. On battery the true draw is known (it's what the battery is losing). The processor's
/// own meter covers cores, graphics and memory but not the screen, drive, radios or fans; the gap between the
/// two is the machine's overhead, and it's stable enough per laptop to learn. Once learned, plugged-in draw is
/// simply processor meter plus overhead, live, and charger output is that plus whatever flows into the battery.
/// The overhead keeps refining every time the laptop runs on battery, and it's remembered between runs.
/// </summary>
public sealed class DrawModel
{
    private const double CpuSmoothingSeconds = 3;       // the meter is spiky; the estimate should read steady but follow real changes
    private const double OverheadSmoothingSeconds = 120;   // the overhead moves slowly (brightness, radios), so smooth it slowly
    private const double NeededSeconds = 120;            // this much battery time before the overhead is fully trusted
    private const double SettleSeconds = 20;             // after a plug or unplug, the battery rate takes a moment to mean anything
    private const double TypicalOverhead = 6;            // screen, drive, radios and fans on a typical laptop: the guess until this one is learned
    private const double TypicalPlatformOverhead = 1;    // the same guess when the meter already covers the whole platform (Intel's Psys)
    private const double MinOverhead = -1.5, MaxOverhead = 40;   // outside this the meter and the battery disagree so badly that one of them is wrong
    private const double MeterGiveUpSeconds = 15;       // a meter that has said nothing for this long isn't going to

    private double? _cpuSmooth;
    private double? _overhead;
    private double _learnedSeconds;
    private DateTime _last = DateTime.MinValue;
    private DateTime _firstFeed = DateTime.MinValue;
    private bool _meterEverRead;
    private DateTime _acChangedAt = DateTime.MinValue;
    private DateTime _savedAt = DateTime.MinValue;
    private bool? _lastOnAc;

    public DrawModel()
    {
        if (Settings.LearnedOverheadMilliwatts is { } mw && Settings.LearnedOverheadSeconds is { } s && s > 0 && mw / 1000.0 is >= MinOverhead and <= MaxOverhead)
        {
            _overhead = mw / 1000.0;
            _learnedSeconds = s;
        }
    }

    public DrawState State { get; private set; }
    /// <summary>The processor meter, smoothed, or null.</summary>
    public double? CpuWatts => _cpuSmooth;
    /// <summary>What the laptop is using: the real figure on battery, the estimate on the charger, or null.</summary>
    public double? LaptopWatts { get; private set; }
    /// <summary>What the charger is putting out in total, on the charger only.</summary>
    public double? ChargerWatts { get; private set; }
    /// <summary>The learned rest-of-machine cost, for diagnostics.</summary>
    public double? Overhead => _overhead;
    public double LearnedSeconds => _learnedSeconds;

    /// <param name="batteryWatts">+ into the battery, − out of it, as the battery reports it; null if unknown.</param>
    /// <param name="batteryWattsTrusted">True when batteryWatts is a real reading or a settled estimate, good enough to learn from.</param>
    /// <param name="cpu">The processor meter's raw reading this tick, or null.</param>
    public void Feed(DateTime now, bool onAc, double? batteryWatts, bool batteryWattsTrusted, double? cpu)
    {
        double gap = _last == DateTime.MinValue ? 0.25 : (now - _last).TotalSeconds;
        _last = now;
        // a gap longer than a few seconds is sleep, a hidden window, or a stalled read: don't count it as time learned,
        // and treat what follows like a fresh plug change, since the battery rate needs a moment to mean anything again
        if (gap > 5) _acChangedAt = now;
        double dt = Math.Clamp(gap, 0.01, 5);
        if (_lastOnAc != onAc) { _acChangedAt = now; _lastOnAc = onAc; }

        if (_firstFeed == DateTime.MinValue) _firstFeed = now;
        if (cpu is not null) _meterEverRead = true;
        if (cpu is { } c)
            _cpuSmooth = _cpuSmooth is { } prev ? prev + (c - prev) * Math.Min(1, dt / CpuSmoothingSeconds) : c;
        else if (!SystemPower.Available)
            _cpuSmooth = null;

        bool settled = (now - _acChangedAt).TotalSeconds > SettleSeconds;

        // learn: on battery, with a trusted battery figure and a processor figure, the gap is the overhead. A gap far
        // outside what any laptop has (a slope from two quick level steps, a meter spike) teaches nothing and is skipped.
        if (!onAc && settled && batteryWattsTrusted && batteryWatts is < 0 && _cpuSmooth is { } cpuNow
            && -batteryWatts.Value - cpuNow is var sample && sample is >= MinOverhead and <= MaxOverhead)
        {
            _learnedSeconds += dt;
            // a plain running average while it warms up, so the first minute counts fully; then a slow moving average
            double weight = _learnedSeconds < NeededSeconds ? dt / _learnedSeconds : dt / OverheadSmoothingSeconds;
            _overhead = _overhead is { } o ? o + (sample - o) * Math.Min(1, weight) : sample;
            if ((now - _savedAt).TotalSeconds > 10)
            {
                _savedAt = now;
                Settings.LearnedOverheadMilliwatts = (int)Math.Round(_overhead.Value * 1000);
                Settings.LearnedOverheadSeconds = (int)_learnedSeconds;
            }
        }

        bool learned = _overhead is not null && _learnedSeconds >= NeededSeconds;
        // a counter set that exists but never yields a reading (unknown instance names, nonsense values) is no meter at all
        bool meterDead = !_meterEverRead && (now - _firstFeed).TotalSeconds > MeterGiveUpSeconds;
        State = !SystemPower.Available || meterDead ? DrawState.Unavailable : learned ? DrawState.Ready : DrawState.Calibrating;

        if (!onAc)
        {
            LaptopWatts = batteryWatts is < 0 ? -batteryWatts.Value : null;   // on battery the truth is right there
            ChargerWatts = null;
        }
        else if (_cpuSmooth is { } cpuAc)
        {
            // learned or not, show a figure straight away: until this laptop has run on battery, the typical overhead
            double overhead = Math.Clamp(_overhead ?? (SystemPower.WholePlatform ? TypicalPlatformOverhead : TypicalOverhead), MinOverhead, MaxOverhead);
            LaptopWatts = Math.Max(0.5, cpuAc + overhead);
            // the charger is the laptop plus what goes into the battery; while the battery's rate is unknown, so is that
            ChargerWatts = batteryWatts is { } b ? Math.Max(0, LaptopWatts.Value + b) : null;
        }
        else
        {
            LaptopWatts = null;
            ChargerWatts = null;
        }
    }
}
