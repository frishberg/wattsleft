using System.Numerics;
using System.Runtime.InteropServices;
using BatteryChecker.Battery;
using BatteryChecker.Tray;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;


namespace BatteryChecker;

/// <summary>
/// The app window. One window for the life of the process: closing hides it
/// to the tray, the tray icon brings it back.
/// </summary>
public sealed partial class MainWindow : Window
{
    private const int WidthDip = 340;
    private const int HeightDip = 412;
    private const int AverageSeconds = 30;
    private const int EstimateSeconds = 300;      // time-left uses a 5 minute average of the draw
    private const int TimeHoldSeconds = 30;       // and the shown value only moves every 30 s
    private const int PollMs = 250;   // the driver updates every second or so; polling faster keeps the numbers feeling live

    private readonly TrayIcon _tray;
    private readonly DispatcherQueueTimer _timer;
    private readonly HoverCards _cards;
    private readonly Queue<(DateTime At, double Watts)> _samples = new();
    private Reading? _reading;
    private double _peakIn, _peakOut;
    private bool? _statsOnAc;
    private double? _shownHours;
    private DateTime _shownAt;
    private bool _shownCharging;

    private enum Menu { Show, StartWithWindows, Separator, Quit }

    private bool? _wasCharging;

    public MainWindow()
    {
        InitializeComponent();
        Root.RequestedTheme = ElementTheme.Dark;   // white ink on Windows blue, whatever the system theme

        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);   // we draw the title bar ourselves
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = true;
        AppWindow.ResizeClient(new SizeInt32(Px(WidthDip), Px(DesignHeight)));
        RoundCorners();
        AppWindow.TitleBar.ExtendsContentIntoTitleBar = true;
        Root.SizeChanged += (_, _) => UpdateRegions();
        Root.Loaded += (_, _) => UpdateRegions();
        AppWindow.Changed += (_, e) =>
        {
            if (!e.DidPositionChange) return;
            if (_settingsOpen && !GoingHome) CloseSettings();   // dragging closes the panel, unless he's heading into it
            _movingUntil = DateTime.UtcNow.AddMilliseconds(250);   // and pauses the readings, so nothing competes with the drag
        };

        AppWindow.Closing += (_, e) => { e.Cancel = true; HideFlyout(); };
        Root.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Escape) { if (_settingsOpen) CloseSettings(); else HideFlyout(); } };

        _tray = new TrayIcon([("Show", false), ("Start with Windows", true), ("-", false), ("Quit", false)]);
        _tray.LeftClick += ToggleFlyout;
        _tray.MenuPicked += OnMenu;
        _ = RefreshStartupCheckAsync();

        CaptionButton(SettingsButton, SettingsDisc, OpenSettings);
        Clickable(PartyToggle, () => SetParty(!Settings.PartyMode));
        Clickable(GraphsToggle, () => SetGraphs(!Settings.ShowGraphs, animate: true));
        Clickable(StatsToggle, () => SetStats(!Settings.ShowStats));
        Root.Loaded += (_, _) => { SetParty(Settings.PartyMode, animate: false); SetStats(Settings.ShowStats, apply: false); SetGraphs(Settings.ShowGraphs, animate: false); };
        CaptionButton(MinimizeButton, MinimizeDisc, () => ((OverlappedPresenter)AppWindow.Presenter).Minimize());
        CaptionButton(CloseButton, CloseDisc, HideFlyout);

        LoadHistory();
        ChartHover(ChargeChart, ChargeHair, ChargeSpan, s => $"{s.Percent:0}%", () => _history.Count > 0 ? _history[0].At : (DateTime?)null);
        ChartHover(InChart, InHair, InScale, s => $"{Math.Max(0, s.Watts):0.0} W in", () => _history.Count > 0 ? _history[0].At : (DateTime?)null);
        ChartHover(OutChart, OutHair, OutScale, s => $"{Math.Max(0, -s.Watts):0.0} W out", () => _history.Count > 0 ? _history[0].At : (DateTime?)null);
        ChartHover(VoltsChart, VoltsHair, VoltsScale, s => $"{s.Volts:0.00} V", () => _history.FirstOrDefault(s => s.Volts > 0).At is var f && f != default ? f : (DateTime?)null);
        // Everything that ever moves on the compositor gets translation enabled once, up front. Animating
        // Translation on an element before this throws "property cannot be animated" and freezes the sequence.
        foreach (var e in new UIElement[] { Dancer, Ladder, TearL, TearR, SadMouth, Graphs, SettingsPanel, ChargeStats, InStats, OutStats, VoltsStats, Root })
            ElementCompositionPreview.SetIsTranslationEnabled(e, true);

        _cards = new HoverCards(DispatcherQueue);
        _cards.Attach(PercentBlock, CardPercent);
        _cards.Attach(InBlock, CardIn);
        _cards.Attach(OutBlock, CardOut);
        _cards.Attach(TimeRow, CardTime);
        _cards.Attach(StoredRow, CardStored);
        _cards.Attach(VoltageRow, CardVoltage);
        _cards.Attach(HealthRow, CardHealth);

        AnimateBarChanges();

        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(PollMs);
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Tick();
    }

    // ------------------------------------------------------------ our title bar

    private void Scrim_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) { CloseSettings(); e.Handled = true; }


    // The title bar sits above the scrim so its buttons keep their hover; a press on it still closes the panel.
    private void TitleBar_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) => CloseSettings();

    /// <summary>A click is a press and a release on the same element; a press elsewhere never counts.</summary>
    private static void Clickable(UIElement element, Action click)
    {
        bool pressed = false;
        element.PointerPressed += (_, e) => { pressed = e.GetCurrentPoint(null).Properties.IsLeftButtonPressed; e.Handled = true; };
        element.PointerExited += (_, _) => pressed = false;
        element.PointerReleased += (_, e) => { if (pressed) click(); pressed = false; e.Handled = true; };
    }

    /// <summary>Wire a caption button: the disc fades and springs in on hover, dips on press, and the click fires on release.</summary>
    private static void CaptionButton(Grid button, Border disc, Action click)
    {
        var scale = new Microsoft.UI.Xaml.Media.CompositeTransform { CenterX = 14, CenterY = 14, ScaleX = 0.7, ScaleY = 0.7 };
        disc.RenderTransform = scale;
        disc.Opacity = 0;
        var settle = new Microsoft.UI.Xaml.Media.Animation.CubicEase { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut };

        void Animate(double opacity, double size, int ms)
        {
            var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
            foreach (var (target, property, to) in new[] { ((DependencyObject)disc, "Opacity", opacity), (scale, "ScaleX", size), (scale, "ScaleY", size) })
            {
                var a = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation { To = to, Duration = TimeSpan.FromMilliseconds(ms), EasingFunction = settle, EnableDependentAnimation = true };
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(a, target);
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(a, property);
                sb.Children.Add(a);
            }
            sb.Begin();
        }

        bool pressed = false;
        button.PointerEntered += (_, _) => Animate(1, 1, 160);
        button.PointerExited += (_, _) => { pressed = false; Animate(0, 0.7, 200); };
        button.PointerPressed += (_, e) => { if (e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) { pressed = true; Animate(1, 0.86, 80); } e.Handled = true; };
        button.PointerReleased += (_, e) => { Animate(1, 1, 160); if (pressed) click(); pressed = false; e.Handled = true; };
    }

    /// <summary>Without a system title bar Windows forgets to round a fixed-size window; ask the compositor directly.</summary>
    private void RoundCorners()
    {
        const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2;
        int round = DWMWCP_ROUND;
        DwmSetWindowAttribute(Hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
    }

    private nint Hwnd => WinRT.Interop.WindowNative.GetWindowHandle(this);

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    /// <summary>EcoQoS: while hidden, let Windows schedule us like background work.</summary>
    private static void EfficiencyMode(bool on)
    {
        const int ProcessPowerThrottling = 4;
        const uint ExecutionSpeed = 0x1;
        var state = new PROCESS_POWER_THROTTLING_STATE { Version = 1, ControlMask = ExecutionSpeed, StateMask = on ? ExecutionSpeed : 0 };
        SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref state, Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());
    }

    [StructLayout(LayoutKind.Sequential)] private struct PROCESS_POWER_THROTTLING_STATE { public uint Version, ControlMask, StateMask; }
    [DllImport("kernel32.dll")] private static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern bool SetProcessInformation(nint process, int infoClass, ref PROCESS_POWER_THROTTLING_STATE info, int size);

    // ------------------------------------------------------------ show / hide

    public void ShowFlyout()
    {
        _timer.Interval = TimeSpan.FromMilliseconds(PollMs);
        EfficiencyMode(false);
        ResumeParty();
        if (Settings.WindowPosition is { } saved && OnSomeScreen(saved.X, saved.Y))
            AppWindow.Move(new PointInt32(saved.X, saved.Y));
        else
            AppWindow.Move(DefaultPosition());   // first run, or the monitor it was on is gone

        AppWindow.Show();
        Activate();
        Root.Focus(FocusState.Programmatic);
        PlayEntrance();
    }

    public void HideFlyout()
    {
        if (!AppWindow.IsVisible)
            return;
        _cards.Hide();
        Settings.WindowPosition = (AppWindow.Position.X, AppWindow.Position.Y);
        AppWindow.Hide();
        // Out of sight: read once a second (the tray only needs the percent), stop the dancing, and ask
        // Windows to run us on its efficiency cores. A battery app must not cost battery.
        _timer.Interval = TimeSpan.FromSeconds(1);
        PauseParty();
        EfficiencyMode(true);
    }

    private void ToggleFlyout()
    {
        if (AppWindow.IsVisible)
            HideFlyout();
        else
            ShowFlyout();
    }

    /// <summary>A remembered position only counts if at least a corner of the window would still be visible.</summary>
    private bool OnSomeScreen(int x, int y)
    {
        var probe = new RectInt32(x + 40, y + 20, 1, 1);
        var area = DisplayArea.GetFromRect(probe, DisplayAreaFallback.None);
        return area is not null;
    }

    /// <summary>Centre of the work area on first launch; after that we remember where you left it.</summary>
    private PointInt32 DefaultPosition()
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        return new PointInt32(area.X + (area.Width - AppWindow.Size.Width) / 2, area.Y + (area.Height - AppWindow.Size.Height) / 2);
    }

    private void OnMenu(int index)
    {
        switch ((Menu)index)
        {
            case Menu.Show: ShowFlyout(); break;
            case Menu.StartWithWindows: _ = ToggleStartupAsync(); break;
            case Menu.Quit: Quit(); break;
        }
    }

    private async Task ToggleStartupAsync()
    {
        bool now = await Settings.StartsWithWindowsAsync();
        await Settings.SetStartsWithWindowsAsync(!now);
        await RefreshStartupCheckAsync();
    }

    private async Task RefreshStartupCheckAsync()
    {
        _tray.SetChecked((int)Menu.StartWithWindows, await Settings.StartsWithWindowsAsync());
    }

    private void Quit()
    {
        _timer.Stop();
        if (AppWindow.IsVisible)
            Settings.WindowPosition = (AppWindow.Position.X, AppWindow.Position.Y);
        _tray.Dispose();
        Application.Current.Exit();
    }

    // ------------------------------------------------------------ motion

    private void PlayEntrance()
    {
        var visual = ElementCompositionPreview.GetElementVisual(Root);
        ElementCompositionPreview.SetIsTranslationEnabled(Root, true);
        var compositor = visual.Compositor;
        var enter = compositor.CreateCubicBezierEasingFunction(new Vector2(0, 0), new Vector2(0, 1));   // Fluent "fast out, slow in"

        var slide = compositor.CreateScalarKeyFrameAnimation();
        slide.InsertKeyFrame(0f, 24f);
        slide.InsertKeyFrame(1f, 0f, enter);
        slide.Duration = TimeSpan.FromMilliseconds(250);
        visual.StartAnimation("Translation.Y", slide);

        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0f, 0f);
        fade.InsertKeyFrame(1f, 1f, enter);
        fade.Duration = TimeSpan.FromMilliseconds(167);
        visual.StartAnimation("Opacity", fade);
    }

    // ------------------------------------------------------------ graphs

    private const int GraphsHeightDip = 320;
    private static readonly TimeSpan HistoryStep = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HistorySpan = TimeSpan.FromMinutes(30);
    private readonly List<(DateTime At, double Percent, double Watts, double Volts)> _history = new();
    private DateTime _lastSample = DateTime.MinValue;
    private static readonly string HistoryFile = Path.Combine(Settings.DataFolder, "history.csv");

    private async void SetGraphs(bool on, bool animate)
    {
        Settings.ShowGraphs = on;
        if (!on && animate) DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, GroundMoved);
        SlideKnob(GraphsKnobSlide, GraphsPill, on, animate);
        var panel = ElementCompositionPreview.GetElementVisual(Graphs);
        ElementCompositionPreview.SetIsTranslationEnabled(Graphs, true);
        int closed = Px(HeightDip), open = Px(HeightDip + GraphsHeightDip + (Settings.ShowStats ? 4 * StatsLineDip : 0));

        if (!animate)
        {
            Graphs.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                AppWindow.ResizeClient(new SizeInt32(Px(WidthDip), on ? open : closed));
            if (on) DrawGraphs();
            return;
        }

        if (on)
        {
            // the bottom edge slides down while the graphs fade in behind it
            panel.Opacity = 0;
            Graphs.Visibility = Visibility.Visible;
                DrawGraphs();
            var grow = SlideWindowHeight(closed, open, 200);
            await PlayOn(panel, 200, ("Opacity", 0f, 1f), ("Translation.Y", -10f, 0f));
            await grow;
            UpdateRegions();
        }
        else
        {
            // the graphs fade while the bottom edge slides up over them, both at once
            var shrink = SlideWindowHeight(open, closed, 150);
            await PlayOn(panel, 110, ("Opacity", 1f, 0f), ("Translation.Y", 0f, -10f));
            await shrink;
            Graphs.Visibility = Visibility.Collapsed;
            }
    }

    /// <summary>Move the window's bottom edge from one height to another, one step per rendered frame, eased.</summary>
    private Task SlideWindowHeight(int fromPx, int toPx, int ms)
    {
        var done = new TaskCompletionSource();
        var start = DateTime.UtcNow;
        void Frame(object? sender, object e)
        {
            double t = Math.Min(1, (DateTime.UtcNow - start).TotalMilliseconds / ms);
            double eased = 1 - Math.Pow(1 - t, 3);   // fast out, slow in
            AppWindow.ResizeClient(new SizeInt32(Px(WidthDip), (int)Math.Round(fromPx + (toPx - fromPx) * eased)));
            if (t >= 1)
            {
                Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= Frame;
                done.TrySetResult();
            }
        }
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += Frame;
        return done.Task;
    }

    private Task PlayOn(Microsoft.UI.Composition.Visual visual, int ms, params (string Property, float From, float To)[] items)
    {
        var done = new TaskCompletionSource();
        var compositor = visual.Compositor;
        var ease = compositor.CreateCubicBezierEasingFunction(new Vector2(0, 0), new Vector2(0, 1));
        var batch = compositor.CreateScopedBatch(Microsoft.UI.Composition.CompositionBatchTypes.Animation);
        foreach (var (property, from, to) in items)
        {
            var a = compositor.CreateScalarKeyFrameAnimation();
            a.InsertKeyFrame(0f, from);
            a.InsertKeyFrame(1f, to, ease);
            a.Duration = TimeSpan.FromMilliseconds(ms);
            visual.StartAnimation(property, a);
        }
        batch.Completed += (_, _) => done.TrySetResult();
        batch.End();
        return done.Task;
    }

    /// <summary>
    /// One sample every few seconds, always, whether or not the graphs are showing. Each sample is also
    /// appended to a small file so a restart doesn't lose the last half hour.
    /// </summary>
    private void RecordHistory(Reading? r)
    {
        var now = DateTime.UtcNow;
        if (r is null || now - _lastSample < HistoryStep)
            return;
        _lastSample = now;
        _history.Add((now, r.Percent, r.Watts ?? 0, r.Volts ?? 0));
        _history.RemoveAll(h => now - h.At > HistorySpan + HistoryStep);
        try
        {
            File.AppendAllText(HistoryFile, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{now:O},{r.Percent:0.0},{r.Watts ?? 0:0.00},{r.Volts ?? 0:0.000}") + Environment.NewLine);
            if (_history.Count % 360 == 0)   // every half hour: drop anything older than the graph window
                File.WriteAllLines(HistoryFile, _history.Select(h => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{h.At:O},{h.Percent:0.0},{h.Watts:0.00},{h.Volts:0.000}")));
        }
        catch
        {
            // a full disk or a locked file is not worth a crash; the in-memory history still works
        }
        if (Settings.ShowGraphs && AppWindow.IsVisible)
            DrawGraphs();
    }

    /// <summary>Reload whatever the last run left, so the graph starts full instead of empty.</summary>
    private void LoadHistory()
    {
        try
        {
            if (!File.Exists(HistoryFile)) return;
            var cutoff = DateTime.UtcNow - HistorySpan - HistoryStep;
            foreach (var line in File.ReadLines(HistoryFile))
            {
                var parts = line.Split(',');
                if (parts.Length >= 3
                    && DateTime.TryParse(parts[0], null, System.Globalization.DateTimeStyles.RoundtripKind, out var at)
                    && double.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var pct)
                    && double.TryParse(parts[2], System.Globalization.CultureInfo.InvariantCulture, out var w)
                    && at >= cutoff)
                {
                    double volts = parts.Length >= 4 && double.TryParse(parts[3], System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
                    _history.Add((at, pct, w, volts));
                }
            }
        }
        catch
        {
            // a damaged file just means starting empty
        }
    }

    private void DrawGraphs()
    {
        double w = ChargeChart.ActualWidth > 0 ? ChargeChart.ActualWidth : 284, h = 52;
        var now = DateTime.UtcNow;
        // Every chart always fills its width, from its own first sample to now: the scale stretches as the first
        // data comes in and compresses until there is 30 minutes of it, then it's a fixed 30 minute window.
        double Span(DateTime first) => Math.Clamp((now - first).TotalSeconds, 10, HistorySpan.TotalSeconds);
        double spanSeconds = _history.Count == 0 ? 10 : Span(_history[0].At);
        double X(DateTime at) => w - (now - at).TotalSeconds / spanSeconds * w;
        if (ChargeHair.Opacity == 0) ChargeSpan.Text = "0 to 100%";

        // charge: 0..100, area under the line
        var charge = new Microsoft.UI.Xaml.Media.PointCollection();
        var area = new Microsoft.UI.Xaml.Media.PointCollection();
        foreach (var s in _history)
        {
            var p = new Windows.Foundation.Point(X(s.At), h - 3 - s.Percent / 100 * (h - 6));
            charge.Add(p);
            area.Add(p);
        }
        if (_history.Count > 0)
        {
            area.Add(new Windows.Foundation.Point(w, h));
            area.Add(new Windows.Foundation.Point(X(_history[0].At), h));
        }
        ChargeLine.Points = charge;
        ChargeArea.Points = area;

        // in and out, each on its own scale that grows to fit
        DrawWatts(_history.Select(s => (s.At, Math.Max(0, s.Watts))).ToList(), InLine, InArea, InScale);
        DrawWatts(_history.Select(s => (s.At, Math.Max(0, -s.Watts))).ToList(), OutLine, OutArea, OutScale);

        void DrawWatts(List<(DateTime At, double W)> series, Microsoft.UI.Xaml.Shapes.Polyline line, Microsoft.UI.Xaml.Shapes.Polygon fill, TextBlock scale)
        {
            double top = Math.Ceiling(Math.Max(5, series.Count == 0 ? 0 : series.Max(s => s.W)) / 10) * 10;
            var pts = new Microsoft.UI.Xaml.Media.PointCollection();
            var area = new Microsoft.UI.Xaml.Media.PointCollection();
            foreach (var s in series)
            {
                var p = new Windows.Foundation.Point(X(s.At), h - 3 - s.W / top * (h - 6));
                pts.Add(p);
                area.Add(p);
            }
            if (series.Count > 0)
            {
                area.Add(new Windows.Foundation.Point(w, h));
                area.Add(new Windows.Foundation.Point(X(series[0].At), h));
            }
            line.Points = pts;
            fill.Points = area;
            if (scale.Text.Length == 0 || !scale.Text.Contains(" · ")) scale.Text = $"0 to {top:0} W";
        }

        // voltage: a line on a scale that hugs the data, in half-volt steps
        var withVolts = _history.Where(s => s.Volts > 0).ToList();
        var volts = new Microsoft.UI.Xaml.Media.PointCollection();
        if (withVolts.Count > 0)
        {
            double voltSpan = Span(withVolts[0].At);   // its own span: voltage history may start later than the rest
            double lo = Math.Floor(withVolts.Min(s => s.Volts) * 2) / 2, hi = Math.Ceiling(withVolts.Max(s => s.Volts) * 2) / 2;
            if (hi - lo < 0.5) hi = lo + 0.5;
            foreach (var s in withVolts)
                volts.Add(new Windows.Foundation.Point(w - (now - s.At).TotalSeconds / voltSpan * w, h - 3 - (s.Volts - lo) / (hi - lo) * (h - 6)));
            if (VoltsHair.Opacity == 0) VoltsScale.Text = $"{lo:0.0} to {hi:0.0} V";
        }
        VoltsLine.Points = volts;

        if (Settings.ShowStats)
        {
            ChargeStats.Text = StatsLine(_history.Select(s => s.Percent), "%", "0");
            InStats.Text = StatsLine(_history.Where(s => s.Watts > 0).Select(s => s.Watts), "W", "0.0");
            OutStats.Text = StatsLine(_history.Where(s => s.Watts < 0).Select(s => -s.Watts), "W", "0.0");
            VoltsStats.Text = StatsLine(withVolts.Select(s => s.Volts), "V", "0.00");
        }
    }

    /// <summary>Low, average and high for what each graph is showing, in a line under it.</summary>
    private async void SetStats(bool on, bool apply = true)
    {
        Settings.ShowStats = on;
        SlideKnob(StatsKnobSlide, StatsPill, on, apply);
        var lines = new[] { ChargeStats, InStats, OutStats, VoltsStats };
        if (!apply)
        {
            foreach (var line in lines) line.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            return;
        }
        bool graphsShowing = Graphs.Visibility == Visibility.Visible;
        if (on)
        {
            // the lines take their place, then pop in: a quick fade with a small rise
            int before = AppWindow.ClientSize.Height;
            foreach (var line in lines) { ElementCompositionPreview.GetElementVisual(line).Opacity = 0; line.Visibility = Visibility.Visible; }
            if (graphsShowing) DrawGraphs();
            var slide = graphsShowing ? SlideWindowHeight(before, Px(DesignHeight), 160) : Task.CompletedTask;
            await Task.WhenAll(lines.Select(line => PlayOn(ElementCompositionPreview.GetElementVisual(line), 140, ("Opacity", 0f, 1f), ("Translation.Y", 4f, 0f))));
            await slide;
        }
        else
        {
            int before = AppWindow.ClientSize.Height;
            await Task.WhenAll(lines.Select(line => PlayOn(ElementCompositionPreview.GetElementVisual(line), 90, ("Opacity", 1f, 0f), ("Translation.Y", 0f, 4f))));
            foreach (var line in lines) line.Visibility = Visibility.Collapsed;
            if (graphsShowing) await SlideWindowHeight(before, Px(DesignHeight), 140);
        }
    }

    private static string StatsLine(IEnumerable<double> values, string unit, string format)
    {
        var list = values.ToList();
        double low = list.Count == 0 ? 0 : list.Min(), avg = list.Count == 0 ? 0 : list.Average(), high = list.Count == 0 ? 0 : list.Max();
        return $"low {low.ToString(format)} · avg {avg.ToString(format)} · high {high.ToString(format)} {unit}";
    }

    /// <summary>
    /// Hover a chart and the caption on its right shows the value and time under the pointer, with a hairline
    /// at that spot. The caption goes back to the scale when the pointer leaves.
    /// </summary>
    private void ChartHover(Canvas chart, Microsoft.UI.Xaml.Shapes.Rectangle hair, TextBlock caption, Func<(DateTime At, double Percent, double Watts, double Volts), string> label, Func<DateTime?> firstSample)
    {
        string? restore = null;
        chart.PointerMoved += (_, e) =>
        {
            if (firstSample() is not { } first || _history.Count == 0) return;
            double w = chart.ActualWidth;
            var now = DateTime.UtcNow;
            double span = Math.Clamp((now - first).TotalSeconds, 10, HistorySpan.TotalSeconds);
            double x = Math.Clamp(e.GetCurrentPoint(chart).Position.X, 0, w);
            var at = now - TimeSpan.FromSeconds((w - x) / w * span);
            var nearest = _history.MinBy(s => Math.Abs((s.At - at).TotalSeconds));
            restore ??= caption.Text;
            caption.Text = $"{label(nearest)} · {nearest.At.ToLocalTime():t}";
            Canvas.SetLeft(hair, x);
            hair.Opacity = 0.6;
        };
        chart.PointerExited += (_, _) =>
        {
            hair.Opacity = 0;
            if (restore is not null) { caption.Text = restore; restore = null; }
        };
    }

    // ------------------------------------------------------------ the ring mark

    private int _iconPercent = -1;
    private nint _taskbarIcon;

    /// <summary>Clockwise arc from 12 o'clock, as path geometry.</summary>
    private static Microsoft.UI.Xaml.Media.Geometry Arc(double cx, double cy, double radius, double fraction)
    {
        fraction = Math.Clamp(fraction, 0.001, 0.9999);
        double end = fraction * Math.Tau;
        double x = cx + radius * Math.Sin(end), y = cy - radius * Math.Cos(end);
        int large = fraction > 0.5 ? 1 : 0;
        string data = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"M{cx},{cy - radius} A{radius},{radius} 0 {large} 1 {x:0.###},{y:0.###}");
        return (Microsoft.UI.Xaml.Media.Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(typeof(Microsoft.UI.Xaml.Media.Geometry), data);
    }

    /// <summary>The taskbar button shows the same ring, redrawn whenever the whole-number percent changes.</summary>
    private void UpdateTaskbarIcon(int percent)
    {
        if (percent == _iconPercent)
            return;
        _iconPercent = percent;
        nint fresh = TrayIcon.RenderRing(32, percent / 100.0);
        AppWindow.SetIcon(Microsoft.UI.Win32Interop.GetIconIdFromIcon(fresh));
        if (_taskbarIcon != 0)
            TrayIcon.Destroy(_taskbarIcon);
        _taskbarIcon = fresh;
    }

    /// <summary>A short fade-in on the parts that change when charging starts or stops.</summary>
    private void Settle()
    {
        foreach (var element in new UIElement[] { StateRow, InBlock, OutBlock, TimeRow })
        {
            var visual = ElementCompositionPreview.GetElementVisual(element);
            var fade = visual.Compositor.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(0f, 0.15f);
            fade.InsertKeyFrame(1f, 1f, visual.Compositor.CreateCubicBezierEasingFunction(new Vector2(0, 0), new Vector2(0, 1)));
            fade.Duration = TimeSpan.FromMilliseconds(450);
            visual.StartAnimation("Opacity", fade);
        }
    }

    /// <summary>The bar fill is scaled, not resized, so every change glides instead of jumping.</summary>
    private void AnimateBarChanges()
    {
        var visual = ElementCompositionPreview.GetElementVisual(BarFill);
        var compositor = visual.Compositor;
        var glide = compositor.CreateVector3KeyFrameAnimation();
        glide.Target = "Scale";
        glide.InsertExpressionKeyFrame(1f, "this.FinalValue", compositor.CreateCubicBezierEasingFunction(new Vector2(0, 0), new Vector2(0, 1)));
        glide.Duration = TimeSpan.FromMilliseconds(400);
        var implicitAnimations = compositor.CreateImplicitAnimationCollection();
        implicitAnimations["Scale"] = glide;
        visual.ImplicitAnimations = implicitAnimations;
        visual.Scale = new Vector3(0, 1, 1);
    }

    // ------------------------------------------------------------ data → screen

    private DateTime _movingUntil;

    private void Tick()
    {
        if (DateTime.UtcNow < _movingUntil) return;   // mid-drag: leave the frames to Windows
        try
        {
            Show(BatteryReader.Read());
        }
        catch (Exception e)
        {
            StateText.Text = "error: " + e.Message;
        }
    }

    private void Show(Reading? r)
    {
        _reading = r;
        RecordHistory(r);
        if (r is null)
        {
            PercentText.Text = "—";
            StateText.Text = "No battery found"; StateRest.Text = "";
            InText.Text = OutText.Text = TimeText.Text = StoredText.Text = HealthText.Text = VoltageText.Text = "—";
            _samples.Clear();
            _tray.Set("—", "No battery found");
            return;
        }

        var now = DateTime.UtcNow;
        if (r.Watts is double w)
        {
            _samples.Enqueue((now, w));
            while (_samples.Count > 0 && (now - _samples.Peek().At).TotalSeconds > EstimateSeconds)
                _samples.Dequeue();
            _peakIn = Math.Max(_peakIn, w);
            _peakOut = Math.Max(_peakOut, -w);
            if (_statsOnAc != r.OnAc)
            {
                _samples.Clear();                        // the average must not mix charging with draining
                _samples.Enqueue((now, w));
                _shownHours = null;                      // the old time-left belongs to the old state
                if (_statsOnAc is not null) ReactToPlug(r.OnAc);   // a real change, not the first reading
                _statsOnAc = r.OnAc;
            }
        }
        double? avg = Average();
        double? est = Estimate();

        bool low = r.Percent <= 15 && !r.OnAc;
        var color = low ? Brush("Low") : Brush("Ink");
        PercentText.Text = $"{r.Percent:0}%";
        PercentText.Foreground = color;
        BarFill.Fill = color;
        RingFill.Stroke = color;
        int shownPercent = (int)Math.Round(r.Percent);
        if (shownPercent != _iconPercent)
        {
            RingFill.Data = Arc(6, 6, 4.5, r.Percent / 100);                              // geometry only when the number changes
            ElementCompositionPreview.GetElementVisual(BarFill).Scale = new Vector3((float)(r.Percent / 100), 1, 1);
        }
        UpdateTaskbarIcon(shownPercent);
        Bolt.Visibility = r.Charging ? Visibility.Visible : Visibility.Collapsed;
        if (_wasCharging is not null && _wasCharging != r.Charging)
            Settle();   // plugged in or out: let the new numbers fade in instead of snapping
        _wasCharging = r.Charging;

        string state = r.Charging ? "Charging" + (est is { } h1 ? $" · full in {Hours(h1)}" : "")
                     : r.OnAc ? (r.Percent >= 99 ? "Plugged in · full" : "Plugged in · holding")
                     : "On battery" + (est is { } h2 ? $" · {Hours(h2)} left" : "");
        // "Charging" then the bolt, then the rest of the line
        int dot = state.IndexOf(" · ");
        StateText.Text = dot < 0 ? state : state[..dot];
        StateRest.Text = dot < 0 ? "" : state[(dot + 1)..];

        var lit = Brush("Ink");
        var faint = Brush("InkFaint");
        InText.Text = $"{Watts(r.WattsIn)} W";
        InText.Foreground = r.WattsIn > 0 ? Brush("Accent") : faint;
        // On the charger the laptop runs off the wall, so the battery drain is genuinely
        // zero and the laptop's own draw isn't measurable. Show a dash, not a fake number.
        OutText.Text = r.OnAc ? "—" : $"{Watts(r.WattsOut)} W";
        OutText.Foreground = r.WattsOut > 0 ? lit : faint;
        InSub.Text = r.Watts is null ? "not reported" : r.WattsIn > 0 && avg is { } a1 ? $"avg {Watts(a1)} W" : "from charger";
        OutSub.Text = r.Watts is null ? "not reported" : r.OnAc ? "powered by charger" : r.WattsOut > 0 && avg is { } a2 ? $"avg {Watts(-a2)} W" : "to laptop";

        TimeLabel.Text = r.Charging ? "Time to full" : "Time left";
        TimeText.Text = Hours(est);
        StoredText.Text = $"{Wh(r.RemainingMwh)} / {Wh(r.FullMwh)}";
        VoltageText.Text = r.Volts is { } v ? $"{v:0.00} V" : "—";
        HealthText.Text = (r.Health is { } health ? $"{health * 100:0}%" : "—") + (r.Cycles is { } c ? $" · {c} cycles" : "");

        string tip = $"{r.Percent:0}% · {state.ToLowerInvariant()}";
        if (r.WattsIn > 0) tip += $" · in {Watts(r.WattsIn)} W";
        if (r.WattsOut > 0) tip += $" · out {Watts(r.WattsOut)} W";
        _tray.Set($"{r.Percent:0}", tip, low ? 0x007171F8u : 0x00FFFFFFu);
        TaskbarProgress.Show(Hwnd, (int)Math.Round(r.Percent), low);
        KeepOnChargedBar();
    }

    private double? Average(int seconds = AverageSeconds)
    {
        var since = DateTime.UtcNow - TimeSpan.FromSeconds(seconds);
        var recent = _samples.Where(s => s.At >= since).ToList();
        return recent.Count == 0 ? null : recent.Average(s => s.Watts);
    }

    /// <summary>
    /// Hours to empty or to full, from the energy left and a 5 minute average of the draw. Windows' own
    /// number is built on the instantaneous draw and swings wildly, so it isn't used. The shown value is
    /// rounded to 5 minutes and only refreshed every 30 s; only a plug or unplug resets it at once.
    /// </summary>
    private double? Estimate()
    {
        var now = DateTime.UtcNow;
        bool charging = _reading?.Charging == true;
        bool due = _shownHours is null || charging != _shownCharging || now - _shownAt >= TimeSpan.FromSeconds(TimeHoldSeconds);
        if (due)
        {
            double? raw = RawEstimate();
            _shownHours = raw is null ? null : Math.Round(raw.Value * 12) / 12;   // to the nearest 5 minutes
            _shownAt = now;
            _shownCharging = charging;
        }
        return _shownHours;
    }

    private double? RawEstimate()
    {
        if (_reading is not { } r)
            return null;
        if (r.OnAc && (r.Percent >= 100 || !r.Charging))
            return null;   // full, or holding on the charger: there is no "time to full"
        if (r is not { RemainingMwh: int remaining, FullMwh: > 0 } || Average(EstimateSeconds) is not double avg || avg == 0)
            return null;
        if ((avg < 0) != (r.Watts is < 0))
            return null;   // the average still points the other way (just plugged or unplugged); wait for it to settle
        return avg < 0 ? remaining / (-avg * 1000) : (r.FullMwh.Value - remaining) / (avg * 1000);
    }

    // ------------------------------------------------------------ hover cards (built live, one plain sentence each)

    private HoverCards.Card CardPercent()
    {
        if (_reading is not { } r) return new("Charge", "—", ["No battery reading yet."]);
        string line = r.Charging ? "How full the battery is, and it's filling up right now."
                    : r.OnAc ? "How full the battery is. It's full and resting while the laptop runs off the charger."
                    : r.Percent <= 15 ? "How full the battery is. Getting low, plug in soon."
                    : "How full the battery is while you run on it.";
        return new("Charge", $"{r.Percent:0}%", [line]);
    }

    private HoverCards.Card CardIn()
    {
        var r = _reading;
        string line = r is null or { OnAc: false } ? "Power flowing from the charger into the battery. Nothing right now, because no charger is connected."
                    : r.WattsIn == 0 ? "Power flowing from the charger into the battery. Nothing right now, because it's full and the laptop runs straight off the charger."
                    : "Power flowing from the charger into the battery right now. Higher means faster charging, and it slows down on purpose near full.";
        return new("In", $"{Watts(r?.WattsIn)} W", [line]);
    }

    private HoverCards.Card CardOut()
    {
        var r = _reading;
        if (r is { OnAc: true })
            return new("Out", "—", ["Power the laptop is taking from the battery. Nothing while plugged in, because it runs off the charger, and Windows can't measure what comes through the cable."]);
        return new("Out", $"{Watts(r?.WattsOut)} W", ["Power the laptop is using from the battery right now. A bright screen, video calls and heavy work push it up; lower means it lasts longer."]);
    }

    private HoverCards.Card CardTime()
    {
        var r = _reading;
        var est = Estimate();
        if (r is null || est is null)
            return new("Time", "—", ["No countdown right now. Plugged in and full means there's nothing to count down to."]);
        if (r.Charging)
            return new("Time to full", Hours(est), ["How long until the battery is full, at the speed it has been charging over the last five minutes."]);
        return new("Time left", Hours(est), ["How long until the battery is empty if you keep using it the way you have for the last five minutes. A guide, not a promise."]);
    }

    private HoverCards.Card CardStored()
    {
        if (_reading is not { } r) return new("Stored", "—", []);
        return new("Stored energy", Wh(r.RemainingMwh), [$"The actual energy in the battery, out of the {Wh(r.FullMwh)} a full charge holds today. This is the fuel in the tank; the percent is just the gauge."]);
    }

    private HoverCards.Card CardVoltage()
    {
        if (_reading is not { Volts: { } v }) return new("Voltage", "—", ["This battery doesn't report its voltage."]);
        return new("Voltage", $"{v:0.00} V", ["The battery's electrical pressure. It creeps up as the battery fills and sags a little under heavy use; laptop batteries normally sit between about 11 and 17 volts."]);
    }

    private HoverCards.Card CardHealth()
    {
        if (_reading is not { Health: double health, DesignMwh: int design, FullMwh: int full } r)
            return new("Health", "—", ["This battery doesn't report what it held when new."]);
        string cycles = r.Cycles is int c ? $" It has been through {c} full charges' worth of use." : "";
        return new("Health", $"{health * 100:0}%", [$"How much a full charge holds now ({Wh(full)}) compared with when the battery was new ({Wh(design)}). Batteries wear slowly; above about 80% is doing fine.{cycles}"]);
    }

    // ------------------------------------------------------------ helpers

    private static string Watts(double? w) => w is null ? "0.0" : $"{Math.Abs(w.Value):0.0}";
    private static string Wh(int? mwh) => mwh is null ? "—" : $"{mwh.Value / 1000.0:0.0} Wh";

    private static string Hours(double? h)
    {
        if (h is null || h <= 0 || h > 48) return "—";
        int minutes = (int)Math.Round(h.Value * 60);
        return minutes < 60 ? $"{minutes}m" : $"{minutes / 60}h {minutes % 60:00}m";
    }


    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    private int Px(int dip) => (int)Math.Round(dip * GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0);

    private const int StatsLineDip = 18;
    private int DesignHeight => HeightDip + (Graphs.Visibility == Visibility.Visible ? GraphsHeightDip + (Settings.ShowStats ? 4 * StatsLineDip : 0) : 0);

    // ------------------------------------------------------------ drag from anywhere: Windows owns the whole window as a caption

    // The entire client area is declared a caption, so a press anywhere starts Windows' own native drag
    // (smooth, snap layouts, the lot). Passthrough regions punch holes for things that need the pointer:
    // buttons, toggles, hover-card rows, charts, the settings panel and the little guy's strips.
    private Microsoft.UI.Input.InputNonClientPointerSource NonClient => Microsoft.UI.Input.InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
    private readonly List<RectInt32> _extraPassthrough = new();   // strips the party code adds (floor, ground, ladder)

    private void UpdateRegions()
    {
        if (Root.ActualWidth == 0) return;
        var whole = new RectInt32(0, 0, AppWindow.ClientSize.Width, AppWindow.ClientSize.Height);
        NonClient.SetRegionRects(Microsoft.UI.Input.NonClientRegionKind.Caption, [whole]);

        var holes = new List<RectInt32>();
        void Hole(FrameworkElement e) { if (e.Visibility == Visibility.Visible && e.ActualWidth > 0) holes.Add(RectOf(e)); }
        foreach (var e in new FrameworkElement[] { SettingsButton, MinimizeButton, CloseButton, InBlock, OutBlock, TimeRow, StoredRow, VoltageRow, HealthRow })
            Hole(e);
        if (_settingsOpen) Hole(SettingsPanel);   // not the scrim: it covers the window and would block dragging
        if (Graphs.Visibility == Visibility.Visible) foreach (var c in new FrameworkElement[] { ChargeChart, InChart, OutChart, VoltsChart }) Hole(c);
        holes.AddRange(_extraPassthrough);
        NonClient.SetRegionRects(Microsoft.UI.Input.NonClientRegionKind.Passthrough, holes.ToArray());
    }

    /// <summary>An element's box in physical pixels, window-relative, as the non-client API wants it.</summary>
    private RectInt32 RectOf(FrameworkElement e)
    {
        var p = e.TransformToVisual(Root).TransformPoint(new Windows.Foundation.Point(0, 0));
        double s = GetDpiForWindow(Hwnd) / 96.0;
        return new RectInt32((int)(p.X * s), (int)(p.Y * s), (int)Math.Ceiling(e.ActualWidth * s), (int)Math.Ceiling(e.ActualHeight * s));
    }

    private RectInt32 RectOf(double x, double y, double w, double h)
    {
        double s = GetDpiForWindow(Hwnd) / 96.0;
        return new RectInt32((int)(x * s), (int)(y * s), (int)Math.Ceiling(w * s), (int)Math.Ceiling(h * s));
    }


    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);
}
