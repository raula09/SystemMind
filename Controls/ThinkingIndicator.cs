using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace SystemMind.Controls;

/// <summary>Three animated dots used while the assistant is waiting for a response.</summary>
public sealed class ThinkingIndicator : Control
{
    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<ThinkingIndicator, bool>(nameof(IsActive));

    private readonly DispatcherTimer _timer;
    private double _time;
    private DateTime _lastTick = DateTime.UtcNow;

    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    static ThinkingIndicator() => AffectsRender<ThinkingIndicator>(IsActiveProperty);

    public ThinkingIndicator()
    {
        IsHitTestVisible = false;
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += (_, _) => Animate();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _lastTick = DateTime.UtcNow;
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    private void Animate()
    {
        var now = DateTime.UtcNow;
        _time += Math.Min(0.05, (now - _lastTick).TotalSeconds);
        _lastTick = now;
        if (IsActive) InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        if (!IsActive) return;

        var centerY = Bounds.Height / 2;
        var spacing = Math.Min(13, Bounds.Width / 3.5);
        var centerX = Bounds.Width / 2;
        var brush = new SolidColorBrush(Color.Parse("#74B6FF"));

        for (var index = 0; index < 3; index++)
        {
            var phase = _time * 5.5 - index * 0.75;
            var lift = (Math.Sin(phase) + 1) * 2.2;
            var radius = 2.15 + (Math.Sin(phase) + 1) * 0.35;
            context.DrawEllipse(brush, null, new Point(centerX + (index - 1) * spacing, centerY - lift), radius, radius);
        }
    }
}
