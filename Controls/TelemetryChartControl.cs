using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace DiagnoX.Controls;

public sealed class TelemetryChartControl : Control
{
    public static readonly StyledProperty<IEnumerable<double>> ValuesProperty =
        AvaloniaProperty.Register<TelemetryChartControl, IEnumerable<double>>(nameof(Values), Array.Empty<double>());

    public static readonly StyledProperty<Color> StrokeProperty =
        AvaloniaProperty.Register<TelemetryChartControl, Color>(nameof(Stroke), Color.Parse("#4D96F0"));

    public IEnumerable<double> Values
    {
        get => GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value ?? Array.Empty<double>());
    }

    public Color Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    static TelemetryChartControl()
    {
        AffectsRender<TelemetryChartControl>(ValuesProperty, StrokeProperty);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var bounds = Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return;

        var values = Values as IList<double> ?? new List<double>(Values);
        if (values.Count == 0)
            return;

        var min = double.MaxValue;
        var max = double.MinValue;
        foreach (var value in values)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                continue;

            min = Math.Min(min, value);
            max = Math.Max(max, value);
        }

        if (max <= min)
        {
            max = min + 1;
        }

        var pen = new Pen(new SolidColorBrush(Stroke), 2d);
        var geom = new StreamGeometry();
        using (var gc = geom.Open())
        {
            double width = bounds.Width;
            double height = bounds.Height;
            double padding = 4;
            int count = values.Count;

            for (int i = 0; i < count; i++)
            {
                double raw = values[i];
                if (double.IsNaN(raw) || double.IsInfinity(raw))
                    continue;

                double x = padding + (i / Math.Max(1, count - 1)) * (width - padding * 2);
                double y = height - padding - ((raw - min) / (max - min)) * (height - padding * 2);

                if (i == 0)
                {
                    gc.BeginFigure(new Point(x, y), false);
                }
                else
                {
                    gc.LineTo(new Point(x, y));
                }
            }
        }

        context.DrawGeometry(null, pen, geom);

        var baseline = new Pen(new SolidColorBrush(Color.FromArgb(80, 160, 177, 191)), 1);
        context.DrawLine(baseline, new Point(0, bounds.Height - 1), new Point(bounds.Width, bounds.Height - 1));
    }
}
