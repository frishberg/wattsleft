using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.Graphics;

namespace BatteryChecker;

/// <summary>
/// Party mode. The little guy lives inside the toggle. Switch it on and he's
/// pushed out of its left side, drops straight down onto the bar and dances
/// on it. Switch it off and he walks back under the door, a ladder grows,
/// he climbs up and slips back in.
///
/// You can pick him up and drop him anywhere. Over the bar he lands on it.
/// Anywhere else he falls to the bottom of the window, walks to a spot under
/// the bar and ladders back up. Grab him off a ladder and he falls too.
///
/// One sequence runs at a time (<see cref="_sequence"/>); a grab cancels it.
/// Limbs are storyboards on joint angles; the body moves on the compositor.
/// </summary>
public sealed partial class MainWindow
{
    private const float BodyW = 26, BodyH = 28;

    private CancellationTokenSource? _sequence;   // whatever he's doing right now: dancing, entering, exiting, falling, climbing
    private bool _settingsOpen;
    private bool _held;
    private float _x;                             // where he stands, window coordinates
    private bool _facingLeft;
    private Point _grabOffset;

    private Visual Body => ElementCompositionPreview.GetElementVisual(Dancer);
    private Compositor Compositor => Body.Compositor;
    private bool Busy => _sequence is { IsCancellationRequested: false } && !_dancing;
    private bool _dancing;

    // ------------------------------------------------------------ settings panel (in-window so he can come and go from it)

    /// <summary>The panel shakes its head: a quick side-to-side that dies out, the way a wrong password does.</summary>
    private void ShakeNo()
    {
        var panel = ElementCompositionPreview.GetElementVisual(SettingsPanel);
        var shake = Compositor.CreateScalarKeyFrameAnimation();
        shake.InsertKeyFrame(0.00f, 0f);
        shake.InsertKeyFrame(0.15f, -9f, Smooth);
        shake.InsertKeyFrame(0.35f, 7f, Smooth);
        shake.InsertKeyFrame(0.55f, -5f, Smooth);
        shake.InsertKeyFrame(0.75f, 3f, Smooth);
        shake.InsertKeyFrame(1.00f, 0f, Smooth);
        shake.Duration = TimeSpan.FromMilliseconds(380);
        panel.StartAnimation("Translation.X", shake);
    }

    private void OpenSettings()
    {
        if (_settingsOpen) { CloseSettings(); return; }
        _settingsOpen = true;
        Scrim.Visibility = Visibility.Visible;
        SettingsPanel.Visibility = Visibility.Visible;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, UpdateRegions);
        var panel = ElementCompositionPreview.GetElementVisual(SettingsPanel);
        ElementCompositionPreview.SetIsTranslationEnabled(SettingsPanel, true);
        _ = Play(180, (panel, "Opacity", Scalar(Enter, (0, 0), (1, 1))), (panel, "Translation.Y", Scalar(Enter, (0, -8), (1, 0))));
    }

    /// <summary>He's on his way home (or about to be): the panel is his door, so it stays until he's through it.</summary>
    private bool GoingHome => _exiting || _pendingExit;

    private async void CloseSettings()
    {
        if (!_settingsOpen) return;
        if (GoingHome) { ShakeNo(); return; }
        _settingsOpen = false;
        Scrim.Visibility = Visibility.Collapsed;
        var panel = ElementCompositionPreview.GetElementVisual(SettingsPanel);
        await Play(160, (panel, "Opacity", Scalar(Exit, (1, 0))), (panel, "Translation.Y", Scalar(Exit, (1, -8))));   // the entrance, backwards
        if (!_settingsOpen) SettingsPanel.Visibility = Visibility.Collapsed;
        UpdateRegions();
    }

    // ------------------------------------------------------------ the toggle

    private bool _pendingExit;   // toggled off mid-sequence: he finishes what he's doing, then goes home
    private bool _exiting;       // the walk-home-and-climb-in is playing

    private async void SetParty(bool on, bool animate = true)
    {
        Log($"toggle {(on ? "on" : "off")} busy={Busy} held={_held}");
        if (Busy || _held)
        {
            // He's mid-fall, mid-climb, or in your hand. The toggle still flips; the exit waits its turn.
            Settings.PartyMode = on;
            SlideKnob(on, animate);
            _pendingExit = !on;
            return;
        }
        _pendingExit = false;
        Settings.PartyMode = on;
        SlideKnob(on, animate);

        if (!animate)   // restoring at launch: he's already out, no ceremony
        {
            if (on) { Stand(FloorLeft() + 20); Dancer.Visibility = Visibility.Visible; StartDancing(); }
            return;
        }

        var ct = Begin();
        try { if (on) await EnterAsync(ct); else await ExitAsync(ct); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Recover(ex); }
        finally { Done(ct); }
    }

    /// <summary>The queued exit: runs once whatever he was doing has put him back on the bar.</summary>
    private async Task LeaveAsync()
    {
        var ct = Begin();
        try { await ExitAsync(ct); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Recover(ex); }
        finally { Done(ct); }
    }

    /// <summary>A sequence finished on its own: nothing is running now, unless something newer took over.</summary>
    private void Done(CancellationToken ct)
    {
        if (_sequence is not null && _sequence.Token == ct && !_dancing)
            _sequence = null;
    }

    private void SlideKnob(bool on, bool animate) => SlideKnob(PartyKnobSlide, PartyPill, on, animate);

    /// <summary>Our toggle: the blue knob slides along the white pill, which brightens when on.</summary>
    private static void SlideKnob(TranslateTransform knob, Microsoft.UI.Xaml.Controls.Border pill, bool on, bool animate)
    {
        var slide = new DoubleAnimation { To = on ? 18 : 0, Duration = TimeSpan.FromMilliseconds(animate ? 180 : 0), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        Storyboard.SetTarget(slide, knob);
        Storyboard.SetTargetProperty(slide, "X");
        var sb = new Storyboard();
        sb.Children.Add(slide);
        sb.Begin();
        pill.Opacity = on ? 1 : 0.55;
    }

    /// <summary>Something threw mid-sequence: note it, put him somewhere sane, and carry on. Never leave him frozen.</summary>
    private void Recover(Exception ex)
    {
        Log("recover: " + ex.GetType().Name + ": " + ex.Message);
        try
        {
            HideLadder();
            if (Settings.PartyMode) { Stand(Math.Clamp(_x, FloorLeft(), FloorRight())); Dancer.Visibility = Visibility.Visible; StartDancing(); }
            else Dancer.Visibility = Visibility.Collapsed;
        }
        catch (Exception inner) { Log("recover failed: " + inner.Message); }
    }

    private static readonly string PartyLog = Path.Combine(Settings.DataFolder, "party.log");
    private static void Log(string line)
    {
        try { File.AppendAllText(PartyLog, $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}"); } catch { }
    }

    private bool _pausedDancing;

    /// <summary>Window hidden: no point animating. Remember whether he was dancing.</summary>
    private void PauseParty()
    {
        _pausedDancing = _dancing;
        if (_dancing) { Begin(); StandStill(); }
    }

    private void ResumeParty()
    {
        if (_pausedDancing && Settings.PartyMode && !_held) StartDancing();
        _pausedDancing = false;
    }

    /// <summary>Cancel whatever he was doing and start a new sequence.</summary>
    private CancellationToken Begin()
    {
        Log("begin (cancels previous: " + (_sequence is { IsCancellationRequested: false }) + ")");
        _sequence?.Cancel();
        _sequence = new CancellationTokenSource();
        _dancing = false;
        return _sequence.Token;
    }

    // ------------------------------------------------------------ places (window coordinates)

    private Point InRoot(FrameworkElement e) => e.TransformToVisual(Root).TransformPoint(new Point(0, 0));
    private float FloorY() => (float)(InRoot(BarTrack).Y - BodyH + 1);                  // feet on the bar
    private float FloorLeft() => (float)InRoot(BarTrack).X;
    /// <summary>The dance floor is the charged part of the bar only: it ends where the fill ends.</summary>
    private float FloorRight()
    {
        double fill = BarTrack.ActualWidth * (_reading?.Percent ?? 0) / 100;
        return (float)Math.Max(FloorLeft(), InRoot(BarTrack).X + fill - BodyW);
    }
    private float GroundY() => (float)(Root.ActualHeight - BodyH);                       // feet on the very bottom edge of the window
    private float DoorX() => (float)(InRoot(PartyToggle).X + 2);                          // the left edge of the toggle: the way in
    private float ExitDoorX() => (float)(InRoot(PartyToggle).X + PartyToggle.ActualWidth - 2);   // the right edge: the way out
    private float DoorY() => (float)(InRoot(PartyToggle).Y + PartyToggle.ActualHeight - BodyH + 2);
    private float UnderDoorX() => DoorX() - BodyW / 2;                                       // the spot right under the door
    private float HomeX() => Math.Clamp(UnderDoorX(), FloorLeft(), FloorRight());          // the closest he can stand to it
    /// <summary>Where he visibly is right now. Stops his body animations first, because a running animation's
    /// stored value is its target, not the frame on screen; stopping leaves the on-screen value behind.</summary>
    private Vector3 Freeze()
    {
        Body.StopAnimation("Translation.X"); Body.StopAnimation("Translation.Y"); Body.StopAnimation("RotationAngleInDegrees"); Body.StopAnimation("Scale");
        return Where();
    }

    private Vector3 Where() => Body.Properties.TryGetVector3("Translation", out var t) == CompositionGetValueStatus.Succeeded ? t : new Vector3(_x, FloorY(), 0);

    private void Stand(float x)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(Dancer, true);
        Body.CenterPoint = new Vector3(BodyW / 2, BodyH, 0);   // pivot at the feet
        Body.Properties.InsertVector3("Translation", new Vector3(x, FloorY(), 0));
        Body.Scale = Vector3.One;
        Body.Opacity = 1;
        Body.RotationAngleInDegrees = 0;
        _x = x;
        _facingLeft = false;
    }

    /// <summary>He never turns round. He's symmetrical enough to walk either way, and it keeps his arms honest.</summary>
    private void Face(bool left) => _facingLeft = left;

    // ------------------------------------------------------------ picking him up

    private void Dancer_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;
        Begin();                                  // stops dancing, entering, exiting, climbing, anything
        _held = true;
        PassthroughFor(RectOf(0, 0, Root.ActualWidth, Root.ActualHeight));
        Dancer.CapturePointer(e.Pointer);
        // Where inside his box the pointer landed, straight from the hit test, so whatever he was doing
        // (dancing, climbing, mid-hop) he stays exactly under the cursor from the first frame.
        var local = e.GetCurrentPoint(Dancer).Position;
        var p = e.GetCurrentPoint(Root).Position;
        Freeze();
        _grabOffset = new Point(Math.Clamp(local.X, 0, BodyW), Math.Clamp(local.Y, 0, BodyH));
        Body.Properties.InsertVector3("Translation", new Vector3((float)(p.X - _grabOffset.X), (float)(p.Y - _grabOffset.Y), 0));
        Body.Opacity = 1;
        Body.CenterPoint = new Vector3(BodyW / 2, 6, 0);       // held by the scruff: he pivots at the head
        HideLadder();
        if (!Settings.PartyMode) { Settings.PartyMode = true; SlideKnob(true, true); }   // grabbed mid-exit: he's out again
        _pendingExit = false;
        _ = Limbs(160, 150, -150, 8, -8);                      // arms up, legs dangling
        e.Handled = true;
    }

    private void Dancer_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_held) return;
        var p = e.GetCurrentPoint(Root).Position;
        var was = Where();
        float x = (float)(p.X - _grabOffset.X), y = (float)(p.Y - _grabOffset.Y);
        Body.Properties.InsertVector3("Translation", new Vector3(x, y, 0));
        float swing = Math.Clamp((x - was.X) * 2.5f, -35, 35);  // he swings with the motion
        Body.StartAnimation("RotationAngleInDegrees", Scalar(Smooth, (1, swing)).With(120));
        e.Handled = true;
    }

    private async void Dancer_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_held) return;
        _held = false;
        Dancer.ReleasePointerCaptures();
        e.Handled = true;
        var ct = Begin();
        try { await DropAsync(ct); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Recover(ex); }
        finally { Done(ct); }
    }

    /// <summary>Let go: over the bar he lands on it; anywhere else he falls to the bottom and ladders back up.</summary>
    private async Task DropAsync(CancellationToken ct)
    {
        var at = Where();
        Body.CenterPoint = new Vector3(BodyW / 2, BodyH, 0);
        bool overBar = at.X >= FloorLeft() - 4 && at.X <= FloorRight() + 4 && at.Y <= FloorY() + 2;
        if (overBar)
        {
            await FallTo(at.X, FloorY(), ct);
            _x = at.X;
            StartDancing();
            return;
        }

        await TumbleAsync(Math.Clamp(at.X, 0, (float)Root.ActualWidth - BodyW), ct);
    }

    /// <summary>Fall to the bottom of the window, walk to a random spot under the charged part, ladder up, dance.</summary>
    private async Task TumbleAsync(float x, CancellationToken ct)
    {
        Body.CenterPoint = new Vector3(BodyW / 2, BodyH, 0);
        Log($"tumble from x={x:0} to ground y={GroundY():0}");
        PassthroughFor(FloorStrip(), GroundStrip(), RectOf(x - 12, 0, BodyW + 24, Root.ActualHeight));   // the whole column he falls through
        await FallTo(x, GroundY(), ct);
        PassthroughFor(FloorStrip(), GroundStrip());
        _x = x;
        float target = FloorLeft() + (float)(Random.Shared.NextDouble() * (FloorRight() - FloorLeft()));
        Log($"walk to x={target:0} on ground y={GroundY():0}");
        await WalkTo(target, GroundY(), ct);
        Log($"climb from y={GroundY():0} to y={FloorY():0}");
        PassthroughFor(FloorStrip(), GroundStrip(), LadderStrip(GroundY(), FloorY()));
        await ClimbAsync(GroundY(), FloorY(), ct);
        Log("on the bar, dancing");
        StartDancing();
    }

    /// <summary>The window got shorter: if he's now below its bottom edge, he lands on the new ground and ladders back up.</summary>
    private async void GroundMoved()
    {
        await Task.Delay(200);   // after the shrink animation
        var at = Where();
        if (Dancer.Visibility != Visibility.Visible || _held || at.Y <= GroundY() + 1) return;
        Log($"ground moved: he was at y={at.Y:0}, ground is now {GroundY():0}");
        var ct = Begin();
        try { Body.Properties.InsertVector3("Translation", new Vector3(at.X, GroundY() - 40, 0)); await TumbleAsync(at.X, ct); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Recover(ex); }
        finally { Done(ct); }
    }

    /// <summary>Called every reading: if the charge shrank out from under him, he falls off the end.</summary>
    private async void KeepOnChargedBar()
    {
        if (!_dancing || _held || _x <= FloorRight() + 3)
            return;
        var ct = Begin();
        try { await TumbleAsync(_x, ct); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Recover(ex); }
        finally { Done(ct); }
    }

    /// <summary>Gravity: accelerates down to y, limbs flailing, lands with a squash.</summary>
    private async Task FallTo(float x, float y, CancellationToken ct)
    {
        var at = Where();
        float drop = Math.Max(0, y - at.Y);
        int ms = (int)Math.Clamp(Math.Sqrt(drop) * 34, 160, 900);
        Body.Properties.InsertVector3("Translation", new Vector3(x, at.Y, 0));
        var fall = Play(ms,
            (Body, "Translation.Y", Scalar(Accelerate, (1, y - 4))),
            (Body, "RotationAngleInDegrees", Scalar(Smooth, (0.5f, -18), (1, 8))));
        var flail = Task.Run(async () =>
        {
            for (int i = 0; !fall.IsCompleted && i < 12; i++)
                await DispatcherQueue.EnqueueAsync(() => Limbs(110, i % 2 == 0 ? 165 : 110, i % 2 == 0 ? -110 : -165, i % 2 == 0 ? 40 : -10, i % 2 == 0 ? -10 : 40));
        }, ct);
        await fall;
        ct.ThrowIfCancellationRequested();
        // the landing: knees bend to take it, arms come forward, then he straightens up
        _ = Limbs(90, 70, -70, 40, -40);
        await Play(90, (Body, "Translation.Y", Scalar(Smooth, (1, y + 2))), (Body, "RotationAngleInDegrees", Scalar(Smooth, (1, 10))));
        _ = Limbs(180, 40, -40, 18, -18);
        await Play(180, (Body, "Translation.Y", Scalar(Smooth, (1, y))), (Body, "RotationAngleInDegrees", Scalar(Smooth, (1, 0))));
        ct.ThrowIfCancellationRequested();
    }

    // ------------------------------------------------------------ the entrance

    private async Task EnterAsync(CancellationToken ct)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(Dancer, true);
        Body.CenterPoint = new Vector3(BodyW / 2, BodyH, 0);
        float doorX = ExitDoorX(), doorY = DoorY(), floorY = FloorY();
        float outX = Math.Min(doorX - BodyW / 2 + 6, (float)Root.ActualWidth - BodyW);   // shoved just past the knob; the window edge is the only limit

        // tucked inside the toggle, facing out (right)
        Body.Properties.InsertVector3("Translation", new Vector3(doorX - BodyW / 2 - 10, doorY, 0));
        Body.Scale = new Vector3(0.5f, 0.5f, 1);
        Body.Opacity = 0;
        Body.RotationAngleInDegrees = 0;
        _facingLeft = false;
        Pose(30, -30, 12, -12);
        Dancer.Visibility = Visibility.Visible;

        // shoved out: pops out to the right at full size, tipping, arms thrown up
        _ = Limbs(200, 150, -150, 30, -30);
        await Play(200,
            (Body, "Opacity", Scalar(Enter, (0.3f, 1))),
            (Body, "Scale", Vec(Enter, (1, Vector3.One))),
            (Body, "Translation.X", Scalar(Enter, (1, outX))),
            (Body, "RotationAngleInDegrees", Scalar(Enter, (1, 24))));
        ct.ThrowIfCancellationRequested();

        // then straight down: onto the charged bar if it's under him, otherwise all the way to the bottom
        if (outX >= FloorLeft() - 4 && outX <= FloorRight() + 4)
        {
            await FallTo(outX, floorY, ct);
            _x = outX;
            StartDancing();
        }
        else
        {
            await TumbleAsync(outX, ct);
        }
    }

    // ------------------------------------------------------------ the exit

    private async Task ExitAsync(CancellationToken ct)
    {
        _exiting = true;
        try { await ExitCoreAsync(ct); }
        finally { _exiting = false; }
    }

    private async Task ExitCoreAsync(CancellationToken ct)
    {
        StandStill();
        float underDoor = UnderDoorX(), homeX = HomeX(), floorY = FloorY(), doorY = DoorY();

        await WalkTo(homeX, floorY, ct);
        Face(false);
        await ClimbAsync(floorY, doorY, ct);

        // The charged bar may end short of the door. Then the ladder stands at its end, and he leaps the rest.
        float gap = underDoor - homeX;
        if (gap > 3)
        {
            await LeapAsync(gap, doorY, ct);
            homeX = underDoor;
        }

        // through the door: slides right into the toggle and shrinks away; the ladder, no longer held, drops out of the window
        _ = Limbs(200, 40, -40, 10, -10);
        var drop = DropLadder();
        await Play(200,
            (Body, "Translation.X", Scalar(Exit, (1, homeX + 14))),
            (Body, "Scale", Vec(Exit, (1, new Vector3(0.5f, 0.5f, 1)))),
            (Body, "Opacity", Scalar(Exit, (0.5f, 1), (1, 0))));
        ct.ThrowIfCancellationRequested();
        Dancer.Visibility = Visibility.Collapsed;
        PassthroughFor();
        await drop;
    }

    /// <summary>
    /// A standing leap across a gap at door height: the longer the gap, the longer he winds up and braces,
    /// the higher the arc, and the harder the landing.
    /// </summary>
    private async Task LeapAsync(float gap, float y, CancellationToken ct)
    {
        float startX = _x, endX = _x + gap;
        int windMs = (int)Math.Clamp(300 + gap * 9, 300, 1800);   // 100 px gap: 1.2 s of getting ready
        int jumpMs = (int)Math.Clamp(320 + gap * 2.2, 320, 760);
        float peak = Math.Clamp(8 + gap * 0.18f, 8, 34);

        // measure it up, then wind up: crouch lower and lean back the longer he waits
        _ = Limbs(220, 60, -60, 12, -12);
        await Play(220, (Body, "RotationAngleInDegrees", Scalar(Smooth, (1, 6))));
        ct.ThrowIfCancellationRequested();
        _ = Limbs(windMs, -70, 70, 45, -45);                       // arms swing back, knees bend deep
        await Play(windMs,
            (Body, "Translation.Y", Scalar(Smooth, (1, y + 4))),
            (Body, "RotationAngleInDegrees", Scalar(Smooth, (1, -14))),
            (Body, "Translation.X", Scalar(Smooth, (1, startX - Math.Min(10, gap * 0.12f)))));
        ct.ThrowIfCancellationRequested();
        await Task.Delay((int)(gap * 3), ct);                       // the brace: a held beat before the spring

        // the spring and the flight: stretch, arms and legs out, arc over, tuck for the landing
        _ = Limbs(jumpMs, 160, -160, 45, -35);
        await Play(jumpMs,
            (Body, "Translation.X", Scalar(Linear, (1, endX))),
            (Body, "Translation.Y", Scalar(Enter, (0.45f, y - peak))),
            (Body, "RotationAngleInDegrees", Scalar(Smooth, (0.4f, 12), (1, -6))));
        ct.ThrowIfCancellationRequested();
        var fall = Compositor.CreateScalarKeyFrameAnimation();
        fall.InsertKeyFrame(1f, y, Accelerate);
        fall.Duration = TimeSpan.FromMilliseconds(jumpMs * 0.55);
        await Play((int)(jumpMs * 0.55), (Body, "Translation.Y", fall));
        ct.ThrowIfCancellationRequested();

        // the landing: the bigger the jump, the deeper the knees go, then he stands up
        float dip = Math.Clamp(2 + gap * 0.03f, 2, 6);
        _ = Limbs(100, 80, -80, 50, -50);
        await Play(100, (Body, "Translation.Y", Scalar(Smooth, (1, y + dip))), (Body, "RotationAngleInDegrees", Scalar(Smooth, (1, 8))));
        _ = Limbs(220, 40, -40, 20, -20);
        await Play(220, (Body, "Translation.Y", Scalar(Smooth, (1, y))), (Body, "RotationAngleInDegrees", Scalar(Smooth, (1, 0))));
        _x = endX;
        _ = DropLadder();
        await Limbs(120, 20, -20, 10, -10);
    }

    /// <summary>Stop mid-move and stand still where he is.</summary>
    private void StandStill()
    {
        var at = Freeze();
        _x = at.X;
        Body.Properties.InsertVector3("Translation", new Vector3(at.X, FloorY(), 0));
        Body.RotationAngleInDegrees = 0;
        Body.Scale = Vector3.One;
    }

    /// <summary>Walk along a floor to x: legs and arms swing in step, a little hop per stride.</summary>
    private async Task WalkTo(float x, float floorY, CancellationToken ct)
    {
        Face(x < _x);
        int strideMs = 170;
        int strides = Math.Max(2, (int)(Math.Abs(x - _x) / 14));
        var hops = new List<(float, float)>();
        for (int i = 0; i < strides; i++)
        {
            hops.Add(((i + 0.5f) / strides, floorY - 3));
            hops.Add(((i + 1f) / strides, floorY));
        }
        var walk = Play(strides * strideMs,
            (Body, "Translation.X", Scalar(Linear, (1, x))),
            (Body, "Translation.Y", Scalar(Linear, hops.ToArray())));
        for (int i = 0; i < strides; i++)
            await Step(strideMs, ct, i % 2 == 0 ? -25 : 25, i % 2 == 0 ? 25 : -25, i % 2 == 0 ? 28 : -28, i % 2 == 0 ? -28 : 28);
        await walk;
        ct.ThrowIfCancellationRequested();
        _x = x;
    }

    /// <summary>A ladder grows from his feet up to the higher floor; he climbs it hand over hand.</summary>
    private async Task ClimbAsync(float fromY, float toY, CancellationToken ct)
    {
        float top = toY + BodyH - 3, bottom = fromY + BodyH;
        Ladder.Data = LadderPath(_x + 5, _x + BodyW - 5, top, bottom);
        var ladder = ElementCompositionPreview.GetElementVisual(Ladder);
        ladder.StopAnimation("Translation.Y"); ladder.StopAnimation("RotationAngleInDegrees");
        ladder.Properties.InsertVector3("Translation", Vector3.Zero);
        ladder.RotationAngleInDegrees = 0;
        ladder.CenterPoint = new Vector3(_x + BodyW / 2, bottom, 0);
        ladder.Scale = new Vector3(1, 0, 1);
        ladder.Opacity = 1;
        Ladder.Visibility = Visibility.Visible;
        _ = Limbs(180, 150, -20, 10, -10);   // reaches up for the first rung while it grows
        await Play(180, (ladder, "Scale", Vec(Enter, (1, Vector3.One))));
        ct.ThrowIfCancellationRequested();

        int rungs = Math.Max(3, (int)((bottom - top) / 9));
        int rungMs = 150;
        var climb = Play(rungs * rungMs, (Body, "Translation.Y", Scalar(Linear, (1, toY))));
        for (int i = 0; i < rungs; i++)
            await Step(rungMs, ct, i % 2 == 0 ? 165 : 120, i % 2 == 0 ? -120 : -165, i % 2 == 0 ? 35 : 5, i % 2 == 0 ? -5 : -35);
        await climb;
        ct.ThrowIfCancellationRequested();
    }

    private void HideLadder()
    {
        var ladder = ElementCompositionPreview.GetElementVisual(Ladder);
        ladder.StopAnimation("Scale");
        ladder.StopAnimation("Translation.Y");
        ladder.StopAnimation("RotationAngleInDegrees");
        ladder.Properties.InsertVector3("Translation", Vector3.Zero);
        ladder.RotationAngleInDegrees = 0;
        Ladder.Visibility = Visibility.Collapsed;
    }

    /// <summary>Nobody's holding it now: the ladder tips a little and falls away past the bottom of the window.</summary>
    private async Task DropLadder()
    {
        var ladder = ElementCompositionPreview.GetElementVisual(Ladder);
        ElementCompositionPreview.SetIsTranslationEnabled(Ladder, true);
        float travel = (float)Root.ActualHeight + 40;
        var fall = Compositor.CreateScalarKeyFrameAnimation();
        fall.InsertKeyFrame(1f, travel, Accelerate);
        fall.Duration = TimeSpan.FromMilliseconds(700);
        fall.DelayTime = TimeSpan.FromMilliseconds(120);   // a beat: it stands on its own for a moment first
        var tip = Compositor.CreateScalarKeyFrameAnimation();
        tip.InsertKeyFrame(1f, 9f, Smooth);
        tip.Duration = TimeSpan.FromMilliseconds(820);
        var done = new TaskCompletionSource();
        var batch = Compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        ladder.StartAnimation("Translation.Y", fall);
        ladder.StartAnimation("RotationAngleInDegrees", tip);
        batch.Completed += (_, _) => done.TrySetResult();
        batch.End();
        await done.Task;
        if (ladder.Properties.TryGetVector3("Translation", out var t) == CompositionGetValueStatus.Succeeded && t.Y >= travel - 1)
            HideLadder();   // it's gone; unless a newer ladder has been put up meanwhile
    }

    private static Geometry LadderPath(float left, float right, float top, float bottom)
    {
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        var d = new System.Text.StringBuilder(string.Create(ci, $"M{left},{top} L{left},{bottom} M{right},{top} L{right},{bottom}"));
        for (float y = bottom - 6; y > top + 2; y -= 9)
            d.Append(string.Create(ci, $" M{left},{y} L{right},{y}"));
        return (Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(typeof(Geometry), d.ToString());
    }

    // ------------------------------------------------------------ feelings about the cable

    /// <summary>Unplugged: he drops to his knees and sobs for three seconds. Plugged in: he can't contain himself. Then back to dancing.</summary>
    private async void ReactToPlug(bool pluggedIn)
    {
        if (!_dancing || _held) return;
        var ct = Begin();
        try
        {
            StandStill();
            if (pluggedIn) await JoyAsync(ct); else await DespairAsync(ct);
            StartDancing();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Recover(ex); }
    }

    private async Task DespairAsync(CancellationToken ct)
    {
        float floorY = FloorY();
        var mouth = ElementCompositionPreview.GetElementVisual(SadMouth);
        Body.RotationAngleInDegrees = 0;   // square to the screen the whole way through

        // 1. the news lands: arms drop, face falls
        _ = Limbs(280, 0, 0, 10, -10);
        mouth.Opacity = 1;
        await Task.Delay(280, ct);

        // 2. collapses onto his knees: the legs fold under him (seen from the front they foreshorten), the body drops
        _ = Limbs(300, 25, -25, 6, -6);
        var fold = Kneel(true, 300);
        await Play(300, (Body, "Translation.Y", Scalar(Accelerate, (1, floorY + 8))));
        await fold;
        ct.ThrowIfCancellationRequested();

        // 3. crying: hands to his face, shoulders heaving, tears streaming, for three seconds
        _ = Limbs(240, 172, -172, 6, -6);
        var tears = Tears(ct);
        var sobStart = DateTime.UtcNow;
        while ((DateTime.UtcNow - sobStart).TotalMilliseconds < 3000)
        {
            ct.ThrowIfCancellationRequested();
            await Play(220, (Body, "Translation.Y", Scalar(Smooth, (1, floorY + 10))));
            await Play(260, (Body, "Translation.Y", Scalar(Smooth, (1, floorY + 8))));
        }
        await tears;

        // 4. a wipe of the eyes, back up on his feet, and on with the show
        mouth.Opacity = 0;
        _ = Limbs(320, 170, -30, 14, -14);
        var unfold = Kneel(false, 320);
        await Play(320, (Body, "Translation.Y", Scalar(Smooth, (1, floorY))));
        await unfold;
        await Limbs(200, 40, -40, 18, -18);
    }

    /// <summary>Kneeling, seen from the front: the legs fold under him, so they shorten to a third and the hips come down.</summary>
    private Task Kneel(bool down, int ms)
    {
        var legs = LimbVisuals();
        var done = new TaskCompletionSource();
        var batch = Compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        foreach (var leg in new[] { legs[2], legs[3] })
        {
            var a = Compositor.CreateVector3KeyFrameAnimation();
            a.InsertKeyFrame(1f, new Vector3(1, down ? 0.3f : 1f, 1), Smooth);
            a.Duration = TimeSpan.FromMilliseconds(ms);
            leg.StartAnimation("Scale", a);
        }
        batch.Completed += (_, _) => done.TrySetResult();
        batch.End();
        return done.Task;
    }

    /// <summary>Tears: two drops that fall from his face and fade, over and over, for three seconds.</summary>
    private async Task Tears(CancellationToken ct)
    {
        var left = ElementCompositionPreview.GetElementVisual(TearL);
        var right = ElementCompositionPreview.GetElementVisual(TearR);
        ElementCompositionPreview.SetIsTranslationEnabled(TearL, true);
        ElementCompositionPreview.SetIsTranslationEnabled(TearR, true);
        var start = DateTime.UtcNow;
        try
        {
            while ((DateTime.UtcNow - start).TotalMilliseconds < 3000)
            {
                foreach (var tear in new[] { left, right })
                {
                    ct.ThrowIfCancellationRequested();
                    tear.Opacity = 1;
                    tear.Properties.InsertVector3("Translation", Vector3.Zero);
                    var fall = Compositor.CreateScalarKeyFrameAnimation();
                    fall.InsertKeyFrame(1f, 14f, Accelerate);
                    fall.Duration = TimeSpan.FromMilliseconds(520);
                    var fade = Compositor.CreateScalarKeyFrameAnimation();
                    fade.InsertKeyFrame(0.6f, 1f);
                    fade.InsertKeyFrame(1f, 0f);
                    fade.Duration = TimeSpan.FromMilliseconds(520);
                    tear.StartAnimation("Translation.Y", fall);
                    tear.StartAnimation("Opacity", fade);
                    await Task.Delay(300, ct);
                }
            }
        }
        finally
        {
            left.Opacity = 0;
            right.Opacity = 0;
        }
    }

    private async Task JoyAsync(CancellationToken ct)
    {
        float floorY = FloorY();
        // a double take, then three big jumps with arms flung up, then a happy little shake
        _ = Limbs(150, 30, -30, 18, -18);
        await Play(150, (Body, "RotationAngleInDegrees", Scalar(Smooth, (1, -8))));
        for (int i = 0; i < 3; i++)
        {
            ct.ThrowIfCancellationRequested();
            _ = Limbs(120, 60, -60, 50, -50);                                   // crouch
            await Play(120, (Body, "Translation.Y", Scalar(Smooth, (1, floorY + 4))), (Body, "RotationAngleInDegrees", Scalar(Smooth, (1, 0))));
            _ = Limbs(260, 170, -170, 35, -35);                                  // up!
            await Play(260, (Body, "Translation.Y", Scalar(Enter, (1, floorY - 22 - i * 3))));
            _ = Limbs(200, 150, -150, 20, -20);
            await Play(200, (Body, "Translation.Y", Scalar(Accelerate, (1, floorY))));
        }
        _ = Limbs(160, 165, -165, 18, -18);
        for (int i = 0; i < 6; i++)
        {
            ct.ThrowIfCancellationRequested();
            await Play(70, (Body, "RotationAngleInDegrees", Scalar(Linear, (1, i % 2 == 0 ? 10 : -10))));
        }
        _ = Limbs(200, 40, -40, 18, -18);
        await Play(200, (Body, "RotationAngleInDegrees", Scalar(Smooth, (1, 0))));
    }

    // ------------------------------------------------------------ dancing: routines, picked at random, back to back

    /// <summary>Where the pointer must reach him: a band over the bar while dancing, the bottom band on the ground, the ladder while climbing.</summary>
    private void PassthroughFor(params RectInt32[] strips)
    {
        _extraPassthrough.Clear();
        _extraPassthrough.AddRange(strips);
        UpdateRegions();
    }
    private RectInt32 FloorStrip() => RectOf(FloorLeft(), FloorY() - 30, BarTrack.ActualWidth, BodyH + 32);
    private RectInt32 GroundStrip() => RectOf(0, GroundY() - 30, Root.ActualWidth, BodyH + 30);
    private RectInt32 LadderStrip(float fromY, float toY) => RectOf(_x - 12, Math.Min(fromY, toY) - 4, BodyW + 24, Math.Abs(fromY - toY) + BodyH + 8);

    private void StartDancing()
    {
        PassthroughFor(FloorStrip());
        if (_pendingExit)
        {
            _pendingExit = false;
            _ = LeaveAsync();
            return;
        }
        var ct = Begin();
        _dancing = true;
        if (Ladder.Visibility == Visibility.Visible) _ = DropLadder();
        _ = DanceLoopAsync(ct);
    }

    private async Task DanceLoopAsync(CancellationToken ct)
    {
        var rng = Random.Shared;
        Func<CancellationToken, Task>[] routines = [Groove, Moonwalk, Robot, JumpingJacks, Twist, RunningMan, Moonwalk, Shimmy, Bow, Groove, Moonwalk, Groove];
        try
        {
            while (!ct.IsCancellationRequested)
                await routines[rng.Next(routines.Length)](ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Recover(ex); }
    }

    /// <summary>Idle groove: a few beats of shuffling, hopping and leaning, arms doing their thing.</summary>
    private async Task Groove(CancellationToken ct)
    {
        var rng = Random.Shared;
        int beats = rng.Next(3, 6);
        for (int i = 0; i < beats; i++)
        {
            ct.ThrowIfCancellationRequested();
            float floorY = FloorY();
            int ms = rng.Next(340, 540);
            float target = (float)Math.Clamp(_x + rng.Next(-30, 31), FloorLeft(), FloorRight());
            if (Math.Abs(target - _x) > 14) Face(target < _x);   // small shuffles don't turn him round
            _x = target;
            float height = rng.Next(3, 8);
            float lean = rng.Next(6, 16) * (i % 2 == 0 ? 1 : -1);
            _ = i % 2 == 0 ? Limbs(ms, 150, -40, 22, -12) : Limbs(ms, 40, -150, 12, -22);
            await Play(ms,
                (Body, "Translation.X", Scalar(Smooth, (1, target))),
                (Body, "Translation.Y", Scalar(Smooth, (0.5f, floorY - height), (1, floorY))),
                (Body, "RotationAngleInDegrees", Scalar(Smooth, (1, lean))));
        }
    }

    /// <summary>Moonwalk: faces one way, glides the other, feet shuffling, leaning back.</summary>
    private async Task Moonwalk(CancellationToken ct)
    {
        float left = FloorLeft(), right = FloorRight();
        bool glideLeft = _x > (left + right) / 2;
        float target = glideLeft ? Math.Max(left, _x - 90) : Math.Min(right, _x + 90);
        Face(!glideLeft);
        var glide = Play(1400,
            (Body, "Translation.X", Scalar(Linear, (1, target))),
            (Body, "RotationAngleInDegrees", Scalar(Smooth, (0.15f, glideLeft ? 12 : -12), (0.9f, glideLeft ? 12 : -12), (1, 0))));
        for (int i = 0; i < 7; i++)
            await Step(200, ct, 20, -30, i % 2 == 0 ? 30 : -6, i % 2 == 0 ? -6 : 30);
        await glide;
        _x = target;
    }

    /// <summary>The robot: sharp poses with holds, no easing, arms at right angles.</summary>
    private async Task Robot(CancellationToken ct)
    {
        (double, double, double, double)[] poses = [(90, -90, 0, 0), (90, -170, 0, 0), (170, -170, 0, 0), (170, -90, 0, 0), (90, -90, 0, 0), (40, -40, 15, -15)];
        foreach (var (al, ar, ll, lr) in poses)
        {
            ct.ThrowIfCancellationRequested();
            await Limbs(90, al, ar, ll, lr, jerky: true);
            await Play(140, (Body, "RotationAngleInDegrees", Scalar(Linear, (1, Body.RotationAngleInDegrees == 0 ? 4 : 0))));
        }
    }

    /// <summary>Jumping jacks: arms and legs out on the way up, in on the way down.</summary>
    private async Task JumpingJacks(CancellationToken ct)
    {
        float floorY = FloorY();
        for (int i = 0; i < 3; i++)
        {
            ct.ThrowIfCancellationRequested();
            _ = Limbs(220, 165, -165, 38, -38);
            await Play(220, (Body, "Translation.Y", Scalar(Enter, (1, floorY - 12))));
            _ = Limbs(220, 20, -20, 6, -6);
            await Play(220, (Body, "Translation.Y", Scalar(Accelerate, (1, floorY))));
        }
    }

    /// <summary>The twist: feet planted, hips and shoulders swivel side to side, arms loose, sinking lower each time.</summary>
    private async Task Twist(CancellationToken ct)
    {
        float floorY = FloorY();
        for (int i = 0; i < 6; i++)
        {
            ct.ThrowIfCancellationRequested();
            bool left = i % 2 == 0;
            float sink = Math.Min(4, i * 0.8f);
            _ = Limbs(200, left ? 100 : 40, left ? -40 : -100, left ? 30 : -6, left ? 6 : -30);
            await Play(200,
                (Body, "RotationAngleInDegrees", Scalar(Smooth, (1, left ? -8 : 8))),
                (Body, "Translation.Y", Scalar(Smooth, (1, floorY + sink))));
        }
        _ = Limbs(220, 40, -40, 18, -18);
        await Play(220, (Body, "RotationAngleInDegrees", Scalar(Smooth, (1, 0))), (Body, "Translation.Y", Scalar(Smooth, (1, floorY))));
    }

    /// <summary>Running man: fast alternating knees, tiny shuffles, arms pumping.</summary>
    private async Task RunningMan(CancellationToken ct)
    {
        float floorY = FloorY();
        for (int i = 0; i < 8; i++)
        {
            ct.ThrowIfCancellationRequested();
            float shuffle = (float)Math.Clamp(_x + (i % 2 == 0 ? 5 : -5), FloorLeft(), FloorRight());
            _ = Limbs(130, i % 2 == 0 ? -50 : 60, i % 2 == 0 ? 60 : -50, i % 2 == 0 ? 55 : -10, i % 2 == 0 ? -10 : 55);
            await Play(130,
                (Body, "Translation.Y", Scalar(Smooth, (0.5f, floorY - 4), (1, floorY))),
                (Body, "Translation.X", Scalar(Smooth, (1, shuffle))));
        }
    }

    /// <summary>Shimmy: a rapid wiggle, arms half up.</summary>
    private async Task Shimmy(CancellationToken ct)
    {
        _ = Limbs(200, 110, -110, 14, -14);
        for (int i = 0; i < 8; i++)
        {
            ct.ThrowIfCancellationRequested();
            await Play(80, (Body, "RotationAngleInDegrees", Scalar(Linear, (1, i % 2 == 0 ? 9 : -9))));
        }
        await Play(120, (Body, "RotationAngleInDegrees", Scalar(Smooth, (1, 0))));
    }

    /// <summary>A bow to the audience.</summary>
    private async Task Bow(CancellationToken ct)
    {
        float dir = _facingLeft ? -1 : 1;
        _ = Limbs(320, 60, -20, 4, -4);
        await Play(320, (Body, "RotationAngleInDegrees", Scalar(Smooth, (1, 38 * dir))));
        await Task.Delay(260, ct);
        _ = Limbs(320, 40, -40, 18, -18);
        await Play(320, (Body, "RotationAngleInDegrees", Scalar(Smooth, (1, 0))));
    }

    // ------------------------------------------------------------ limbs (compositor rotations about the joints)

    private Visual[]? _limbs;
    private static readonly Vector3[] Joints = [new(13, 9, 0), new(13, 9, 0), new(13, 17, 0), new(13, 17, 0)];   // shoulders, hips

    /// <summary>The four limb visuals, pivoted at their joints. Composition hands off from the current angle, so no snapping.</summary>
    private Visual[] LimbVisuals()
    {
        if (_limbs is null)
        {
            _limbs = [ElementCompositionPreview.GetElementVisual(ArmL), ElementCompositionPreview.GetElementVisual(ArmR), ElementCompositionPreview.GetElementVisual(LegL), ElementCompositionPreview.GetElementVisual(LegR)];
            for (int i = 0; i < 4; i++) _limbs[i].CenterPoint = Joints[i];
            Pose(35, -35, 18, -18);
        }
        return _limbs;
    }

    private void Pose(double armL, double armR, double legL, double legR)
    {
        var limbs = LimbVisuals();
        double[] angles = [armL, armR, legL, legR];
        for (int i = 0; i < 4; i++)
        {
            limbs[i].StopAnimation("RotationAngleInDegrees");
            limbs[i].RotationAngleInDegrees = (float)angles[i];
        }
    }

    /// <summary>One timed step of a limb cycle: start the pose and wait exactly its duration.</summary>
    private async Task Step(int ms, CancellationToken ct, double armL, double armR, double legL, double legR)
    {
        ct.ThrowIfCancellationRequested();
        _ = Limbs(ms, armL, armR, legL, legR);
        await Task.Delay(ms, ct);
    }

    private Task Limbs(int ms, double armL, double armR, double legL, double legR, bool jerky = false)
    {
        var limbs = LimbVisuals();
        double[] angles = [armL, armR, legL, legR];
        var ease = jerky ? Linear : Smooth;
        var done = new TaskCompletionSource();
        var batch = Compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        for (int i = 0; i < 4; i++)
        {
            var a = Compositor.CreateScalarKeyFrameAnimation();
            a.InsertKeyFrame(1f, (float)angles[i], ease);
            a.Duration = TimeSpan.FromMilliseconds(ms);
            limbs[i].StartAnimation("RotationAngleInDegrees", a);
        }
        batch.Completed += (_, _) => done.TrySetResult();
        batch.End();
        return done.Task;
    }

    // ------------------------------------------------------------ body (compositor animations)

    private CompositionEasingFunction Enter => Compositor.CreateCubicBezierEasingFunction(new Vector2(0, 0), new Vector2(0, 1));
    private CompositionEasingFunction Exit => Compositor.CreateCubicBezierEasingFunction(new Vector2(1, 0), new Vector2(1, 1));
    private CompositionEasingFunction Accelerate => Compositor.CreateCubicBezierEasingFunction(new Vector2(0.5f, 0), new Vector2(1, 1));
    private CompositionEasingFunction Smooth => Compositor.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0), new Vector2(0.4f, 1));
    private CompositionEasingFunction Linear => Compositor.CreateLinearEasingFunction();

    private ScalarKeyFrameAnimation Scalar(CompositionEasingFunction ease, params (float At, float Value)[] frames)
    {
        var a = Compositor.CreateScalarKeyFrameAnimation();
        foreach (var (at, value) in frames) a.InsertKeyFrame(at, value, ease);
        return a;
    }

    private Vector3KeyFrameAnimation Vec(CompositionEasingFunction ease, params (float At, Vector3 Value)[] frames)
    {
        var a = Compositor.CreateVector3KeyFrameAnimation();
        foreach (var (at, value) in frames) a.InsertKeyFrame(at, value, ease);
        return a;
    }

    /// <summary>Run several compositor animations for the same duration and wait for all of them.</summary>
    private Task Play(int ms, params (Visual Visual, string Property, KeyFrameAnimation Animation)[] items)
    {
        var done = new TaskCompletionSource();
        var batch = Compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        foreach (var (visual, property, animation) in items)
        {
            animation.Duration = TimeSpan.FromMilliseconds(ms);
            visual.StartAnimation(property, animation);
        }
        batch.Completed += (_, _) => done.TrySetResult();
        batch.End();
        return done.Task;
    }
}

internal static class AnimationExtensions
{
    public static KeyFrameAnimation With(this KeyFrameAnimation a, int ms)
    {
        a.Duration = TimeSpan.FromMilliseconds(ms);
        return a;
    }

    /// <summary>Run a UI-thread action from a background task and await its task.</summary>
    public static Task EnqueueAsync(this Microsoft.UI.Dispatching.DispatcherQueue queue, Func<Task> action)
    {
        var tcs = new TaskCompletionSource();
        queue.TryEnqueue(async () => { try { await action(); tcs.TrySetResult(); } catch (Exception e) { tcs.TrySetException(e); } });
        return tcs.Task;
    }
}
