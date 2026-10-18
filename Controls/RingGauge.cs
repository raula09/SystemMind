using System;
using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace DiagnoX.Controls;

/// <summary>
/// Donut gauge with a centred value and a small caption beneath it.
/// Bind Value (0..Maximum) directly, or bind only ValueText (e.g. "53.8%" or "69°C"):
/// when Value is not set the first number found in ValueText is used for the arc.
/// </summary>
public sealed class RingGauge : Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<RingGauge, double>(nameof(Value), double.NaN);

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<RingGauge, double>(nameof(Maximum), 100);

    public static readonly StyledProperty<double> ThicknessProperty =
        AvaloniaProperty.Register<RingGauge, double>(nameof(Thickness), 9);

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<RingGauge, IBrush?>(nameof(Stroke), new SolidColorBrush(Color.Parse("#4D96F0")));

    public static readonly StyledProperty<IBrush?> TrackProperty =
        AvaloniaProperty.Register<RingGauge, IBrush?>(nameof(Track), new SolidColorBrush(Color.Parse("#2C323A")));

    public static readonly StyledProperty<string?> ValueTextProperty =
        AvaloniaProperty.Register<RingGauge, string?>(nameof(ValueText));

    public static readonly StyledProperty<string?> CaptionProperty =
        AvaloniaProperty.Register<RingGauge, string?>(nameof(Caption));

    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double Thickness { get => GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }
    public IBrush? Stroke { get => GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public IBrush? Track { get => GetValue(TrackProperty); set => SetValue(TrackProperty, value); }
    public string? ValueText { get => GetValue(ValueTextProperty); set => SetValue(ValueTextProperty, value); }
    public string? Caption { get => GetValue(CaptionProperty); set => SetValue(CaptionProperty, value); }

    private static readonly Regex NumberPattern = new(@"-?\d+(?:[.,]\d+)?", RegexOptions.Compiled);
    private static readonly IBrush MainBrush = new SolidColorBrush(Color.Parse("#F2F4F6"));
    private static readonly IBrush CaptionBrush = new SolidColorBrush(Color.Parse("#8A929C"));

    static RingGauge()
    {
        AffectsRender<RingGauge>(ValueProperty, MaximumProperty, ThicknessProperty, StrokeProperty,
            TrackProperty, ValueTextProperty, CaptionProperty);
    }

    private double ResolveValue()
    {
        if (!double.IsNaN(Value)) return Value;

        var text = ValueText;
        if (string.IsNullOrWhiteSpace(text)) return double.NaN;

        var m = NumberPattern.Match(text);
        if (!m.Success) return double.NaN;

        return double.TryParse(m.Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v
            : double.NaN;
    }

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width, h = Bounds.Height;
        double size = Math.Min(w, h);
        if (size < 24) return;

        double t = Math.Min(Thickness, size / 3);
        double r = (size - t) / 2;
        var c = new Point(w / 2, h / 2);

        if (Track is { } track)
            ctx.DrawEllipse(null, new Pen(track, t), c, r, r);

        double v = ResolveValue();
        double max = Maximum <= 0 ? 100 : Maximum;

        if (!double.IsNaN(v) && v > 0 && Stroke is { } stroke)
        {
            double frac = Math.Clamp(v / max, 0, 1);
            var pen = new Pen(stroke, t, lineCap: PenLineCap.Round);

            if (frac >= 0.999)
            {
                ctx.DrawEllipse(null, pen, c, r, r);
            }
            else
            {
                double a0 = -Math.PI / 2;
                double a1 = a0 + frac * Math.PI * 2;
                var start = new Point(c.X + r * Math.Cos(a0), c.Y + r * Math.Sin(a0));
                var end = new Point(c.X + r * Math.Cos(a1), c.Y + r * Math.Sin(a1));

                var geo = new StreamGeometry();
                using (var g = geo.Open())
                {
                    g.BeginFigure(start, false);
                    g.ArcTo(end, new Size(r, r), 0, frac > 0.5, SweepDirection.Clockwise);
                    g.EndFigure(false);
                }
                ctx.DrawGeometry(null, pen, geo);
            }
        }

        // Centre text
        string main = !string.IsNullOrEmpty(ValueText)
            ? ValueText!
            : double.IsNaN(v) ? "—" : v.ToString("0.#", CultureInfo.InvariantCulture) + "%";

        var family = FontFamily.Default;
        var mainTypeface = new Typeface(family, FontStyle.Normal, FontWeight.SemiBold);
        var mainFt = new FormattedText(main, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            mainTypeface, size * 0.19, MainBrush);

        // Shrink long values (e.g. "N/A", "100.0%") so they stay inside the ring instead of spilling over it.
        double innerWidth = (2 * r - t) * 0.82;
        if (mainFt.Width > innerWidth)
        {
            mainFt = new FormattedText(main, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                mainTypeface, Math.Max(8, size * 0.19 * innerWidth / mainFt.Width), MainBrush);
        }

        FormattedText? capFt = null;
        if (!string.IsNullOrEmpty(Caption))
        {
            capFt = new FormattedText(Caption!, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(family, FontStyle.Normal, FontWeight.Medium), size * 0.105, CaptionBrush);
        }

        double total = mainFt.Height + (capFt?.Height ?? 0);
        double y = c.Y - total / 2;
        ctx.DrawText(mainFt, new Point(c.X - mainFt.Width / 2, y));
        if (capFt != null)
            ctx.DrawText(capFt, new Point(c.X - capFt.Width / 2, y + mainFt.Height));
    }
}
