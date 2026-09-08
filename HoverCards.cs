using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace BatteryChecker;

/// <summary>
/// Our own hover cards. Rest the pointer on a block and, after a beat, a
/// Fluent flyout opens beside it with a headline number and plain-English
/// context. Built at show time so the numbers are always live.
/// </summary>
public sealed class HoverCards
{
    public sealed record Card(string Title, string Value, IReadOnlyList<string> Lines);

    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(700);

    private readonly DispatcherQueueTimer _timer;
    private Flyout? _open;
    private FrameworkElement? _target;
    private Func<Card>? _content;

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

    /// <summary>The card leaves the way it came: a short fade and a slide back toward its anchor, then it closes.</summary>
    private static async Task FadeOutAndClose(Flyout flyout)
    {
        if (flyout.Content is UIElement content)
        {
            var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(content);
            Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetIsTranslationEnabled(content, true);
            var compositor = visual.Compositor;
            var ease = compositor.CreateCubicBezierEasingFunction(new System.Numerics.Vector2(1, 0), new System.Numerics.Vector2(1, 1));
            var fade = compositor.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(1f, 0f, ease);
            fade.Duration = TimeSpan.FromMilliseconds(140);
            var slide = compositor.CreateScalarKeyFrameAnimation();
            slide.InsertKeyFrame(1f, -6f, ease);
            slide.Duration = TimeSpan.FromMilliseconds(140);
            visual.StartAnimation("Opacity", fade);
            visual.StartAnimation("Translation.X", slide);
            await Task.Delay(140);
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

        var panel = new StackPanel { MaxWidth = 280, Spacing = 4 };
        panel.Children.Add(new TextBlock
        {
            Text = card.Title.ToUpperInvariant(),
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
        panel.Children.Add(new TextBlock
        {
            Text = card.Value,
            FontFamily = new FontFamily("Segoe UI Variable Display"),
            FontSize = 24,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4),
        });
        foreach (var line in card.Lines)
        {
            panel.Children.Add(new TextBlock
            {
                Text = line,
                TextWrapping = TextWrapping.Wrap,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });
        }

        var flyout = new Flyout
        {
            Content = panel,
            ShouldConstrainToRootBounds = false,
            AllowFocusOnInteraction = false,
        };
        flyout.ShowAt(_target, new FlyoutShowOptions
        {
            Placement = FlyoutPlacementMode.RightEdgeAlignedTop,
            ShowMode = FlyoutShowMode.Transient,
        });
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
        slideIn.InsertKeyFrame(0f, -6f);
        slideIn.InsertKeyFrame(1f, 0f, ease);
        slideIn.Duration = TimeSpan.FromMilliseconds(140);
        visual.StartAnimation("Opacity", fadeIn);
        visual.StartAnimation("Translation.X", slideIn);
    }
}
