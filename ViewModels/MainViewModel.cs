using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia.Layout;
using Avalonia.Media;
using SystemMind.Models;
using SystemMind.Services;

namespace SystemMind.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly SystemMonitor _monitor = new();
    private readonly AdvisorService _advisor = new();
    private SystemSnapshot _snapshot = null!;
    private string _prompt = "";
    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<Recommendation> Recommendations { get; } = [];
    public ObservableCollection<ProcessRow> TopProcesses { get; } = [];
    public ObservableCollection<DiskRow> DiskItems { get; } = [];
    public ObservableCollection<ChatMessage> Messages { get; } = [];
    public ICommand RefreshCommand { get; }
    public ICommand SendCommand { get; }
    public ICommand QuickAskCommand { get; }
    public string Greeting => DateTime.Now.Hour < 12 ? "morning" : DateTime.Now.Hour < 18 ? "afternoon" : "evening";
    public string Prompt { get => _prompt; set { _prompt = value; OnChanged(); } }
    public string MemoryHeadline => $"{_snapshot.MemoryUsedGb:F1} / {_snapshot.MemoryTotalGb:F1} GB";
    public string MemoryDetail => $"{MemoryPercent:F0}% in use";
    public double MemoryPercent => _snapshot.MemoryPercent;
    public string StorageHeadline => $"{_snapshot.DiskUsedGb:F0} / {_snapshot.DiskTotalGb:F0} GB";
    public string StorageDetail => $"{_snapshot.DiskTotalGb - _snapshot.DiskUsedGb:F0} GB free";
    public double StoragePercent => _snapshot.DiskPercent;
    public string CpuHeadline => $"{_snapshot.CpuPercent:F0}%";
    public string CpuDetail => $"Across {Environment.ProcessorCount} logical cores";
    public double CpuPercent => _snapshot.CpuPercent;
    public MainViewModel()
    {
        RefreshCommand = new RelayCommand(Refresh);
        SendCommand = new AsyncCommand(_ => SendAsync());
        QuickAskCommand = new AsyncCommand(p => AskAsync(p?.ToString() ?? ""));
        Refresh();
        Messages.Add(new ChatMessage("SystemMind", "I’m watching your live resource snapshot. Ask why RAM is high, what to clean, or whether a game looks feasible.", "#132C3B", HorizontalAlignment.Left));
    }
    private void Refresh()
    {
        _snapshot = _monitor.Capture();
        Recommendations.Clear();
        AddRecommendation(_snapshot.MemoryPercent > 80, "Memory pressure", _snapshot.MemoryPercent > 80 ? "Close the largest unused apps to reduce paging and stutter." : "Memory use looks healthy right now.", "RAM");
        AddRecommendation(_snapshot.DiskPercent > 80, "Storage is getting tight", _snapshot.DiskPercent > 80 ? "Review Downloads, old installers, and unused games before deleting anything." : "Keep an eye on Downloads and large unused applications.", "Disk");
        AddRecommendation(_snapshot.CpuPercent > 75, "CPU activity is elevated", _snapshot.CpuPercent > 75 ? "Wait for the workload to finish or identify active background tasks." : "CPU headroom is currently available.", "CPU");
        TopProcesses.Clear(); foreach (var p in _snapshot.Processes) TopProcesses.Add(new ProcessRow(p.Name, $"{p.MemoryMb:F0} MB"));
        DiskItems.Clear(); foreach (var d in _snapshot.Disks.Take(4)) DiskItems.Add(new DiskRow(d.Name, $"{d.UsedGb:F0} / {d.TotalGb:F0} GB", d.TotalGb == 0 ? 0 : d.UsedGb / d.TotalGb * 100));
        foreach (var name in new[] { nameof(MemoryHeadline), nameof(MemoryDetail), nameof(MemoryPercent), nameof(StorageHeadline), nameof(StorageDetail), nameof(StoragePercent), nameof(CpuHeadline), nameof(CpuDetail), nameof(CpuPercent) }) OnChanged(name);
    }
    private void AddRecommendation(bool urgent, string title, string detail, string impact) => Recommendations.Add(new Recommendation(title, detail, impact, urgent ? "#FF9B70" : "#45D6A5"));
    private async Task SendAsync() { var q = Prompt.Trim(); if (q.Length == 0) return; Prompt = ""; await AskAsync(q); }
    private async Task AskAsync(string question) { Messages.Add(new ChatMessage("You", question, "#183E50", HorizontalAlignment.Right)); Messages.Add(new ChatMessage("SystemMind", await _advisor.AskAsync(question, _snapshot), "#132C3B", HorizontalAlignment.Left)); }
    private void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
public record Recommendation(string Title, string Detail, string Impact, string Accent);
public record ProcessRow(string Name, string Memory);
public record DiskRow(string Name, string Value, double Percent);
public record ChatMessage(string Author, string Text, string Background, HorizontalAlignment Alignment);
public sealed class RelayCommand(Action action) : ICommand { public event EventHandler? CanExecuteChanged; public bool CanExecute(object? p) => true; public void Execute(object? p) => action(); }
public sealed class AsyncCommand(Func<object?, Task> action) : ICommand { public event EventHandler? CanExecuteChanged; public bool CanExecute(object? p) => true; public async void Execute(object? p) => await action(p); }
