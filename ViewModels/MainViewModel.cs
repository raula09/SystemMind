using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using SystemMind.Models;
using SystemMind.Services;

namespace SystemMind.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly SystemMonitor _monitor = new();
    private readonly AdvisorService _advisor = new();
    private SystemSnapshot _snapshot = null!;
    private string _prompt = "";
    private string _activeView = "StorageExplorer"; // Default to WinDirStat view!
    private string _statusBanner = "";
    private bool _isAiThinking;
    private readonly DispatcherTimer _resourceTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private string _gpuText = "GPU: unavailable";
    private ResourceMetric _resourceMetric = ResourceMetric.Ram;

    public event PropertyChangedEventHandler? PropertyChanged;

    public StorageExplorerViewModel StorageExplorer { get; }
    public CleanupViewModel Cleanup { get; }
    public PerformanceViewModel Performance { get; }
    public SettingsViewModel Settings { get; }

    public ObservableCollection<Recommendation> Recommendations { get; } = [];
    public ObservableCollection<ProcessRow> TopProcesses { get; } = [];
    public ObservableCollection<DiskRow> DiskItems { get; } = [];
    public ObservableCollection<ChatMessage> Messages { get; } = [];
    public ObservableCollection<ResourceCategoryUsage> ResourceGroups { get; } = [];

    public string ResourceMetricName => _resourceMetric switch { ResourceMetric.Cpu => "CPU", ResourceMetric.Gpu => "GPU", _ => "RAM" };
    public string ResourceSortButtonText => $"Sort by: {ResourceMetricName}  ↻";

    public ICommand RefreshCommand { get; }
    public ICommand SendCommand { get; }
    public ICommand QuickAskCommand { get; }
    public ICommand SetViewCommand { get; }
    public ICommand ClearChatCommand { get; }
    public ICommand CycleResourceMetricCommand { get; }

    public string ActiveView
    {
        get => _activeView;
        set
        {
            if (_activeView != value)
            {
                _activeView = value;
                OnChanged();
                OnChanged(nameof(IsOverviewActive));
                OnChanged(nameof(IsStorageExplorerActive));
                OnChanged(nameof(IsCleanupActive));
                OnChanged(nameof(IsPerformanceActive));
                OnChanged(nameof(IsSettingsActive));
                OnChanged(nameof(IsLiveResourcesActive));
                OnChanged(nameof(IsCompatibilityActive));
            }
        }
    }

    public bool IsOverviewActive => ActiveView == "Overview";
    public bool IsStorageExplorerActive => ActiveView == "StorageExplorer";
    public bool IsCleanupActive => ActiveView == "Cleanup";
    public bool IsPerformanceActive => ActiveView == "Performance";
    public bool IsSettingsActive => ActiveView == "Settings";
    public bool IsLiveResourcesActive => ActiveView == "LiveResources";
    public bool IsCompatibilityActive => ActiveView == "Compatibility";

    public string Greeting => DateTime.Now.Hour < 12 ? "morning" : DateTime.Now.Hour < 18 ? "afternoon" : "evening";
    public string Prompt { get => _prompt; set { _prompt = value; OnChanged(); } }

    public bool IsAiThinking
    {
        get => _isAiThinking;
        set { if (_isAiThinking != value) { _isAiThinking = value; OnChanged(); } }
    }

    public string StatusBanner
    {
        get => _statusBanner;
        set { if (_statusBanner != value) { _statusBanner = value; OnChanged(); OnChanged(nameof(HasStatusBanner)); } }
    }

    public bool HasStatusBanner => !string.IsNullOrEmpty(StatusBanner);

    public string MemoryHeadline => $"{_snapshot?.MemoryUsedGb:F1} / {_snapshot?.MemoryTotalGb:F1} GB";
    public string MemoryDetail => $"{MemoryPercent:F0}% in use";
    public double MemoryPercent => _snapshot?.MemoryPercent ?? 0;
    public string StorageHeadline => $"{_snapshot?.DiskUsedGb:F0} / {_snapshot?.DiskTotalGb:F0} GB";
    public string StorageDetail => $"{(_snapshot != null ? _snapshot.DiskTotalGb - _snapshot.DiskUsedGb : 0):F0} GB free";
    public double StoragePercent => _snapshot?.DiskPercent ?? 0;
    public string CpuHeadline => $"{_snapshot?.CpuPercent:F0}%";
    public string CpuDetail => $"Across {Environment.ProcessorCount} logical cores";
    public double CpuPercent => _snapshot?.CpuPercent ?? 0;
    public string CpuResourceText => $"{CpuPercent:F0}%";
    public string MemoryResourceText => _snapshot == null ? "—" : $"{_snapshot.MemoryUsedGb:F1} / {_snapshot.MemoryTotalGb:F1} GB";
    public string DiskResourceText => _snapshot == null ? "—" : $"{_snapshot.DiskPercent:F0}% used";
    public double MemoryResourcePercent => _snapshot?.MemoryPercent ?? 0;
    public double DiskResourcePercent => _snapshot?.DiskPercent ?? 0;
    public string GpuText => _gpuText;
    public double GpuPercent => _snapshot?.GpuPercent ?? 0;
    public bool IsGpuAvailable => _snapshot?.GpuPercent.HasValue == true;
    public string GpuNameText => _snapshot?.Hardware?.GpuName ?? "Unavailable";
    public string GpuMemoryText => _snapshot?.Hardware?.GpuMemoryGb is { } memory ? $"{memory:F1} GB VRAM" : "VRAM unavailable";
    public string BatteryText => _snapshot?.Hardware?.BatteryPercent is { } percent ? $"{percent:F0}%" : "Unavailable";
    public string BatteryStatusText => _snapshot?.Hardware?.BatteryStatus ?? "No battery detected";
    public string BatteryHealthText => _snapshot?.Hardware?.BatteryHealthPercent is { } health ? $"{health:F0}%" : "Unavailable";
    public string ThermalText => _snapshot?.Hardware?.CpuTemperatureC is { } temperature ? $"{temperature:F0}°C" : "Unavailable";
    public string ThermalCpuText => _snapshot?.Hardware?.CpuTemperatureC is { } temperature ? $"{temperature:F0}°C" : "Unavailable";
    public string ThermalMaxText => _snapshot?.Hardware?.MaxTemperatureC is { } temperature ? $"{temperature:F0}°C" : "Unavailable";
    public string NetworkDownText => $"↓ {FormatRate(_snapshot?.Hardware?.NetworkReceiveBytesPerSecond ?? 0)}";
    public string NetworkUpText => $"↑ {FormatRate(_snapshot?.Hardware?.NetworkSendBytesPerSecond ?? 0)}";
    public string NetworkTotalInText => FormatBytes(_snapshot?.Hardware?.NetworkReceivedBytes ?? 0);
    public string NetworkTotalOutText => FormatBytes(_snapshot?.Hardware?.NetworkSentBytes ?? 0);
    public string UsageBreakdownTitle
    {
        get
        {
            var processes = _snapshot?.AllProcesses ?? [];
            return _resourceMetric switch
            {
                ResourceMetric.Cpu => $"WHO IS USING CPU · {processes.Sum(process => process.CpuPercent):F1}% COMBINED PROCESS USAGE",
                ResourceMetric.Gpu when !IsGpuAvailable => "WHO IS USING GPU · GPU COUNTERS UNAVAILABLE",
                ResourceMetric.Gpu => $"WHO IS USING GPU · {processes.Sum(process => process.GpuPercent ?? 0):F1}% COMBINED ENGINE ACTIVITY",
                _ => $"WHO IS USING MEMORY · {processes.Sum(process => process.MemoryMb) / 1024d:F1} GB TRACKED ACROSS PROCESSES"
            };
        }
    }
    public string CpuStatus => StatusFor(CpuPercent, 75, 90, "OK", "BUSY", "VERY BUSY");
    public string CpuStatusColor => ColorFor(CpuPercent, 75, 90);
    public string MemoryStatus => StatusFor(MemoryResourcePercent, 75, 90, "OK", "HIGH", "FULL");
    public string MemoryStatusColor => ColorFor(MemoryResourcePercent, 75, 90);
    public string DiskStatus => StatusFor(DiskResourcePercent, 75, 90, "OK", "BUSY", "VERY BUSY");
    public string DiskStatusColor => ColorFor(DiskResourcePercent, 75, 90);
    public string GpuStatus => !IsGpuAvailable ? "N/A" : StatusFor(GpuPercent, 75, 90, "OK", "BUSY", "VERY BUSY");
    public string GpuStatusColor => !IsGpuAvailable ? "#8FA3B8" : ColorFor(GpuPercent, 75, 90);

    public MainViewModel()
    {
        EnvLoader.Load();

        StorageExplorer = new StorageExplorerViewModel();
        Cleanup = new CleanupViewModel();
        Performance = new PerformanceViewModel();
        Settings = new SettingsViewModel();

        // Wire sub-view model events to AI and notifications
        StorageExplorer.RequestAiQuestion += q => _ = AskAsync(q);
        StorageExplorer.NotifyUser += msg => ShowBanner(msg);

        Cleanup.RequestAiQuestion += q => _ = AskAsync(q);
        Cleanup.NotifyUser += msg => ShowBanner(msg);

        Performance.NotifyUser += msg => ShowBanner(msg);
        Settings.NotifyUser += msg => ShowBanner(msg);

        RefreshCommand = new RelayCommand(Refresh);
        CycleResourceMetricCommand = new RelayCommand(CycleResourceMetric);
        SendCommand = new AsyncCommand(_ => SendAsync());
        QuickAskCommand = new AsyncCommand(p => AskAsync(p?.ToString() ?? ""));
        SetViewCommand = new RelayCommand(p => { if (p is string view) ActiveView = view; });
        ClearChatCommand = new RelayCommand(() =>
        {
            Messages.Clear();
            Messages.Add(new ChatMessage("SystemMind", "Chat history cleared. How can I help you inspect or optimize your system?", "#132C3B", HorizontalAlignment.Left));
        });

        Refresh();
        _resourceTimer.Tick += (_, _) =>
        {
            Refresh();
            if (IsPerformanceActive) Performance.Refresh();
        };
        _resourceTimer.Start();

        // Automatically start background scan of Root directory (/) on startup
        _ = StorageExplorer.StartScanAsync();

        Messages.Add(new ChatMessage(
            "SystemMind",
            "👋 Welcome! I'm your AI System & Storage Advisor.\n\n" +
            "• **Storage Explorer**: Scanning your entire root filesystem (`/`) to show every file and folder in a full WinDirStat-style treemap.\n" +
            "• **Cleanup Assistant**: Detects caches, junk files, and large dev artifacts.\n" +
            "• **Performance**: Live process manager.\n\n" +
            "I am currently scanning the entire root filesystem (`/`) in the background so you can immediately inspect all disk space hogs across the whole system!",
            "#132C3B",
            HorizontalAlignment.Left));
    }

    public void ShowBanner(string msg)
    {
        StatusBanner = msg;
        Task.Delay(5000).ContinueWith(_ =>
        {
            if (StatusBanner == msg) StatusBanner = "";
        });
    }

    public void StopMonitoring() => _resourceTimer.Stop();

    private void Refresh()
    {
        _snapshot = _monitor.Capture();
        Recommendations.Clear();

        AddRecommendation(
            _snapshot.MemoryPercent > 80,
            "Memory pressure",
            _snapshot.MemoryPercent > 80 ? "Close heavy background applications to prevent paging." : "Memory usage is within safe limits.",
            "RAM");

        AddRecommendation(
            _snapshot.DiskPercent > 80,
            "Storage running tight",
            _snapshot.DiskPercent > 80 ? "Review Downloads, caches, and large media files." : "Adequate storage headroom available.",
            "Disk");

        AddRecommendation(
            _snapshot.CpuPercent > 75,
            "CPU activity elevated",
            _snapshot.CpuPercent > 75 ? "Active compute tasks running across cores." : "CPU headroom is comfortably available.",
            "CPU");

        TopProcesses.Clear();
        foreach (var p in _snapshot.Processes)
            TopProcesses.Add(new ProcessRow(p.Name, $"{p.MemoryMb:F0} MB"));

        DiskItems.Clear();
        foreach (var d in _snapshot.Disks)
            DiskItems.Add(new DiskRow(d.Name, $"{d.UsedGb:F0} / {d.TotalGb:F0} GB", d.TotalGb == 0 ? 0 : d.UsedGb / d.TotalGb * 100));

        _gpuText = _snapshot.GpuPercent is { } gpu
            ? $"{gpu:F0}%"
            : _snapshot.Hardware?.GpuName is not null ? "N/A" : "Unavailable";
        OnChanged(nameof(GpuText));
        OnChanged(nameof(GpuPercent));
        OnChanged(nameof(IsGpuAvailable));
        OnChanged(nameof(GpuNameText));
        OnChanged(nameof(GpuMemoryText));
        OnChanged(nameof(BatteryText));
        OnChanged(nameof(BatteryStatusText));
        OnChanged(nameof(BatteryHealthText));
        OnChanged(nameof(ThermalText));
        OnChanged(nameof(ThermalCpuText));
        OnChanged(nameof(ThermalMaxText));
        OnChanged(nameof(NetworkDownText));
        OnChanged(nameof(NetworkUpText));
        OnChanged(nameof(NetworkTotalInText));
        OnChanged(nameof(NetworkTotalOutText));
        OnChanged(nameof(UsageBreakdownTitle));
        OnChanged(nameof(ResourceSortButtonText));
        OnChanged(nameof(CpuStatus));
        OnChanged(nameof(CpuStatusColor));
        OnChanged(nameof(MemoryStatus));
        OnChanged(nameof(MemoryStatusColor));
        OnChanged(nameof(DiskStatus));
        OnChanged(nameof(DiskStatusColor));
        OnChanged(nameof(GpuStatus));
        OnChanged(nameof(GpuStatusColor));
        OnChanged(nameof(CpuResourceText));
        OnChanged(nameof(MemoryResourceText));
        OnChanged(nameof(MemoryResourcePercent));
        OnChanged(nameof(DiskResourceText));
        OnChanged(nameof(DiskResourcePercent));

        RebuildResourceGroups();
        StorageExplorer.RefreshDrives();

        foreach (var name in new[]
        {
            nameof(MemoryHeadline), nameof(MemoryDetail), nameof(MemoryPercent),
            nameof(StorageHeadline), nameof(StorageDetail), nameof(StoragePercent),
            nameof(CpuHeadline), nameof(CpuDetail), nameof(CpuPercent)
        })
        {
            OnChanged(name);
        }
    }

    private void RebuildResourceGroups()
    {
        if (_snapshot == null) return;
        var expandedByCategory = ResourceGroups.ToDictionary(group => group.Name, group => group.IsExpanded, StringComparer.OrdinalIgnoreCase);
        var categoryColors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Local AI"] = "#A78BFA",
            ["Browsing"] = "#F5A044",
            ["Gaming"] = "#EF6C8F",
            ["Development"] = "#5B8FF9",
            ["System"] = "#77838F",
            ["System & other"] = "#77838F",
            ["Other"] = "#77838F"
        };
        var groupedProcesses = (_snapshot.AllProcesses ?? [])
            .GroupBy(process => GetDisplayCategory(process.Name, process.Category), StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                Name = group.Key,
                Processes = group.OrderByDescending(GetProcessMetric).ToList(),
                MemoryMb = group.Sum(process => process.MemoryMb),
                CpuPercent = group.Sum(process => process.CpuPercent),
                GpuPercent = group.Sum(process => process.GpuPercent ?? 0),
                Usage = _resourceMetric switch
                {
                    ResourceMetric.Cpu => group.Sum(process => process.CpuPercent),
                    ResourceMetric.Gpu => group.Sum(process => process.GpuPercent ?? 0),
                    _ => group.Sum(process => process.MemoryMb)
                }
            })
            .OrderByDescending(group => group.Usage)
            .ToList();
        var totalUsage = groupedProcesses.Sum(group => group.Usage);
        ResourceGroups.Clear();
        foreach (var group in groupedProcesses)
        {
            var usage = group.Usage;
            if (_resourceMetric == ResourceMetric.Ram && group.MemoryMb <= 0) continue;
            var usageText = _resourceMetric switch
            {
                ResourceMetric.Cpu => $"{group.CpuPercent:F1}%",
                ResourceMetric.Gpu when !IsGpuAvailable => "N/A",
                ResourceMetric.Gpu => $"{group.GpuPercent:F1}%",
                _ => $"{group.MemoryMb / 1024d:F1} GB"
            };
            var details = group.Processes.Select(process => new ResourceProcessDetail(
                process.Name,
                process.Id,
                $"{process.CpuPercent:F1}%",
                process.GpuPercent is { } gpu ? $"{gpu:F1}%" : "N/A",
                $"{process.MemoryMb:F1} MB")).ToList();
            var category = new ResourceCategoryUsage(
                group.Name,
                usageText,
                totalUsage <= 0 ? 0 : usage / totalUsage * 100,
                categoryColors.GetValueOrDefault(group.Name, "#77838F"),
                details);
            category.IsExpanded = expandedByCategory.GetValueOrDefault(group.Name);
            ResourceGroups.Add(category);
        }
        OnChanged(nameof(UsageBreakdownTitle));
        OnChanged(nameof(ResourceSortButtonText));
    }

    private void CycleResourceMetric()
    {
        _resourceMetric = _resourceMetric switch
        {
            ResourceMetric.Ram => ResourceMetric.Cpu,
            ResourceMetric.Cpu => ResourceMetric.Gpu,
            _ => ResourceMetric.Ram
        };
        OnChanged(nameof(ResourceMetricName));
        OnChanged(nameof(ResourceSortButtonText));
        RebuildResourceGroups();
    }

    private double GetProcessMetric(ProcessDetailedInfo process) => _resourceMetric switch
    {
        ResourceMetric.Cpu => process.CpuPercent,
        ResourceMetric.Gpu => process.GpuPercent ?? 0,
        _ => process.MemoryMb
    };

    private void AddRecommendation(bool urgent, string title, string detail, string impact) =>
        Recommendations.Add(new Recommendation(title, detail, impact, urgent ? "#FF9B70" : "#45D6A5"));

    private static string GetDisplayCategory(string processName, string category)
    {
        if (new[] { "ollama", "lmstudio", "llama", "kobold", "vllm", "text-generation-webui" }
            .Any(token => processName.Contains(token, StringComparison.OrdinalIgnoreCase))) return "Local AI";
        return category switch
        {
            "Development" => "Development",
            "Browsing" => "Browsing",
            "Gaming" => "Gaming",
            "System" or "Other" => "System & other",
            _ => category
        };
    }

    private static string StatusFor(double value, double busyAt, double fullAt, string normal, string busy, string full) =>
        value >= fullAt ? full : value >= busyAt ? busy : normal;

    private static string FormatRate(double bytesPerSecond) => $"{FormatBytes(bytesPerSecond)}/s";

    private static string FormatBytes(double bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{value:F0} {units[unit]}" : $"{value:F1} {units[unit]}";
    }

    private static string ColorFor(double value, double busyAt, double fullAt) =>
        value >= fullAt ? "#F0525F" : value >= busyAt ? "#F5B335" : "#35D3A0";

    private async Task SendAsync()
    {
        var q = Prompt.Trim();
        if (q.Length == 0) return;
        Prompt = "";
        await AskAsync(q);
    }

    private async Task AskAsync(string question)
    {
        if (string.IsNullOrWhiteSpace(question)) return;

        var lowerQ = question.ToLowerInvariant();
        if ((lowerQ.Contains("space") || lowerQ.Contains("taking") || lowerQ.Contains("breakdown") || lowerQ.Contains("scan") || lowerQ.Contains("clean"))
            && StorageExplorer.RootNode == null && !StorageExplorer.IsScanning)
        {
            _ = StorageExplorer.StartScanAsync();
        }

        Messages.Add(new ChatMessage("You", question, "#183E50", HorizontalAlignment.Right));
        IsAiThinking = true;

        try
        {
            var answer = await _advisor.AskAsync(
                question,
                _snapshot,
                StorageExplorer.RootNode,
                StorageExplorer.Extensions.ToList(),
                StorageExplorer.IsScanning,
                StorageExplorer.ScanPath,
                StorageExplorer.TotalFilesScanned,
                StorageExplorer.TotalBytesScanned);

            var aiMessage = new ChatMessage("SystemMind", answer, "#132C3B", HorizontalAlignment.Left, animate: true);
            Messages.Add(aiMessage);
            _ = aiMessage.AnimateAsync();
        }
        catch (Exception ex)
        {
            Messages.Add(new ChatMessage("SystemMind", $"Error: {ex.Message}", "#381B1B", HorizontalAlignment.Left));
        }
        finally
        {
            IsAiThinking = false;
        }
    }

    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public record Recommendation(string Title, string Detail, string Impact, string Accent);
public record ProcessRow(string Name, string Memory);
public record DiskRow(string Name, string Value, double Percent, string MountPoint = "");
public enum ResourceMetric { Ram, Cpu, Gpu }
public record ResourceProcessDetail(string Name, int Id, string CpuText, string GpuText, string MemoryText);

public sealed class ChatMessage : INotifyPropertyChanged
{
    private string _displayText = "";

    public ChatMessage(string author, string text, string background, HorizontalAlignment alignment, bool animate = false)
    {
        Author = author;
        Text = text;
        Background = background;
        Alignment = alignment;
        DisplayText = animate ? string.Empty : text;
    }

    public string Author { get; }
    public string Text { get; }
    public string Background { get; }
    public HorizontalAlignment Alignment { get; }

    public string DisplayText
    {
        get => _displayText;
        set
        {
            if (_displayText != value)
            {
                _displayText = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayText)));
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public async Task AnimateAsync()
    {
        if (string.IsNullOrEmpty(Text) || Author != "SystemMind")
        {
            DisplayText = Text;
            return;
        }

        var final = Text;
        DisplayText = string.Empty;

        for (var i = 1; i <= final.Length; i++)
        {
            var next = final.Substring(0, i);
            await Dispatcher.UIThread.InvokeAsync(() => DisplayText = next);
            await Task.Delay(12);
        }
    }
}

public sealed class ResourceCategoryUsage : INotifyPropertyChanged
{
    private bool _isExpanded;

    public string Name { get; }
    public string UsageText { get; }
    public double Percent { get; }
    public string Color { get; }
    public IReadOnlyList<ResourceProcessDetail> Processes { get; }
    public ICommand ToggleExpandedCommand { get; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ToggleText)));
        }
    }

    public string ToggleText => IsExpanded ? "Hide processes" : "Show processes";
    public event PropertyChangedEventHandler? PropertyChanged;

    public ResourceCategoryUsage(string name, string usageText, double percent, string color, IReadOnlyList<ResourceProcessDetail> processes)
    {
        Name = name;
        UsageText = usageText;
        Percent = percent;
        Color = color;
        Processes = processes;
        ToggleExpandedCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
    }
}

public sealed class RelayCommand(Action<object?> action) : ICommand
{
    public RelayCommand(Action action) : this(_ => action()) { }
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? p) => true;
    public void Execute(object? p) => action(p);
}

public sealed class AsyncCommand(Func<object?, Task> action) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? p) => true;
    public async void Execute(object? p) => await action(p);
}
