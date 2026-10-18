using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace DiagnoX.Controls;

/// <summary>
/// Animated particle sphere used as the assistant's presence indicator.
/// Idle: slow rotation with a gentle breathing surface.
/// Active (IsActive = true): faster spin, turbulent surface, pulse rings and a brighter glow.
/// </summary>
public sealed class AssistantSphere : Control
{
    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<AssistantSphere, bool>(nameof(IsActive));

    public static readonly StyledProperty<bool> IsEngagedProperty =
        AvaloniaProperty.Register<AssistantSphere, bool>(nameof(IsEngaged));

    public static readonly StyledProperty<Color> AccentProperty =
        AvaloniaProperty.Register<AssistantSphere, Color>(nameof(Accent), Color.Parse("#4CC2FF"));

    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    /// <summary>Gives the idle sphere a responsive pulse while the user is composing a prompt.</summary>
    public bool IsEngaged
    {
        get => GetValue(IsEngagedProperty);
        set => SetValue(IsEngagedProperty, value);
    }

    public Color Accent
    {
        get => GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    private const int PointCount = 340;
    private const int Steps = 14;

    private readonly double[] _px = new double[PointCount];
    private readonly double[] _py = new double[PointCount];
    private readonly double[] _pz = new double[PointCount];
    private readonly DispatcherTimer _timer;

    private SolidColorBrush[] _brushes = Array.Empty<SolidColorBrush>();
    private Color _brushAccent;
    private double _time;
    private double _energy;
    private DateTime _last = DateTime.UtcNow;

    static AssistantSphere()
    {
        AffectsRender<AssistantSphere>(IsActiveProperty, IsEngagedProperty, AccentProperty);
    }

    public AssistantSphere()
    {
        IsHitTestVisible = false;

        // Fibonacci lattice: evenly distributed points on a unit sphere.
        double golden = Math.PI * (3 - Math.Sqrt(5));
        for (int i = 0; i < PointCount; i++)
        {
            double y = 1 - 2 * (i + 0.5) / PointCount;
            double r = Math.Sqrt(Math.Max(0, 1 - y * y));
            double theta = golden * i;
            _px[i] = Math.Cos(theta) * r;
            _py[i] = y;
            _pz[i] = Math.Sin(theta) * r;
        }

        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += (_, _) => Step();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _last = DateTime.UtcNow;
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    private void Step()
    {
        var now = DateTime.UtcNow;
        double dt = Math.Min(0.05, (now - _last).TotalSeconds);
        _last = now;
        _time += dt;

        double target = IsActive ? 1.0 : IsEngaged ? 0.38 : 0.0;
        _energy += (target - _energy) * Math.Min(1.0, dt * 3.5);
        InvalidateVisual();
    }

    private static Color Mix(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromRgb(
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
    }

    private void EnsureBrushes(Color accent)
    {
        if (_brushes.Length == Steps && _brushAccent == accent) return;
        _brushAccent = accent;
        _brushes = new SolidColorBrush[Steps];
        for (int k = 0; k < Steps; k++)
        {
            double d = k / (double)(Steps - 1);
            var c = Mix(accent, Colors.White, d * 0.55);
            byte alpha = (byte)(26 + 229 * Math.Pow(d, 1.3));
            _brushes[k] = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
        }
    }

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w < 8 || h < 8) return;

        double cx = w / 2, cy = h / 2;
        double e = _energy, t = _time;
        var accent = Accent;
        EnsureBrushes(accent);

        double breath = 1 + 0.025 * Math.Sin(t * 1.4) + e * 0.04 * Math.Sin(t * 7.0);
        double R = Math.Min(w, h) * 0.30 * breath;
        var center = new Point(cx, cy);

        // Ambient glow
        double gr = R * (1.6 + 0.25 * e);
        var glow = new RadialGradientBrush
        {
            RadiusX = new RelativeScalar(0.5, RelativeUnit.Relative),
            RadiusY = new RelativeScalar(0.5, RelativeUnit.Relative)
        };
        glow.GradientStops.Add(new GradientStop { Color = Color.FromArgb((byte)(60 + 90 * e), accent.R, accent.G, accent.B), Offset = 0 });
        glow.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0, accent.R, accent.G, accent.B), Offset = 1 });
        ctx.DrawEllipse(glow, null, center, gr, gr);

        // Pulse rings while the assistant is working
        if (e > 0.02)
        {
            for (int k = 0; k < 2; k++)
            {
                double phase = (t * 0.8 + k * 0.5) % 1.0;
                double pr = R * (1.0 + 0.55 * phase);
                byte pa = (byte)(Math.Clamp((1 - phase) * 110 * e, 0, 255));
                ctx.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(pa, accent.R, accent.G, accent.B)), 1), center, pr, pr);
            }
        }

        // Core body with an offset highlight for volume
        var core = new RadialGradientBrush
        {
            Center = new RelativePoint(0.38, 0.32, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(0.38, 0.32, RelativeUnit.Relative),
            RadiusX = new RelativeScalar(0.85, RelativeUnit.Relative),
            RadiusY = new RelativeScalar(0.85, RelativeUnit.Relative)
        };
        core.GradientStops.Add(new GradientStop { Color = Color.FromArgb(78, accent.R, accent.G, accent.B), Offset = 0 });
        core.GradientStops.Add(new GradientStop { Color = Color.FromArgb(20, accent.R, accent.G, accent.B), Offset = 0.6 });
        core.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0, 0, 0, 0), Offset = 1 });
        var rim = new Pen(new SolidColorBrush(Color.FromArgb((byte)(40 + 50 * e), accent.R, accent.G, accent.B)), 1);
        ctx.DrawEllipse(core, rim, center, R * 0.98, R * 0.98);

        // Orbit ring (back half hidden behind the sphere is implied by drawing order: ring first)
        double ringAngle = -0.5 + t * 0.15 * (1 + 3 * e);
        var ringMatrix = Matrix.CreateRotation(ringAngle) * Matrix.CreateTranslation(cx, cy);
        var ringPen = new Pen(new SolidColorBrush(Color.FromArgb((byte)(34 + 60 * e), accent.R, accent.G, accent.B)), 1);

        // Particle surface
        double rotY = t * (0.22 + 1.4 * e);
        const double tilt = 0.42;
        double cY = Math.Cos(rotY), sY = Math.Sin(rotY);
        double cT = Math.Cos(tilt), sT = Math.Sin(tilt);
        double sizeScale = Math.Max(0.7, R / 26.0);
        double turb = 0.035 + 0.11 * e;
        double speedA = 1.1 + 2.2 * e, speedB = 0.9 + 1.8 * e;

        for (int i = 0; i < PointCount; i++)
        {
            double x = _px[i], y = _py[i], z = _pz[i];

            double n = Math.Sin(x * 3.1 + t * speedA) * Math.Cos(y * 2.7 - t * speedB) * Math.Sin(z * 3.3 + t * 0.7);
            double d = 1 + turb * n;
            x *= d; y *= d; z *= d;

            double x1 = x * cY + z * sY;
            double z1 = -x * sY + z * cY;
            double y2 = y * cT - z1 * sT;
            double z2 = y * sT + z1 * cT;

            double persp = 1.0 / (1.0 - z2 * 0.22);
            double sx = cx + x1 * R * persp;
            double sy = cy + y2 * R * persp;

            double depth = Math.Clamp((z2 + 1) * 0.5, 0, 1);
            int bi = Math.Clamp((int)(depth * (Steps - 1)), 0, Steps - 1);
            double size = (0.55 + 1.45 * depth) * sizeScale * 0.75;

            ctx.DrawEllipse(_brushes[bi], null, new Point(sx, sy), size, size);
        }

        // Orbit ring + satellite drawn on top for a layered look
        using (ctx.PushTransform(ringMatrix))
        {
            double rx = R * 1.55, ry = R * 0.42;
            ctx.DrawEllipse(null, ringPen, new Point(0, 0), rx, ry);

            double a = t * (0.9 + 2.4 * e);
            var dot = new Point(Math.Cos(a) * rx, Math.Sin(a) * ry);
            var bright = Mix(accent, Colors.White, 0.7);
            ctx.DrawEllipse(new SolidColorBrush(Color.FromArgb(230, bright.R, bright.G, bright.B)), null, dot, 1.8 * sizeScale * 0.8, 1.8 * sizeScale * 0.8);
        }
    }
}
