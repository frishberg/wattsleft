using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace BatteryChecker;

/// <summary>
/// Our own hover cards. Rest the pointer on a block and, after a beat, a small
/// card in the app's own blue opens beside the window, level with what you're
/// pointing at, with one plain sentence. Every card lands in the same column
/// just outside the window, so nothing is ever covered. Built at show time so
/// the words match the live numbers.
/// </summary>
public sealed class HoverCards
{
    public sealed record Card(string Title, string Value, IReadOnlyList<string> Lines);

    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(350);
    public const double Width = 256;   // the whole card, border to border

    /// <summary>Where to put the card for an anchor: a point in the anchor's own coordinates, and which side of it the card goes.</summary>
    public Func<FrameworkElement, (Windows.Foundation.Point Position, FlyoutPlacementMode Placement)>? Locate { get; set; }

    private readonly DispatcherQueueTimer _timer;
    private Flyout? _open;
    private FrameworkElement? _target;
    private Func<Card>? _content;
    private float _slideFrom = -6f;

    public HoverCards(DispatcherQueue queue)
    {
        _timer = queue.CreateTimer();
        _timer.Interval = Delay;
        _timer.IsRepeating = false;
        _timer.Tick += (_, _) => Open();
    }

    public void Attach(FrameworkElement element, Func<Card> content)
    {
        element.PointerEntered += (_, _) => Arm(element, content);
        element.PointerExited += (_, _) => Hide();
        element.PointerPressed += (_, _) => Hide();
    }

    public void Hide()
    {
        _timer.Stop();
        _target = null;
        if (_open is { } flyout)
        {
            _open = null;
            _ = FadeOutAndClose(flyout);
        }
    }

    /// <summary>A gentle fade out, eased, fully finished before the flyout is asked to close, so nothing cuts it short.</summary>
    private static async Task FadeOutAndClose(Flyout flyout)
    {
        if (flyout.Content is UIElement content)
        {
            var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(content);
            var compositor = visual.Compositor;
            var done = new TaskCompletionSource();
            var batch = compositor.CreateScopedBatch(Microsoft.UI.Composition.CompositionBatchTypes.Animation);
            var fade = compositor.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(1f, 0f, compositor.CreateCubicBezierEasingFunction(new System.Numerics.Vector2(0.4f, 0), new System.Numerics.Vector2(0.2f, 1)));
            fade.Duration = TimeSpan.FromMilliseconds(220);
            visual.StartAnimation("Opacity", fade);
            batch.Completed += (_, _) => done.TrySetResult();
            batch.End();
            await done.Task;
        }
        flyout.Hide();
    }

    private void Arm(FrameworkElement element, Func<Card> content)
    {
        if (_open is not null && _target == element)
            return;
        Hide();
        _target = element;
        _content = content;
        _timer.Start();
    }

    private void Open()
    {
        if (_target is null || _content is null)
            return;
        var card = _content();

        var ink = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
        var muted = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        var panel = new StackPanel { Width = Width - 2 * 14 - 2, Spacing = 3 };
        panel.Children.Add(new TextBlock
        {
            Text = card.Title.ToUpperInvariant(),
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = muted,
        });
        foreach (var line in card.Lines)
        {
            panel.Children.Add(new TextBlock
            {
                Text = line,
                TextWrapping = TextWrapping.Wrap,
                Style = (Style)Application.Current.Resources["BodyTextBlockStyle"],
                FontSize = 13,
                LineHeight = 18,
                Foreground = ink,
            });
        }

        // The system's own flyout look (the dark grey card), just sized and padded our way.
        var presenter = new Style(typeof(FlyoutPresenter));
        presenter.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(14, 10, 14, 12)));
        presenter.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(10)));
        presenter.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 0.0));
        presenter.Setters.Add(new Setter(FrameworkElement.MaxWidthProperty, Width));
        presenter.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 0.0));
        var flyout = new Flyout
        {
            Content = panel,
            ShouldConstrainToRootBounds = false,
            AllowFocusOnInteraction = false,
            FlyoutPresenterStyle = presenter,
        };
        var (position, placement) = Locate?.Invoke(_target) ?? (new Windows.Foundation.Point(_target.ActualWidth, _target.ActualHeight / 2), FlyoutPlacementMode.Right);
        flyout.ShowAt(_target, new FlyoutShowOptions
        {
            Position = position,
            Placement = placement,
            ShowMode = FlyoutShowMode.Transient,
        });
        _slideFrom = placement == FlyoutPlacementMode.Left ? 6f : -6f;   // it slides out from the window's edge
        _open = flyout;

        var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(panel);
        Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetIsTranslationEnabled(panel, true);
        var compositor = visual.Compositor;
        var ease = compositor.CreateCubicBezierEasingFunction(new System.Numerics.Vector2(0, 0), new System.Numerics.Vector2(0, 1));
        var fadeIn = compositor.CreateScalarKeyFrameAnimation();
        fadeIn.InsertKeyFrame(0f, 0f);
        fadeIn.InsertKeyFrame(1f, 1f, ease);
        fadeIn.Duration = TimeSpan.FromMilliseconds(140);
        var slideIn = compositor.CreateScalarKeyFrameAnimation();
        slideIn.InsertKeyFrame(0f, _slideFrom);
        slideIn.InsertKeyFrame(1f, 0f, ease);
        slideIn.Duration = TimeSpan.FromMilliseconds(140);
        visual.StartAnimation("Opacity", fadeIn);
        visual.StartAnimation("Translation.X", slideIn);
    }
}
