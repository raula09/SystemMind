using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using DiagnoX.Models;
using DiagnoX.Services;

namespace DiagnoX.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly SystemMonitor _monitor = new();
    private readonly AdvisorService _advisor = new();
    private SystemSnapshot _snapshot = null!;
    private string _prompt = "";
    private string _activeView = "Overview";
    private string _statusBanner = "";
    private string _selectedDashboardCard = "";
    private bool _isAiThinking;
    private readonly DispatcherTimer _resourceTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private string _gpuText = "GPU: unavailable";
    private ResourceMetric _resourceMetric = ResourceMetric.Ram;
    private const int HistoryLength = 60;
    private readonly Queue<double> _cpuHistory = new();
    private readonly Queue<double> _memoryHistory = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public StorageExplorerViewModel StorageExplorer { get; }
    public CleanupViewModel Cleanup { get; }
    public PerformanceViewModel Performance { get; }
    public AnomalyViewModel Anomalies { get; }

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
    public ICommand ShowDashboardDetailCommand { get; }

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
                OnChanged(nameof(IsAnomaliesActive));
                OnChanged(nameof(IsLiveResourcesActive));
                OnChanged(nameof(IsCompatibilityActive));
            }
        }
    }

    public bool IsOverviewActive => ActiveView == "Overview";
    public bool IsStorageExplorerActive => ActiveView == "StorageExplorer";
    public bool IsCleanupActive => ActiveView == "Cleanup";
    public bool IsPerformanceActive => ActiveView == "Performance";
    public bool IsAnomaliesActive => ActiveView == "Anomalies";
    public bool IsLiveResourcesActive => ActiveView == "LiveResources";
    public bool IsCompatibilityActive => ActiveView == "Compatibility";

    public string Greeting => DateTime.Now.Hour < 12 ? "morning" : DateTime.Now.Hour < 18 ? "afternoon" : "evening";
    public string Prompt
    {
        get => _prompt;
        set
        {
            if (_prompt == value) return;
            _prompt = value;
            OnChanged();
            OnChanged(nameof(HasPrompt));
        }
    }

    public bool HasPrompt => !string.IsNullOrWhiteSpace(Prompt);

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
    public bool HasMessages => Messages.Count > 0;
    public bool HasDashboardDetail => !string.IsNullOrEmpty(_selectedDashboardCard);
    public string DashboardDetailTitle => $"{_selectedDashboardCard} details";
    public string DashboardDetail => _selectedDashboardCard switch
    {
        "Insights" => "These recommendations are based on the latest CPU, memory, and storage sample. Select Refresh to collect a new sample.",
        "CPU" => $"Current processor utilisation is {CpuHeadline}. {CpuDetail}. The chart shows the most recent samples, updated every two seconds.",
        "Memory" => $"{MemoryHeadline} is allocated ({MemoryDetail}). High memory pressure can cause slower app switching and disk paging.",
        "Network" => $"Current traffic is {NetworkDownText} received and {NetworkUpText} sent. Total traffic since monitoring began: {NetworkTotalInText} in and {NetworkTotalOutText} out.",
        "Disk" => $"Storage is {StoragePercent:F1}% used. {StorageDetail}. The drive list above breaks down usage by mounted filesystem.",
        "Thermal" => $"CPU temperature: {ThermalCpuText}; hottest sensor: {ThermalMaxText}. Sustained high temperatures can reduce performance.",
        "Battery" => $"Battery level is {BatteryText}. Status: {BatteryStatusText}. Reported health: {BatteryHealthText}.",
        "GPU" => $"{GpuNameText}. {GpuMemoryText}. Current GPU state: {GpuText}.",
        _ => "Select a dashboard card to see more detail."
    };

    public string MemoryHeadline => $"{_snapshot?.MemoryUsedGb:F1} / {_snapshot?.MemoryTotalGb:F1} GB";
    public string MemoryDetail => $"{MemoryPercent:F0}% in use";
    public double MemoryPercent => _snapshot?.MemoryPercent ?? 0;
    public string StorageHeadline => $"{_snapshot?.DiskUsedGb:F0} / {_snapshot?.DiskTotalGb:F0} GB";
    public string StorageDetail => $"{(_snapshot != null ? _snapshot.DiskTotalGb - _snapshot.DiskUsedGb : 0):F0} GB free";
    public double StoragePercent => _snapshot?.DiskPercent ?? 0;
    public string CpuHeadline => $"{_snapshot?.CpuPercent:F0}%";
    public string CpuDetail => $"Across {Environment.ProcessorCount} logical cores";
    public double CpuPercent => _snapshot?.CpuPercent ?? 0;
    public IReadOnlyList<double> CpuHistory => [.. _cpuHistory];
    public IReadOnlyList<double> MemoryHistory => [.. _memoryHistory];
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
    private bool HasBattery => _snapshot?.Hardware?.BatteryPercent is not null;
    public string BatteryText => _snapshot?.Hardware?.BatteryPercent is { } percent ? $"{percent:F0}%" : "—";
    public string BatteryStatusText => _snapshot?.Hardware?.BatteryStatus ?? "No battery";
    public string BatteryHealthText => _snapshot?.Hardware?.BatteryHealthPercent is { } health ? $"{health:F0}%" : HasBattery ? "Unknown" : "—";

    private IReadOnlyList<TemperatureReading> TemperatureReadings => _snapshot?.Hardware?.Temperatures ?? [];
    public IReadOnlyList<TemperatureReading> TemperatureSensors =>
        TemperatureReadings.OrderBy(reading => reading.Kind).ThenByDescending(reading => reading.Celsius).ToList();
    public bool HasTemperatures => TemperatureReadings.Count > 0;

    /// <summary>The ring shows the CPU when it has a sensor, otherwise the best other component.</summary>
    private (string Kind, double Celsius)? PrimaryTemperature
    {
        get
        {
            foreach (var kind in new[] { ThermalSensors.Cpu, ThermalSensors.System, ThermalSensors.Gpu, ThermalSensors.Storage })
                if (ThermalSensors.ForKind(TemperatureReadings, kind) is { } celsius) return (kind, celsius);
            return null;
        }
    }

    public string ThermalText => PrimaryTemperature is { } primary ? $"{primary.Celsius:F0}°C" : "—";
    public string ThermalCaption => PrimaryTemperature?.Kind ?? "No sensor";
    public string ThermalStatusText => PrimaryTemperature is { } primary
        ? primary.Celsius >= 85 ? "Running hot" : primary.Celsius >= 70 ? "Warm" : "Normal"
        : "No sensors reported";
    public string ThermalSensorsText
    {
        get
        {
            if (PrimaryTemperature is not { } primary) return ThermalHint;
            var others = new[] { ThermalSensors.Cpu, ThermalSensors.Gpu, ThermalSensors.Storage }
                .Where(kind => kind != primary.Kind)
                .Select(kind => (Kind: kind, Celsius: ThermalSensors.ForKind(TemperatureReadings, kind)))
                .Where(other => other.Celsius.HasValue)
                .Select(other => $"{other.Kind} {other.Celsius:F0}°C")
                .ToList();
            return others.Count > 0 ? string.Join(" · ", others) : $"Hottest sensor {ThermalMaxText}";
        }
    }
    public string ThermalHint => OperatingSystem.IsLinux() ? "Run sensors-detect (lm_sensors) to load a sensor driver"
        : OperatingSystem.IsWindows() ? "Windows exposes no thermal zone on this device"
        : "This OS does not expose temperature sensors";
    public string ThermalCpuText => FormatTemperature(ThermalSensors.CpuTemperature(TemperatureReadings));
    public string ThermalGpuText => FormatTemperature(ThermalSensors.ForKind(TemperatureReadings, ThermalSensors.Gpu));
    public string ThermalStorageText => FormatTemperature(ThermalSensors.ForKind(TemperatureReadings, ThermalSensors.Storage));
    public string ThermalMaxText => FormatTemperature(_snapshot?.Hardware?.MaxTemperatureC);

    public string NetworkDownText => FormatRate(_snapshot?.Hardware?.NetworkReceiveBytesPerSecond ?? 0);
    public string NetworkUpText => FormatRate(_snapshot?.Hardware?.NetworkSendBytesPerSecond ?? 0);
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
        Anomalies = new AnomalyViewModel(ExplainAnomalyAsync);
        Messages.CollectionChanged += (_, _) => OnChanged(nameof(HasMessages));
        // Wire sub-view model events to AI and notifications
        StorageExplorer.RequestAiQuestion += q => _ = AskAsync(q);
        StorageExplorer.NotifyUser += msg => ShowBanner(msg);

        Cleanup.RequestAiQuestion += q => _ = AskAsync(q);
        Cleanup.NotifyUser += msg => ShowBanner(msg);

        Performance.NotifyUser += msg => ShowBanner(msg);

        RefreshCommand = new RelayCommand(Refresh);
        CycleResourceMetricCommand = new RelayCommand(CycleResourceMetric);
        ShowDashboardDetailCommand = new RelayCommand(card =>
        {
            _selectedDashboardCard = card?.ToString() ?? "";
            OnChanged(nameof(HasDashboardDetail));
            OnChanged(nameof(DashboardDetailTitle));
            OnChanged(nameof(DashboardDetail));
        });
        SendCommand = new AsyncCommand(_ => SendAsync());
        QuickAskCommand = new AsyncCommand(p => AskAsync(p?.ToString() ?? ""));
        SetViewCommand = new RelayCommand(p => { if (p is string view) ActiveView = view; });
        ClearChatCommand = new RelayCommand(() => Messages.Clear());
        Refresh();
        _resourceTimer.Tick += (_, _) =>
        {
            Refresh();
            if (IsPerformanceActive) Performance.Refresh();
        };
        _resourceTimer.Start();

        // Automatically start background scan of Root directory (/) on startup
        _ = StorageExplorer.StartScanAsync();

        // Messages.Add(new ChatMessage(
        //     "DiagnoX",
        //     "👋 Welcome! I'm your AI System & Storage Advisor.\n\n" +
        //     "• **Storage Explorer**: Scanning your entire root filesystem (`/`) to show every file and folder in a full WinDirStat-style treemap.\n" +
        //     "• **Cleanup Assistant**: Detects caches, junk files, and large dev artifacts.\n" +
        //     "• **Performance**: Live process manager.\n\n" +
        //     "I am currently scanning the entire root filesystem (`/`) in the background so you can immediately inspect all disk space hogs across the whole system!",
        //     "#132C3B",
        //     HorizontalAlignment.Left));
    }

    public void ShowBanner(string msg)
    {
        StatusBanner = msg;
        Task.Delay(5000).ContinueWith(_ =>
        {
            if (StatusBanner == msg) StatusBanner = "";
        });
    }

    public void StopMonitoring()
    {
        _resourceTimer.Stop();
        Anomalies.Dispose();
    }

    private void Refresh()
    {
        _snapshot = _monitor.Capture();
        PushHistory(_cpuHistory, _snapshot.CpuPercent);
        PushHistory(_memoryHistory, _snapshot.MemoryPercent);
        Recommendations.Clear();

        AddRecommendation(
            _snapshot.MemoryPercent > 80,
            _snapshot.MemoryPercent > 80 ? "Memory pressure" : "Memory healthy",
            _snapshot.MemoryPercent > 80 ? "Close heavy background applications to prevent paging." : "Memory usage is within safe limits.",
            "RAM");

        AddRecommendation(
            _snapshot.DiskPercent > 80,
            _snapshot.DiskPercent > 80 ? "Storage running tight" : "Storage healthy",
            _snapshot.DiskPercent > 80 ? "Review Downloads, caches, and large media files." : "Adequate storage headroom available.",
            "Disk");

        AddRecommendation(
            _snapshot.CpuPercent > 75,
            _snapshot.CpuPercent > 75 ? "CPU activity elevated" : "CPU load normal",
            _snapshot.CpuPercent > 75 ? "Active compute tasks running across cores." : "CPU headroom is comfortably available.",
            "CPU");

        TopProcesses.Clear();
        foreach (var p in _snapshot.Processes)
            TopProcesses.Add(new ProcessRow(p.Name, $"{p.MemoryMb:F0} MB"));

        DiskItems.Clear();
        foreach (var d in _snapshot.Disks)
            DiskItems.Add(new DiskRow(d.Name, DriveItem.FormatUsage(d.UsedGb, d.TotalGb), d.TotalGb == 0 ? 0 : d.UsedGb / d.TotalGb * 100));

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
        OnChanged(nameof(ThermalCaption));
        OnChanged(nameof(ThermalStatusText));
        OnChanged(nameof(ThermalSensorsText));
        OnChanged(nameof(ThermalCpuText));
        OnChanged(nameof(ThermalGpuText));
        OnChanged(nameof(ThermalStorageText));
        OnChanged(nameof(ThermalMaxText));
        OnChanged(nameof(TemperatureSensors));
        OnChanged(nameof(HasTemperatures));
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
            nameof(CpuHeadline), nameof(CpuDetail), nameof(CpuPercent),
            nameof(CpuHistory), nameof(MemoryHistory)
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
        Recommendations.Add(new Recommendation(title, detail, impact, urgent ? "#FF9B70" : "#45D6A5", urgent));

    private static void PushHistory(Queue<double> history, double value)
    {
        history.Enqueue(value);
        while (history.Count > HistoryLength) history.Dequeue();
    }

    private static string FormatTemperature(double? celsius) => celsius is { } value ? $"{value:F0}°C" : "Not reported";

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

        Messages.Add(new ChatMessage("You", question, "#153441", HorizontalAlignment.Right));
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

            var aiMessage = new ChatMessage("DiagnoX", answer, "#101B22", HorizontalAlignment.Left, animate: true);
            Messages.Add(aiMessage);
            _ = aiMessage.AnimateAsync();
        }
        catch (Exception ex)
        {
            Messages.Add(new ChatMessage("DiagnoX", $"Error: {ex.Message}", "#381B1B", HorizontalAlignment.Left));
        }
        finally
        {
            IsAiThinking = false;
        }
    }

    private Task<string> ExplainAnomalyAsync(AnomalyRecord record) => _advisor.AskAsync(
        $"Explain this Windows system event in plain language. State what it means, the likely causes supported by the event, and one or two safe next checks. Do not claim certainty.\n\n" +
        $"Category: {record.CategoryLabel}\nTitle: {record.Title}\nSource: {record.Source}\nTime: {record.TimestampUtc.ToLocalTime():g}\nDetails: {record.Details}\nRaw event: {record.RawMessage}",
        _snapshot,
        StorageExplorer.RootNode,
        StorageExplorer.Extensions.ToList(),
        StorageExplorer.IsScanning,
        StorageExplorer.ScanPath,
        StorageExplorer.TotalFilesScanned,
        StorageExplorer.TotalBytesScanned);

    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public record Recommendation(string Title, string Detail, string Impact, string Accent, bool IsUrgent);
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
        if (string.IsNullOrEmpty(Text) || Author != "DiagnoX")
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
