using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using SystemMind.Controls;
using SystemMind.ViewModels;

namespace SystemMind;

public sealed record DetailRow(string Label, string Value);

public partial class MainWindow : Window
{
    private TreemapControl? _treemap;

    private Border? _drawer;
    private Border? _swatch;
    private TextBlock? _title;
    private TextBlock? _subtitle;
    private TextBlock? _explain;
    private ItemsControl? _rowsHost;

    private string _currentCard = "";

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);

        _treemap = this.FindControl<TreemapControl>("TreemapDisplay");
        if (_treemap != null)
        {
            _treemap.SizeChanged += (s, e) =>
            {
                if (DataContext is MainViewModel vm && e.NewSize.Width > 10 && e.NewSize.Height > 10)
                {
                    vm.StorageExplorer.UpdateTreemapSize(e.NewSize.Width, e.NewSize.Height);
                }
            };
        }

        _drawer = this.FindControl<Border>("DetailDrawer");
        _swatch = this.FindControl<Border>("DetailSwatch");
        _title = this.FindControl<TextBlock>("DetailTitle");
        _subtitle = this.FindControl<TextBlock>("DetailSubtitle");
        _explain = this.FindControl<TextBlock>("DetailExplain");
        _rowsHost = this.FindControl<ItemsControl>("DetailRows");

        Closed += (_, _) => (DataContext as MainViewModel)?.StopMonitoring();
    }

    // =====================================================================
    //  Card click / drawer open-close
    // =====================================================================

    private void DashboardCard_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { Tag: string tag } card) return;
        if (!e.GetCurrentPoint(card).Properties.IsLeftButtonPressed) return;

        ShowDetail(tag);
        e.Handled = true;
    }

    private void DetailClose_OnClick(object? sender, RoutedEventArgs e) => CloseDrawer();

    private void CloseDrawer()
    {
        if (_drawer != null) _drawer.IsVisible = false;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _drawer is { IsVisible: true })
        {
            CloseDrawer();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    private void DetailAsk_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not { } vm || string.IsNullOrEmpty(_currentCard)) return;

        var question = _currentCard switch
        {
            "CPU" => "Analyse my CPU usage. Is it normal, and which processes could be responsible?",
            "Memory" => "Why is my RAM being used? Is the current usage healthy?",
            "Disk" => "What is taking up my disk space and what can I safely clean up?",
            "Network" => "Explain my current network activity. Is anything unusual?",
            "Thermal" => "Are my system temperatures healthy? What can I do to lower them?",
            "Battery" => "How healthy is my battery and how can I extend its life?",
            "GPU" => "Explain my current GPU load and memory usage.",
            "Insights" => "Walk me through the current insights and which ones I should act on first.",
            _ => "Give me an overview of my system."
        };

        if (vm.GetType().GetProperty("QuickAskCommand")?.GetValue(vm) is ICommand cmd && cmd.CanExecute(question))
        {
            cmd.Execute(question);
            CloseDrawer();
        }
    }

    // =====================================================================
    //  Drawer content
    // =====================================================================

    private void ShowDetail(string tag)
    {
        var vm = DataContext;
        if (vm is null || _drawer is null) return;

        _currentCard = tag;
        var rows = new List<DetailRow>();
        string subtitle = "", explain = "", color = "#4D96F0";

        switch (tag)
        {
            case "CPU":
                {
                    color = "#4D96F0";
                    subtitle = P(vm, "CpuHeadline");
                    var hist = History(vm, "CpuHistory");
                    rows.Add(new("Utilisation", P(vm, "CpuHeadline")));
                    rows.Add(new("Status", Level(hist.Count > 0 ? hist[^1] : double.NaN, 50, 85)));
                    rows.Add(new("Hardware", P(vm, "CpuDetail")));
                    rows.AddRange(HistoryStats(hist, "%"));
                    explain = "Share of total processor time in use, sampled every 2 seconds. Brief spikes are normal. " +
                              "Sustained usage above ~85% with no heavy task running usually means a runaway process; " +
                              "open the Processes view to find it.";
                    break;
                }

            case "Memory":
                {
                    color = "#F2994A";
                    subtitle = P(vm, "MemoryHeadline");
                    var hist = History(vm, "MemoryHistory");
                    var pct = ToDouble(Raw(vm, "MemoryPercent"));
                    rows.Add(new("Usage", Fmt(pct, "F1", "%")));
                    rows.Add(new("Status", Level(pct, 70, 90)));
                    rows.Add(new("In use", P(vm, "MemoryHeadline")));
                    rows.Add(new("Breakdown", P(vm, "MemoryDetail")));
                    rows.AddRange(HistoryStats(hist, "%"));
                    explain = "Physical RAM currently held by programs and the OS. Modern systems deliberately use spare RAM " +
                              "for caching, so high usage alone is fine. It becomes a problem when swap grows and the system " +
                              "slows down; close heavy apps or check the Processes view.";
                    break;
                }

            case "Disk":
                {
                    color = "#3FCF6B";
                    subtitle = P(vm, "StorageHeadline");
                    var pct = ToDouble(Raw(vm, "StoragePercent"));
                    rows.Add(new("Usage", Fmt(pct, "F1", "%")));
                    rows.Add(new("Status", Level(pct, 75, 90)));
                    rows.Add(new("Summary", P(vm, "StorageHeadline")));
                    rows.Add(new("Detail", P(vm, "StorageDetail")));
                    var items = Items(vm, "DiskItems").ToList();
                    rows.Add(new("Volumes", items.Count.ToString(CultureInfo.InvariantCulture)));
                    foreach (var item in items)
                        rows.Add(new(P(item, "Name"), $"{P(item, "Value")}  ({Fmt(ToDouble(Raw(item, "Percent")), "F0", "%")})"));
                    explain = "Used versus total capacity per mounted volume. Above ~90% full, performance and updates can suffer. " +
                              "Use the Storage view to see which folders are largest, or Cleanup to reclaim caches safely.";
                    break;
                }

            case "Network":
                {
                    color = "#B65CF0";
                    subtitle = "Live throughput";
                    rows.Add(new("Download", P(vm, "NetworkDownText")));
                    rows.Add(new("Upload", P(vm, "NetworkUpText")));
                    rows.Add(new("Total received", P(vm, "NetworkTotalInText")));
                    rows.Add(new("Total sent", P(vm, "NetworkTotalOutText")));
                    explain = "Current transfer speed and cumulative traffic since the app started. Constant upload with no obvious " +
                              "cause (cloud sync, backups, uploads) is worth investigating.";
                    break;
                }

            case "Thermal":
                {
                    color = "#F2994A";
                    subtitle = P(vm, "ThermalText");
                    rows.Add(new("Current", P(vm, "ThermalText")));
                    rows.Add(new("CPU", P(vm, "ThermalCpuText")));
                    rows.Add(new("Hottest sensor", P(vm, "ThermalMaxText")));
                    explain = "Temperatures reported by hardware sensors. Under load, 70–85 °C is common for CPUs; sustained readings " +
                              "above ~90 °C can cause throttling. Check fans, dust, airflow and background load if it stays high.";
                    break;
                }

            case "Battery":
                {
                    color = "#3FCF6B";
                    subtitle = P(vm, "BatteryText");
                    rows.Add(new("Charge", P(vm, "BatteryText")));
                    rows.Add(new("Status", P(vm, "BatteryStatusText")));
                    rows.Add(new("Health", P(vm, "BatteryHealthText")));
                    explain = "Current charge, charging state and estimated capacity versus the original design. Health below ~80% " +
                              "means noticeably shorter runtime. Avoiding constant 100% charge and high heat slows wear.";
                    break;
                }

            case "GPU":
                {
                    color = "#4D96F0";
                    subtitle = P(vm, "GpuNameText");
                    rows.Add(new("Load", P(vm, "GpuText")));
                    rows.Add(new("Device", P(vm, "GpuNameText")));
                    rows.Add(new("Memory", P(vm, "GpuMemoryText")));
                    explain = "Graphics processor utilisation and video memory. Spikes are normal during games, video work or " +
                              "hardware-accelerated apps; high load while idle may point to a background process.";
                    break;
                }

            case "Insights":
                {
                    color = "#F5C84C";
                    var recs = Items(vm, "Recommendations").ToList();
                    subtitle = $"{recs.Count} recommendation{(recs.Count == 1 ? "" : "s")}";
                    if (recs.Count == 0)
                        rows.Add(new("All clear", "No recommendations right now"));
                    foreach (var r in recs)
                        rows.Add(new(P(r, "Title"), P(r, "Detail")));
                    explain = "Suggestions generated from current CPU, memory and disk readings. Act on the highest-severity items first; " +
                              "use Ask assistant for step-by-step help.";
                    break;
                }

            default:
                rows.Add(new("Info", "No details available"));
                break;
        }

        if (_title != null) _title.Text = tag;
        if (_subtitle != null) _subtitle.Text = subtitle;
        if (_explain != null) _explain.Text = explain;
        if (_swatch != null) _swatch.Background = Brush.Parse(color);
        if (_rowsHost != null) _rowsHost.ItemsSource = rows;

        _drawer.IsVisible = true;
    }

    // =====================================================================
    //  Reflection / formatting helpers
    // =====================================================================

    private static object? Raw(object? obj, string name) =>
        obj?.GetType().GetProperty(name)?.GetValue(obj);

    private static string P(object? obj, string name)
    {
        var v = Raw(obj, name);
        var s = v switch
        {
            null => "",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => v.ToString() ?? ""
        };
        return string.IsNullOrWhiteSpace(s) ? "—" : s;
    }

    private static IEnumerable<object> Items(object? obj, string name) =>
        Raw(obj, name) is IEnumerable e ? e.Cast<object>().Where(x => x != null) : Enumerable.Empty<object>();

    private static double ToDouble(object? v)
    {
        try { return v is null ? double.NaN : Convert.ToDouble(v, CultureInfo.InvariantCulture); }
        catch { return double.NaN; }
    }

    private static string Fmt(double v, string format, string suffix) =>
        double.IsNaN(v) ? "—" : v.ToString(format, CultureInfo.InvariantCulture) + suffix;

    private static List<double> History(object? vm, string name) =>
        Items(vm, name).Select(ToDouble).Where(d => !double.IsNaN(d)).ToList();

    private static string Level(double v, double warn, double crit)
    {
        if (double.IsNaN(v)) return "—";
        if (v >= crit) return "Critical";
        if (v >= warn) return "Elevated";
        return "Healthy";
    }

    private static IEnumerable<DetailRow> HistoryStats(List<double> h, string unit)
    {
        if (h.Count == 0) yield break;

        yield return new("Average", Fmt(h.Average(), "F1", unit));
        yield return new("Peak", Fmt(h.Max(), "F1", unit));
        yield return new("Lowest", Fmt(h.Min(), "F1", unit));

        if (h.Count >= 10)
        {
            var recent = h.Skip(h.Count - 5).Average();
            var before = h.Skip(Math.Max(0, h.Count - 10)).Take(5).Average();
            var delta = recent - before;
            yield return new("Trend", Math.Abs(delta) < 2 ? "Stable" : delta > 0 ? "Rising" : "Falling");
        }

        var seconds = h.Count * 2;
        yield return new("Window",
            seconds >= 60 ? $"{seconds / 60}m {seconds % 60}s ({h.Count} samples)" : $"{seconds}s ({h.Count} samples)");
    }
}