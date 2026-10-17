using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using SystemMind.Models;

namespace SystemMind.Controls;

public sealed class TreemapControl : Control
{
    public static readonly StyledProperty<IReadOnlyList<TreemapItem>?> ItemsProperty =
        AvaloniaProperty.Register<TreemapControl, IReadOnlyList<TreemapItem>?>(nameof(Items));

    public static readonly StyledProperty<TreemapItem?> SelectedItemProperty =
        AvaloniaProperty.Register<TreemapControl, TreemapItem?>(nameof(SelectedItem));

    public static readonly StyledProperty<TreemapItem?> HoveredItemProperty =
        AvaloniaProperty.Register<TreemapControl, TreemapItem?>(nameof(HoveredItem));

    public IReadOnlyList<TreemapItem>? Items
    {
        get => GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public TreemapItem? SelectedItem
    {
        get => GetValue(SelectedItemProperty);
        set => SetValue(SelectedItemProperty, value);
    }

    public TreemapItem? HoveredItem
    {
        get => GetValue(HoveredItemProperty);
        set => SetValue(HoveredItemProperty, value);
    }

    public event EventHandler<TreemapItem>? NodeSelected;
    public event EventHandler<TreemapItem>? NodeDoubleClicked;

    private readonly Dictionary<string, (IBrush Fill, IPen Pen)> _brushCache = new();
    private static readonly IPen HoverPen = new Pen(new SolidColorBrush(Color.FromRgb(255, 255, 255)), 1.5);
    private static readonly IPen SelectedPen = new Pen(new SolidColorBrush(Color.FromRgb(53, 211, 160)), 2.5);
    private static readonly IPen DefaultBorderPen = new Pen(new SolidColorBrush(Color.FromArgb(90, 7, 16, 25)), 1.0);
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.FromArgb(240, 255, 255, 255));
    private static readonly IBrush SubTextBrush = new SolidColorBrush(Color.FromArgb(180, 200, 220, 240));

    static TreemapControl()
    {
        AffectsRender<TreemapControl>(ItemsProperty, SelectedItemProperty, HoveredItemProperty);
    }

    public TreemapControl()
    {
        ClipToBounds = true;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var bounds = Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        // Draw dark background
        context.DrawRectangle(
            new SolidColorBrush(Color.FromRgb(11, 21, 33)),
            null,
            new Rect(0, 0, bounds.Width, bounds.Height));

        var items = Items;
        if (items == null || items.Count == 0)
        {
            var noDataText = new FormattedText(
                "Scan a directory to generate the WinDirStat treemap visualization",
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                Typeface.Default,
                14,
                new SolidColorBrush(Color.FromRgb(145, 161, 181)));

            var x = Math.Max(0, (bounds.Width - noDataText.Width) / 2);
            var y = Math.Max(0, (bounds.Height - noDataText.Height) / 2);
            context.DrawText(noDataText, new Point(x, y));
            return;
        }

        TreemapItem? hoveredToDrawLast = null;
        TreemapItem? selectedToDrawLast = null;

        // Render each block
        foreach (var item in items)
        {
            if (item.Width <= 0.5 || item.Height <= 0.5) continue;

            if (item == SelectedItem)
            {
                selectedToDrawLast = item;
                continue;
            }

            if (item == HoveredItem)
            {
                hoveredToDrawLast = item;
                continue;
            }

            DrawItem(context, item, DefaultBorderPen);
        }

        // Draw hovered item on top
        if (hoveredToDrawLast != null && hoveredToDrawLast != SelectedItem)
        {
            DrawItem(context, hoveredToDrawLast, HoverPen);
        }

        // Draw selected item on very top
        if (selectedToDrawLast != null)
        {
            DrawItem(context, selectedToDrawLast, SelectedPen);
        }
    }

    private void DrawItem(DrawingContext context, TreemapItem item, IPen borderPen)
    {
        var rect = new Rect(item.X, item.Y, item.Width, item.Height);

        // Get or cache brush
        if (!_brushCache.TryGetValue(item.ColorHex, out var cached))
        {
            try
            {
                var color = Color.Parse(item.ColorHex);
                // Cushion/depth shading
                var brush = new SolidColorBrush(color);
                cached = (brush, borderPen);
                _brushCache[item.ColorHex] = cached;
            }
            catch
            {
                cached = (new SolidColorBrush(Color.FromRgb(60, 80, 100)), borderPen);
            }
        }

        context.DrawRectangle(cached.Fill, borderPen, rect);

        // If rectangle is large enough, render text labels
        if (item.Width > 55 && item.Height > 24)
        {
            var text = item.Label;
            if (text.Length > 20) text = text[..17] + "...";

            var labelFormatted = new FormattedText(
                text,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold),
                11,
                TextBrush);

            context.DrawText(labelFormatted, new Point(item.X + 4, item.Y + 3));

            if (item.Height > 38 && item.Width > 55)
            {
                var sizeFormatted = new FormattedText(
                    item.FormattedSize,
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    Typeface.Default,
                    10,
                    SubTextBrush);

                context.DrawText(sizeFormatted, new Point(item.X + 4, item.Y + 18));
            }
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pt = e.GetPosition(this);
        var hit = FindItemAt(pt.X, pt.Y);
        if (hit != HoveredItem)
        {
            HoveredItem = hit;
            InvalidateVisual();
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (HoveredItem != null)
        {
            HoveredItem = null;
            InvalidateVisual();
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var pt = e.GetPosition(this);
        var hit = FindItemAt(pt.X, pt.Y);
        if (hit != null)
        {
            SelectedItem = hit;
            NodeSelected?.Invoke(this, hit);

            if (e.ClickCount >= 2)
            {
                NodeDoubleClicked?.Invoke(this, hit);
            }

            InvalidateVisual();
        }
    }

    private TreemapItem? FindItemAt(double x, double y)
    {
        var items = Items;
        if (items == null) return null;

        // Traverse backwards so top-drawn items hit first
        for (int i = items.Count - 1; i >= 0; i--)
        {
            if (items[i].Contains(x, y))
            {
                return items[i];
            }
        }

        return null;
    }
}
