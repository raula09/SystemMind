using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace SystemMind.Controls;

/// <summary>
/// Animated glowing border for the assistant prompt box.
/// The glow hugs the bottom edge, curves up the sides and fades out toward the top.
/// Colours slowly travel along the edge and small particles drift upward.
/// Place it ON TOP of the box it decorates, with IsHitTestVisible="False".
/// </summary>
public sealed class AiGlowBorder : Control
{
    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<AiGlowBorder, bool>(nameof(IsActive));

    public static readonly StyledProperty<double> RadiusProperty =
        AvaloniaProperty.Register<AiGlowBorder, double>(nameof(Radius), 14);

    /// <summary>When true (e.g. the AI is thinking) the glow is brighter and flows faster.</summary>
    public bool IsActive { get => GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }

    /// <summary>Corner radius of the box this glow decorates.</summary>
    public double Radius { get => GetValue(RadiusProperty); set => SetValue(RadiusProperty, value); }

    // Cyclic palette: last colour blends back into the first, so the loop has no seam.
    private static readonly Color[] Palette =
    [
        Color.Parse("#F2994A"), // orange
        Color.Parse("#FF5F57"), // red
        Color.Parse("#B65CF0"), // purple
        Color.Parse("#4D96F0"), // blue
        Color.Parse("#B65CF0"), // purple
        Color.Parse("#FF5F57"), // red
    ];

    private const double Pad = 8; // room inside the control for the halo to bleed

    private sealed class Particle
    {
        public double Nx, Rise, Vx, Vy, Life, Max, Size;
    }

    private readonly List<Particle> _particles = [];
    private readonly Random _rng = new();
    private readonly Stopwatch _clock = new();
    private readonly DispatcherTimer _timer;
    private TimeSpan _last;
    private double _phase;
    private double _time;
    private double _spawnAcc;

    public AiGlowBorder()
    {
        IsHitTestVisible = false;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => Step();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _clock.Restart();
        _last = TimeSpan.Zero;
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    private void Step()
    {
        var now = _clock.Elapsed;
        var dt = Math.Min(0.1, (now - _last).TotalSeconds);
        _last = now;

        var active = IsActive;
        _time += dt;
        _phase = (_phase + dt * (active ? 0.22 : 0.07)) % 1.0;

        _spawnAcc += dt * (active ? 30 : 12);
        while (_spawnAcc >= 1 && _particles.Count < 48)
        {
            _spawnAcc -= 1;
            _particles.Add(new Particle
            {
                Nx = _rng.NextDouble(),
                Rise = _rng.NextDouble() * 3,
                Vx = (_rng.NextDouble() - 0.5) * 0.01,
                Vy = 6 + _rng.NextDouble() * 18,
                Max = 1.4 + _rng.NextDouble() * 1.8,
                Size = 0.8 + _rng.NextDouble() * 1.8,
            });
        }
        if (_spawnAcc >= 1) _spawnAcc = 0;

        for (var i = _particles.Count - 1; i >= 0; i--)
        {
            var p = _particles[i];
            p.Life += dt;
            p.Nx += p.Vx * dt;
            p.Rise += p.Vy * dt;
            if (p.Life >= p.Max) _particles.RemoveAt(i);
        }

        InvalidateVisual();
    }

    private static Color Sample(double t)
    {
        t -= Math.Floor(t);
        var pos = t * Palette.Length;
        var i = (int)pos;
        var f = pos - i;
        var a = Palette[i % Palette.Length];
        var b = Palette[(i + 1) % Palette.Length];
        return Color.FromRgb(
            (byte)(a.R + (b.R - a.R) * f),
            (byte)(a.G + (b.G - a.G) * f),
            (byte)(a.B + (b.B - a.B) * f));
    }

    private LinearGradientBrush MakeBrush(Rect rect, double opacity)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(rect.Left, 0, RelativeUnit.Absolute),
            EndPoint = new RelativePoint(rect.Right, 0, RelativeUnit.Absolute),
            Opacity = Math.Clamp(opacity, 0, 1),
        };
        const int stops = 9;
        for (var k = 0; k < stops; k++)
        {
            var o = k / (double)(stops - 1);
            brush.GradientStops.Add(new GradientStop(Sample(o + _phase), o));
        }
        return brush;
    }

    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w < 40 || h < 24) return;

        var rect = new Rect(Pad, Pad, w - 2 * Pad, h - 2 * Pad);
        var r = Math.Min(Radius, rect.Height / 2);
        var top = rect.Top + rect.Height * 0.12;

        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(new Point(rect.Left, top), false);
            g.LineTo(new Point(rect.Left, rect.Bottom - r));
            g.ArcTo(new Point(rect.Left + r, rect.Bottom), new Size(r, r), 0, false, SweepDirection.CounterClockwise);
            g.LineTo(new Point(rect.Right - r, rect.Bottom));
            g.ArcTo(new Point(rect.Right, rect.Bottom - r), new Size(r, r), 0, false, SweepDirection.CounterClockwise);
            g.LineTo(new Point(rect.Right, top));
            g.EndFigure(false);
        }

        // Fade the glow out toward the top so it pools along the bottom edge.
        var mask = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.0),
                new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.30),
                new GradientStop(Color.FromArgb(255, 255, 255, 255), 0.80),
            }
        };

        var k = IsActive ? 1.15 : 0.85 + 0.15 * Math.Sin(_time * 1.8);

        using (ctx.PushOpacityMask(mask, new Rect(0, 0, w, h)))
        {
            // Outer halo layers (cheap blur: wide + faint, narrow + strong), then the crisp edge.
            (double width, double alpha)[] layers = [(16, 0.08), (10, 0.15), (5, 0.32), (2.2, 1.0)];
            foreach (var (width, alpha) in layers)
            {
                var pen = new Pen(MakeBrush(rect, alpha * k), width, lineCap: PenLineCap.Round);
                ctx.DrawGeometry(null, pen, geo);
            }
        }

        // Floating particles
        foreach (var p in _particles)
        {
            var life = p.Life / p.Max;
            var a = Math.Sin(Math.PI * life) * 0.85;
            if (a <= 0.01) continue;

            var x = rect.Left + Math.Clamp(p.Nx, 0, 1) * rect.Width;
            var y = rect.Bottom - p.Rise;
            if (y < rect.Top) continue;

            var c = Sample(p.Nx + _phase);
            c = Color.FromRgb(
                (byte)(c.R + (255 - c.R) * 0.35),
                (byte)(c.G + (255 - c.G) * 0.35),
                (byte)(c.B + (255 - c.B) * 0.35));

            ctx.DrawEllipse(new SolidColorBrush(c, a * 0.25), null, new Point(x, y), p.Size * 2.4, p.Size * 2.4);
            ctx.DrawEllipse(new SolidColorBrush(c, a), null, new Point(x, y), p.Size, p.Size);
        }
    }
}