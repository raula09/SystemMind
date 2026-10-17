using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia.Layout;
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

    public event PropertyChangedEventHandler? PropertyChanged;

    public StorageExplorerViewModel StorageExplorer { get; }
    public CleanupViewModel Cleanup { get; }
    public PerformanceViewModel Performance { get; }
    public SettingsViewModel Settings { get; }

    public ObservableCollection<Recommendation> Recommendations { get; } = [];
    public ObservableCollection<ProcessRow> TopProcesses { get; } = [];
    public ObservableCollection<DiskRow> DiskItems { get; } = [];
    public ObservableCollection<ChatMessage> Messages { get; } = [];

    public ICommand RefreshCommand { get; }
    public ICommand SendCommand { get; }
    public ICommand QuickAskCommand { get; }
    public ICommand SetViewCommand { get; }
    public ICommand ClearChatCommand { get; }

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
                OnChanged(nameof(IsCompatibilityActive));
            }
        }
    }

    public bool IsOverviewActive => ActiveView == "Overview";
    public bool IsStorageExplorerActive => ActiveView == "StorageExplorer";
    public bool IsCleanupActive => ActiveView == "Cleanup";
    public bool IsPerformanceActive => ActiveView == "Performance";
    public bool IsSettingsActive => ActiveView == "Settings";
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
        SendCommand = new AsyncCommand(_ => SendAsync());
        QuickAskCommand = new AsyncCommand(p => AskAsync(p?.ToString() ?? ""));
        SetViewCommand = new RelayCommand(p => { if (p is string view) ActiveView = view; });
        ClearChatCommand = new RelayCommand(() =>
        {
            Messages.Clear();
            Messages.Add(new ChatMessage("SystemMind", "Chat history cleared. How can I help you inspect or optimize your system?", "#132C3B", HorizontalAlignment.Left));
        });

        Refresh();

        // Automatically start background scan of Home directory on startup
        _ = StorageExplorer.StartScanAsync();

        Messages.Add(new ChatMessage(
            "SystemMind",
            "👋 Welcome! I'm your AI System & Storage Advisor.\n\n" +
            "• **Storage Explorer**: Scan any drive or folder to see a full WinDirStat-style treemap breakdown.\n" +
            "• **Cleanup Assistant**: Detects caches, junk files, and large dev artifacts.\n" +
            "• **Performance**: Live process manager.\n\n" +
            "I have already started scanning your Home directory in the background so you can immediately inspect your storage and treemap!",
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
        foreach (var d in _snapshot.Disks.Take(4))
            DiskItems.Add(new DiskRow(d.Name, $"{d.UsedGb:F0} / {d.TotalGb:F0} GB", d.TotalGb == 0 ? 0 : d.UsedGb / d.TotalGb * 100));

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

    private void AddRecommendation(bool urgent, string title, string detail, string impact) =>
        Recommendations.Add(new Recommendation(title, detail, impact, urgent ? "#FF9B70" : "#45D6A5"));

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

            Messages.Add(new ChatMessage("SystemMind", answer, "#132C3B", HorizontalAlignment.Left));
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
public record DiskRow(string Name, string Value, double Percent);
public record ChatMessage(string Author, string Text, string Background, HorizontalAlignment Alignment);

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
