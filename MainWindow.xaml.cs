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
    private const int HeightDip = 420;   // 412 before the NET row, less the old time row
    private const int AverageSeconds = 30;
    private const int EstimateSeconds = 300;      // time-left uses a 5 minute average of the draw
    private const int TimeHoldSeconds = 30;       // and the shown value only moves every 30 s
    private const int PollMs = 250;   // the driver updates every second or so; polling faster keeps the numbers feeling live

    private readonly TrayIcon _tray;
    private readonly DispatcherQueueTimer _timer;
    private readonly HoverCards _cards;
    private readonly Queue<(DateTime At, double Watts)> _samples = new();
    private readonly Queue<(DateTime At, double? Laptop, double? Charger)> _drawSamples = new();   // for the cards: what the last half minute looked like
    private Reading? _reading;
    private double _peakIn, _peakOut;
    private bool? _statsOnAc;
    private double? _shownHours;
    private DateTime _shownAt;
    private bool _shownCharging;

    private enum Menu { Show, StartWithWindows, Separator, Quit }

    private bool? _wasCharging;
    private string _lastHoursText = "";
    private DateTime _lastChargingAt = DateTime.MinValue;
    private DateTime _topShownAt;
    private bool? _topOnAc;

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
        ChartHover(InChart, InHair, InScale, s => $"{Math.Max(0, s.Draw + s.Watts):0.0} W in", () => _history.Count > 0 ? _history[0].At : (DateTime?)null);
        ChartHover(OutChart, OutHair, OutScale, s => $"{Math.Max(0, s.Draw):0.0} W out", () => _history.Count > 0 ? _history[0].At : (DateTime?)null);
        ChartHover(VoltsChart, VoltsHair, VoltsScale, s => $"{s.Volts:0.00} V", () => _history.FirstOrDefault(s => s.Volts > 0).At is var f && f != default ? f : (DateTime?)null);
        // Everything that ever moves on the compositor gets translation enabled once, up front. Animating
        // Translation on an element before this throws "property cannot be animated" and freezes the sequence.
        foreach (var e in new UIElement[] { Dancer, Ladder, TearL, TearR, SadMouth, Graphs, SettingsPanel, ChargeStats, InStats, OutStats, VoltsStats, StateText, Root })
            ElementCompositionPreview.SetIsTranslationEnabled(e, true);

        _cards = new HoverCards(DispatcherQueue);
        // Cards sit in a column just outside the window's right edge, level with what you're pointing at; if the
        // window is against the right of the screen, the column moves to the left edge instead.
        _cards.Locate = anchor =>
        {
            const double gap = 12;
            var at = anchor.TransformToVisual(Root).TransformPoint(new Windows.Foundation.Point(0, 0));
            double scale = GetDpiForWindow(Hwnd) / 96.0;
            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
            bool right = AppWindow.Position.X + AppWindow.Size.Width + (gap + HoverCards.Width) * scale <= area.X + area.Width;
            double x = right ? Root.ActualWidth - at.X + gap : -at.X - gap;
            return (new Windows.Foundation.Point(x, anchor.ActualHeight / 2), right ? Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Right : Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Left);
        };
        _cards.Attach(PercentBlock, CardPercent);
        _cards.Attach(InBlock, CardIn);
        _cards.Attach(OutBlock, CardOut);
        _cards.Attach(NetBlock, CardNet);
        _cards.Attach(StateRow, CardTime);   // the time lives in the state line now
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
    private readonly List<(DateTime At, double Percent, double Watts, double Volts, double Draw)> _history = new();
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
        _history.Add((now, r.Percent, r.Watts ?? 0, r.Volts ?? 0, r.LaptopWatts ?? r.WattsOut));
        _history.RemoveAll(h => now - h.At > HistorySpan + HistoryStep);
        try
        {
            File.AppendAllText(HistoryFile, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{now:O},{r.Percent:0.0},{r.Watts ?? 0:0.00},{r.Volts ?? 0:0.000},{r.LaptopWatts ?? r.WattsOut:0.00}") + Environment.NewLine);
            if (_history.Count % 360 == 0)   // every half hour: drop anything older than the graph window
                File.WriteAllLines(HistoryFile, _history.Select(h => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{h.At:O},{h.Percent:0.0},{h.Watts:0.00},{h.Volts:0.000},{h.Draw:0.00}")));
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
                    double draw = parts.Length >= 5 && double.TryParse(parts[4], System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : Math.Max(0, -w);   // older files: the battery drain
                    _history.Add((at, pct, w, volts, draw));
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
        DrawWatts(_history.Select(s => (s.At, Math.Max(0, s.Draw + s.Watts))).ToList(), InLine, InArea, InScale);
        DrawWatts(_history.Select(s => (s.At, Math.Max(0, s.Draw))).ToList(), OutLine, OutArea, OutScale);

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
            InStats.Text = StatsLine(_history.Where(s => s.Draw + s.Watts > 0).Select(s => s.Draw + s.Watts), "W", "0.0");
            OutStats.Text = StatsLine(_history.Where(s => s.Draw > 0).Select(s => s.Draw), "W", "0.0");
            VoltsStats.Text = StatsLine(withVolts.Select(s => s.Volts), "V", "0.00");
        }
    }

    /// <summary>
    /// Low, average and high for what each graph is showing, in a line under it. On and off are mirror images:
    /// the lines fade and drift while their height opens or closes and the window edge slides, all in one beat,
    /// so nothing ever snaps.
    /// </summary>
    private async void SetStats(bool on, bool apply = true)
    {
        Settings.ShowStats = on;
        SlideKnob(StatsKnobSlide, StatsPill, on, apply);
        var lines = new[] { ChargeStats, InStats, OutStats, VoltsStats };
        bool graphsShowing = Graphs.Visibility == Visibility.Visible;
        if (!apply || !graphsShowing)
        {
            foreach (var line in lines) { line.Visibility = on ? Visibility.Visible : Visibility.Collapsed; line.Height = StatsLineDip; }
            return;
        }
        const int ms = 180;
        int before = AppWindow.ClientSize.Height;
        if (on)
        {
            foreach (var line in lines) { ElementCompositionPreview.GetElementVisual(line).Opacity = 0; line.Height = 0; line.Visibility = Visibility.Visible; }
            DrawGraphs();
            var slide = SlideWindowHeight(before, Px(DesignHeight), ms);
            var open = AnimateHeight(lines, 0, StatsLineDip, ms);
            await Task.WhenAll(lines.Select(line => PlayOn(ElementCompositionPreview.GetElementVisual(line), ms, ("Opacity", 0f, 1f), ("Translation.Y", 4f, 0f))));
            await Task.WhenAll(slide, open);
        }
        else
        {
            var slide = SlideWindowHeight(before, Px(DesignHeight), ms);
            var close = AnimateHeight(lines, StatsLineDip, 0, ms);
            await Task.WhenAll(lines.Select(line => PlayOn(ElementCompositionPreview.GetElementVisual(line), ms, ("Opacity", 1f, 0f), ("Translation.Y", 0f, 4f))));
            await Task.WhenAll(slide, close);
            foreach (var line in lines) { line.Visibility = Visibility.Collapsed; line.Height = StatsLineDip; }
        }
        UpdateRegions();
    }

    /// <summary>Animate the layout height of some elements, eased the same way the window edge is.</summary>
    private static Task AnimateHeight(IEnumerable<FrameworkElement> elements, double from, double to, int ms)
    {
        var done = new TaskCompletionSource();
        var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        var ease = new Microsoft.UI.Xaml.Media.Animation.CubicEase { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut };
        foreach (var e in elements)
        {
            var a = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation { From = from, To = to, Duration = TimeSpan.FromMilliseconds(ms), EasingFunction = ease, EnableDependentAnimation = true };
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(a, e);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(a, "Height");
            sb.Children.Add(a);
        }
        sb.Completed += (_, _) => done.TrySetResult();
        sb.Begin();
        return done.Task;
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
    private void ChartHover(Canvas chart, Microsoft.UI.Xaml.Shapes.Rectangle hair, TextBlock caption, Func<(DateTime At, double Percent, double Watts, double Volts, double Draw), string> label, Func<DateTime?> firstSample)
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

    /// <summary>The taskbar button wears the app's own icon, the blue rounded tile from the Store; the level lives in the tray and the title bar ring.</summary>
    private void UpdateTaskbarIcon(int percent)
    {
        if (_iconPercent != -1)
            return;   // set once
        _iconPercent = percent;
        // The icon built into the exe itself (ApplicationIcon in the project), so packaged and plain builds agree.
        nint icon = LoadImageW(GetModuleHandleW(null), 32512, 1 /* IMAGE_ICON */, 0, 0, 0x8000 /* LR_SHARED */ | 0x40 /* LR_DEFAULTSIZE */);
        if (icon != 0)
            AppWindow.SetIcon(Microsoft.UI.Win32Interop.GetIconIdFromIcon(icon));
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint LoadImageW(nint module, nint name, uint type, int cx, int cy, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandleW(string? name);

    /// <summary>A soft arrival: the element fades up from half and rises a couple of pixels into place.</summary>
    private static void SettleIn(UIElement element)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var ease = visual.Compositor.CreateCubicBezierEasingFunction(new Vector2(0, 0), new Vector2(0, 1));
        var fade = visual.Compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0f, 0.4f);
        fade.InsertKeyFrame(1f, 1f, ease);
        fade.Duration = TimeSpan.FromMilliseconds(320);
        var rise = visual.Compositor.CreateScalarKeyFrameAnimation();
        rise.InsertKeyFrame(0f, 3f);
        rise.InsertKeyFrame(1f, 0f, ease);
        rise.Duration = TimeSpan.FromMilliseconds(320);
        visual.StartAnimation("Opacity", fade);
        visual.StartAnimation("Translation.Y", rise);
    }

    /// <summary>A short fade-in on the parts that change when charging starts or stops.</summary>
    private void Settle()
    {
        foreach (var element in new UIElement[] { StateRow, InBlock, OutBlock, NetBlock })
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

    private bool _readInFlight;
    private DateTime _diagnosticsAt = DateTime.MinValue;   // the same text as Copy diagnostics, kept fresh in the data folder for support

    /// <summary>
    /// The read happens off the UI thread. Asking the battery driver is normally instant, but around a plug or
    /// unplug the firmware is busy and one answer can take most of a second; on the UI thread that froze the
    /// window and every frame-driven motion in it. One read in flight at a time; a slow one just skips ticks.
    /// </summary>
    private async void Tick()
    {
        if (DateTime.UtcNow < _movingUntil || _held || _readInFlight) return;   // mid-drag of the window or of him: leave the frames alone
        _readInFlight = true;
        Reading? r;
        try
        {
            r = await Task.Run(BatteryReader.Read);
        }
        catch (Exception e)
        {
            _readInFlight = false;
            StateText.Text = "error: " + e.Message;
            return;
        }
        _readInFlight = false;
        if (DateTime.UtcNow < _movingUntil || _held) return;
        try { Show(r); }
        catch (Exception e) { StateText.Text = "error: " + e.Message; }
        if ((DateTime.UtcNow - _diagnosticsAt).TotalSeconds >= 60)
        {
            _diagnosticsAt = DateTime.UtcNow;
            try { File.WriteAllText(Path.Combine(Settings.DataFolder, "diagnostics.txt"), BatteryReader.Diagnostics()); } catch { }
        }
    }

    private void Show(Reading? r)
    {
        _reading = r;
        if (r is not null)
        {
            var t = DateTime.UtcNow;
            _drawSamples.Enqueue((t, r.LaptopWatts, r.OnAc ? r.ChargerWatts : 0));
            while (_drawSamples.Count > 0 && (t - _drawSamples.Peek().At).TotalSeconds > AverageSeconds) _drawSamples.Dequeue();
        }
        RecordHistory(r);
        if (r is null)
        {
            PercentText.Text = "—";
            StateText.Text = "No battery found";
            InText.Text = OutText.Text = NetText.Text = StoredText.Text = HealthText.Text = VoltageText.Text = "—";
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

        PercentText.Text = $"{r.Percent:0}%";
        int shownPercent = (int)Math.Round(r.Percent);
        if (shownPercent != _iconPercent)
        {
            RingFill.Data = Arc(6, 6, 4.5, r.Percent / 100);                              // geometry only when the number changes
            ElementCompositionPreview.GetElementVisual(BarFill).Scale = new Vector3((float)(r.Percent / 100), 1, 1);
        }
        UpdateTaskbarIcon(shownPercent);
        if (_wasCharging is not null && _wasCharging != r.Charging)
            Settle();   // plugged in or out: let the new numbers fade in instead of snapping
        _wasCharging = r.Charging;

        // While the estimate is still settling the slot shows a dash, so the line keeps its shape; when the
        // number arrives the line eases in rather than flicking over.
        string hoursText = Hours(est);
        if (_lastHoursText == "—" && hoursText != "—")
            SettleIn(StateText);
        _lastHoursText = hoursText;
        // A single zero-rate read mid-charge must not flick the line to "holding" and back: charging sticks for a few seconds
        if (r.Charging) _lastChargingAt = now;
        bool charging = r.Charging || (r.OnAc && r.Percent < 100 && (now - _lastChargingAt).TotalSeconds < 4);
        string state = charging ? $"Charging · full in {Hours(est)}"
                     : r.Draining ? $"Plugged in · draining · {Hours(est)} left"
                     : r.OnAc ? (r.Percent >= 99 ? "Plugged in · full" : r.ChargerTooWeak ? "Plugged in · weak charger" : "Plugged in · holding")
                     : $"On battery · {Hours(est)} left";
        // "Charging", then the bolt as part of the same line of text, then the rest
        int dot = state.IndexOf(" · ");
        StateText.Inlines.Clear();
        StateText.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = dot < 0 ? state : state[..dot] });
        if (charging)
            StateText.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = " ", FontFamily = new FontFamily("Segoe Fluent Icons,Segoe MDL2 Assets"), FontSize = 9.5 });
        if (dot >= 0)
            StateText.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = " " + state[(dot + 1)..] });

        var lit = Brush("Ink");
        var faint = Brush("InkFaint");
        // A battery that reports no rate shows a dash, never a made-up zero, and the caption says why.
        // IN is what the charger puts out, OUT is what the laptop uses, NET beneath them is what the battery
        // sees: IN minus OUT, positive filling, negative emptying. NET is the one number the battery reports
        // directly; on the charger OUT is the processor meter plus the learned overhead, and IN is OUT plus NET.
        // The row refreshes once a second so it reads like a gauge, not a ticker; a plug change refreshes it at once.
        bool plugChanged = _wasCharging is not null && _wasCharging != r.Charging || _topOnAc != r.OnAc;
        if (plugChanged || (now - _topShownAt).TotalMilliseconds >= 1000)
        {
            _topShownAt = now;
            _topOnAc = r.OnAc;
            string? noRate = r.Watts is not null ? null : r.Source == WattsSource.Measuring ? "measuring…" : "not reported";
            double? laptop = r.LaptopWatts;
            double? charger = !r.OnAc ? 0 : r.ChargerWatts;
            string? noEstimate = !r.OnAc || laptop is not null ? null : r.Draw == DrawState.Calibrating ? "estimating…" : "not available";
            string estTag = r.Source == WattsSource.Estimated ? "est. " : "";

            InText.Text = charger is { } cw ? $"{cw:0.0} W" : "—";
            InText.Foreground = charger > 0 ? lit : faint;
            InSub.Text = !r.OnAc ? "unplugged" : noEstimate ?? "charger";

            OutText.Text = laptop is { } lw ? $"{lw:0.0} W" : "—";
            OutText.Foreground = laptop > 0 ? lit : faint;
            OutSub.Text = !r.OnAc ? (noRate ?? $"{estTag}laptop") : noEstimate ?? "est. laptop";

            NetText.Text = r.Watts is { } nw ? $"{nw:0.0;-0.0;0.0} W" : "—";
            NetText.Foreground = r.Watts is not (null or 0) ? lit : faint;
            NetSub.Text = noRate ?? (estTag.Length > 0 ? "est." : "");   // the sign says which way; the state line says the rest
        }
        double? tipLaptop = r.LaptopWatts, tipCharger = !r.OnAc ? 0 : r.ChargerWatts;

        StoredText.Text = $"{Wh(r.RemainingMwh)} / {Wh(r.FullMwh)}";
        VoltageText.Text = r.Volts is { } v ? $"{v:0.00} V" : "—";
        HealthText.Text = (r.Health is { } health ? $"{health * 100:0}%" : "—") + (r.Cycles is { } c ? $" · {c} cycles" : "");

        string tip = $"{r.Percent:0}% · {state.ToLowerInvariant()}";
        if (tipCharger > 0) tip += $" · in {tipCharger:0.0} W";
        if (tipLaptop > 0) tip += $" · out {tipLaptop:0.0} W";
        if (r.Watts is { } tn && tn != 0) tip += $" · net {tn:0.0;-0.0} W";
        _tray.Set($"{r.Percent:0}", tip);
        TaskbarProgress.Show(Hwnd, (int)Math.Round(r.Percent));
        KeepOnChargedBar();
    }

    private double? AverageLaptop() { var v = _drawSamples.Where(s => s.Laptop is not null).Select(s => s.Laptop!.Value).ToList(); return v.Count == 0 ? null : v.Average(); }
    private double? AverageCharger() { var v = _drawSamples.Where(s => s.Charger is not null).Select(s => s.Charger!.Value).ToList(); return v.Count == 0 ? null : v.Average(); }

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
            // to the nearest 5 minutes; a first reading of a trickle can imply days, which is no estimate at all: drop it and try again next tick
            _shownHours = raw is null or <= 0 or > 48 ? null : Math.Round(raw.Value * 12) / 12;
            _shownAt = now;
            _shownCharging = charging;
        }
        return _shownHours;
    }

    private double? RawEstimate()
    {
        if (_reading is not { } r)
            return null;
        if (r.OnAc && (r.Percent >= 100 || !r.Charging) && !r.Draining)
            return null;   // full, or holding on the charger: there is no "time to full" (draining on a weak charger still counts down)
        if (r.Watts is null && r.PercentPerHour is { } pph && pph != 0)
            return r.Charging ? (pph > 0 ? (100 - r.Percent) / pph : null) : (pph < 0 ? r.Percent / -pph : null);   // no watts at all: the percent slope still gives a time
        if (_samples.Count == 0 || (DateTime.UtcNow - _samples.Peek().At).TotalSeconds < 10)
            return null;   // one or two readings are not an average: wait for ten seconds of them
        if (r is not { RemainingMwh: int remaining, FullMwh: > 0 } || Average(EstimateSeconds) is not double avg || avg == 0)
            return null;
        if ((avg < 0) != (r.Watts is < 0))
            return null;   // the average still points the other way (just plugged or unplugged); wait for it to settle
        return avg < 0 ? remaining / (-avg * 1000) : (r.FullMwh.Value - remaining) / (avg * 1000);
    }

    // ------------------------------------------------------------ hover cards (built live, one plain sentence each)

    // Every card: what the number is and what's happening, in plain words, two sentences.
    // Quoted watts are averages over the last half minute, so the words describe the situation, not one blink of it.

    private static string About(double? w) => w is { } v ? $"about {Math.Abs(v):0} W" : "—";

    private HoverCards.Card CardPercent()
    {
        if (_reading is not { } r) return new("Charge", "—", ["How full the battery is."]);
        string now = r.Charging ? "It's filling up right now."
                   : r.Draining ? "It's going down even with the charger in."
                   : r.OnAc ? "It's full, and the laptop is running off the charger."
                   : r.Percent <= 15 ? "Getting low. Plug in soon."
                   : "The laptop is running on it.";
        return new("Charge", $"{r.Percent:0}%", [$"How full the battery is. {now}"]);
    }

    private static string SourceNote(Reading? r) => r?.Source switch
    {
        WattsSource.Estimated => " This battery doesn't report it directly, so it's worked out from how fast the level moves.",
        WattsSource.Measuring => " This battery doesn't report it directly, so it's being measured. Give it a few minutes.",
        WattsSource.Unavailable => " This battery can't report it.",
        _ => ""
    };

    private static string Learning(Reading r) => r.Draw == DrawState.Calibrating
        ? "Not yet: it needs a few minutes on battery first to learn this laptop. Unplug once and it'll know from then on."
        : "This laptop can't report it. NET still shows whether the charger is keeping up.";

    private HoverCards.Card CardIn()
    {
        var r = _reading;
        const string what = "Energy coming in from the charger. ";
        if (r is null or { OnAc: false })
            return new("In", "0 W", [what + "None right now: nothing is plugged in."]);
        if (AverageCharger() is { } charger && AverageLaptop() is { } laptop && Average() is { } net)
        {
            string split = net > 0.5 ? $"{About(laptop)} runs the laptop and {About(net)} fills the battery."
                         : net < -0.5 ? $"not enough, so the battery is chipping in {About(net)}."
                         : "all of it runs the laptop, since the battery is full.";
            return new("In", About(charger), [what + $"Lately {About(charger)}: {split}"]);
        }
        return new("In", "—", [what + Learning(r)]);
    }

    private HoverCards.Card CardOut()
    {
        var r = _reading;
        const string what = "Energy the laptop is using. ";
        if (r is null)
            return new("Out", "—", [what]);
        if (!r.OnAc)
            return new("Out", About(AverageLaptop()), [what + $"Lately {About(AverageLaptop())}, all of it from the battery. A bright screen and heavy work push it up." + SourceNote(r)]);
        if (AverageLaptop() is { } laptop)
            return new("Out", About(laptop), [what + $"Lately {About(laptop)}, estimated while plugged in. A bright screen and heavy work push it up."]);
        return new("Out", "—", [what + Learning(r)]);
    }

    private HoverCards.Card CardNet()
    {
        var r = _reading;
        const string what = "In minus out: what's left for the battery. ";
        if (r is null || r.Watts is null)
            return new("Net", "—", [what + "No reading right now." + SourceNote(r)]);
        double? avg = Average();
        string now = !r.OnAc ? $"Nothing is coming in, so the battery is giving the laptop {About(avg)}."
                   : avg > 0.5 ? $"Lately {About(avg)} is going into the battery, so the charger is keeping up."
                   : avg < -0.5 ? $"Lately {About(avg)} is coming out of the battery even with the charger in, so this charger isn't keeping up."
                   : "Nothing in or out: the battery is full.";
        return new("Net", avg is { } a ? $"{a:0;-0;0} W" : "—", [what + now + SourceNote(r)]);
    }

    private HoverCards.Card CardTime()
    {
        var r = _reading;
        var est = Estimate();
        if (r is null || est is null)
            return new("Time", "—", ["Nothing to count down yet. Give it a few seconds."]);
        if (r.Charging)
            return new("Time to full", Hours(est), ["How long until the battery is full, at the pace of the last few minutes."]);
        if (r.Draining)
            return new("Time left", Hours(est), ["How long until the battery is empty, even with the charger in, at the pace of the last few minutes."]);
        return new("Time left", Hours(est), ["How long the battery will last if you keep going like the last few minutes. A guide, not a promise."]);
    }

    private HoverCards.Card CardStored()
    {
        if (_reading is not { } r) return new("Stored", "—", ["The energy in the battery right now."]);
        return new("Stored", Wh(r.RemainingMwh), [$"The energy in the battery right now, out of the {Wh(r.FullMwh)} a full charge holds. The percent is just this as a gauge."]);
    }

    private HoverCards.Card CardVoltage()
    {
        if (_reading is not { Volts: { } v }) return new("Voltage", "—", ["The battery's voltage. This one doesn't report it."]);
        return new("Voltage", $"{v:0.00} V", ["The battery's voltage. It rises as the battery fills and dips under heavy use."]);
    }

    private HoverCards.Card CardHealth()
    {
        if (_reading is not { Health: double health, DesignMwh: int design, FullMwh: int full } r)
            return new("Health", "—", ["What a full charge holds now versus when the battery was new. This one doesn't report it."]);
        string cycles = r.Cycles is int c ? $" {c} charge cycles so far." : "";
        return new("Health", $"{health * 100:0}%", [$"What a full charge holds now versus when the battery was new. Above 80% is fine.{cycles}"]);
    }

    // ------------------------------------------------------------ helpers

    private static string Watts(double? w) => w is null ? "—" : $"{Math.Abs(w.Value):0.0}";
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

    // ------------------------------------------------------------ drag by the title bar: Windows owns that strip as a caption

    // Only the title bar is a caption, so a press there starts Windows' own native drag (smooth, snap layouts, the
    // lot) and everything below it is ordinary client area with ordinary input. The passthrough holes are for the
    // three caption buttons, which need the pointer themselves.
    private Microsoft.UI.Input.InputNonClientPointerSource NonClient => Microsoft.UI.Input.InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
    private readonly List<RectInt32> _extraPassthrough = new();   // strips the party code adds (floor, ground, ladder)
    private void UpdateRegions()
    {
        if (Root.ActualWidth == 0) return;
        NonClient.SetRegionRects(Microsoft.UI.Input.NonClientRegionKind.Caption, [RectOf(AppTitleBar)]);

        var holes = new List<RectInt32>();
        void Hole(FrameworkElement e) { if (e.Visibility == Visibility.Visible && e.ActualWidth > 0) holes.Add(RectOf(e)); }
        foreach (var e in new FrameworkElement[] { SettingsButton, MinimizeButton, CloseButton })
            Hole(e);
        if (_settingsOpen) Hole(SettingsPanel);   // its top overlaps the title bar strip
        holes.AddRange(_extraPassthrough);        // the little guy's strips, in case one crosses the title bar
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
