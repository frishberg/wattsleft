namespace BatteryChecker.Battery;

/// <summary>Whether the laptop's draw can be estimated while plugged in.</summary>
public enum DrawState
{
    Unavailable,   // this machine doesn't expose a processor power meter
    Calibrating,   // it does, but the rest-of-machine cost hasn't been learned yet: needs a few minutes on battery
    Ready          // estimating
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
    private const double NeededSeconds = 120;            // this much battery time before the overhead is trusted
    private const double SettleSeconds = 45;             // after a plug or unplug, the battery rate takes a while to mean anything
    private const double MinOverhead = -1.5, MaxOverhead = 60;   // outside this the meter and the battery disagree so badly that the units must be wrong

    private double? _cpuSmooth;
    private double? _overhead;
    private double _learnedSeconds;
    private DateTime _last = DateTime.MinValue;
    private DateTime _acChangedAt = DateTime.MinValue;
    private DateTime _savedAt = DateTime.MinValue;
    private bool? _lastOnAc;

    public DrawModel()
    {
        if (Settings.LearnedOverheadMilliwatts is { } mw && Settings.LearnedOverheadSeconds is { } s && s >= NeededSeconds)
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
        double dt = _last == DateTime.MinValue ? 0.25 : Math.Clamp((now - _last).TotalSeconds, 0.01, 5);
        _last = now;
        if (_lastOnAc != onAc) { _acChangedAt = now; _lastOnAc = onAc; }

        if (cpu is { } c)
            _cpuSmooth = _cpuSmooth is { } prev ? prev + (c - prev) * Math.Min(1, dt / CpuSmoothingSeconds) : c;
        else if (!SystemPower.Available)
            _cpuSmooth = null;

        bool settled = (now - _acChangedAt).TotalSeconds > SettleSeconds;

        // learn: on battery, with a trusted battery figure and a processor figure, the gap is the overhead
        if (!onAc && settled && batteryWattsTrusted && batteryWatts is < 0 && _cpuSmooth is { } cpuNow)
        {
            double sample = -batteryWatts.Value - cpuNow;
            _overhead = _overhead is { } o ? o + (sample - o) * Math.Min(1, dt / OverheadSmoothingSeconds) : sample;
            _learnedSeconds += dt;
            if (_learnedSeconds >= NeededSeconds && (_overhead < MinOverhead || _overhead > MaxOverhead))
            {
                _overhead = null;      // the meter is not measuring what we think it is on this machine: start over
                _learnedSeconds = 0;
            }
            if ((now - _savedAt).TotalSeconds > 30)
            {
                _savedAt = now;
                Settings.LearnedOverheadMilliwatts = _overhead is { } ov ? (int)Math.Round(ov * 1000) : null;
                Settings.LearnedOverheadSeconds = (int)_learnedSeconds;
            }
        }

        bool learned = _overhead is not null && _learnedSeconds >= NeededSeconds;
        State = !SystemPower.Available ? DrawState.Unavailable : learned ? DrawState.Ready : DrawState.Calibrating;

        if (!onAc)
        {
            LaptopWatts = batteryWatts is < 0 ? -batteryWatts.Value : null;   // on battery the truth is right there
            ChargerWatts = null;
        }
        else if (learned && _cpuSmooth is { } cpuAc)
        {
            LaptopWatts = Math.Max(0.5, cpuAc + _overhead!.Value);
            ChargerWatts = batteryWatts is { } b ? Math.Max(0, LaptopWatts.Value + b) : LaptopWatts;
        }
        else
        {
            LaptopWatts = null;
            ChargerWatts = null;
        }
    }
}
