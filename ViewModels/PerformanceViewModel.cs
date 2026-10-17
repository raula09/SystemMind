using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using SystemMind.Models;
using SystemMind.Services;

namespace SystemMind.ViewModels;

public sealed class PerformanceViewModel : INotifyPropertyChanged
{
    private readonly SystemMonitor _monitor = new();
    private string _searchFilter = "";
    private ProcessDetailedInfo? _selectedProcess;
    private string _statusMessage = "Monitoring system processes";
    private double _memoryUsedGb;
    private double _memoryTotalGb;
    private double _cpuPercent;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<string>? NotifyUser;

    public ObservableCollection<ProcessDetailedInfo> AllProcesses { get; } = [];
    public ObservableCollection<ProcessDetailedInfo> FilteredProcesses { get; } = [];

    public string SearchFilter
    {
        get => _searchFilter;
        set
        {
            if (_searchFilter != value)
            {
                _searchFilter = value;
                OnChanged();
                ApplyFilter();
            }
        }
    }

    public ProcessDetailedInfo? SelectedProcess
    {
        get => _selectedProcess;
        set { if (_selectedProcess != value) { _selectedProcess = value; OnChanged(); OnChanged(nameof(HasSelectedProcess)); } }
    }

    public bool HasSelectedProcess => SelectedProcess != null;

    public string StatusMessage
    {
        get => _statusMessage;
        set { if (_statusMessage != value) { _statusMessage = value; OnChanged(); } }
    }

    public double CpuPercent
    {
        get => _cpuPercent;
        private set { _cpuPercent = value; OnChanged(); OnChanged(nameof(CpuText)); }
    }

    public string CpuText => $"{CpuPercent:F0}%";

    public double MemoryPercent => _memoryTotalGb <= 0 ? 0 : (_memoryUsedGb / _memoryTotalGb) * 100.0;
    public string MemoryHeadline => $"{_memoryUsedGb:F1} / {_memoryTotalGb:F1} GB";
    public string MemoryDetail => $"{MemoryPercent:F0}% in use";

    public ICommand RefreshCommand { get; }
    public ICommand TerminateProcessCommand { get; }

    public PerformanceViewModel()
    {
        RefreshCommand = new RelayCommand(Refresh);
        TerminateProcessCommand = new RelayCommand(TerminateSelected);
        Refresh();
    }

    public void Refresh()
    {
        var snapshot = _monitor.Capture();
        _memoryUsedGb = snapshot.MemoryUsedGb;
        _memoryTotalGb = snapshot.MemoryTotalGb;
        CpuPercent = snapshot.CpuPercent;

        OnChanged(nameof(MemoryPercent));
        OnChanged(nameof(MemoryHeadline));
        OnChanged(nameof(MemoryDetail));

        AllProcesses.Clear();
        if (snapshot.AllProcesses != null)
        {
            foreach (var p in snapshot.AllProcesses)
            {
                AllProcesses.Add(p);
            }
        }

        ApplyFilter();
        StatusMessage = $"Tracking {AllProcesses.Count} active processes. Cores: {Environment.ProcessorCount}";
    }

    private void TerminateSelected()
    {
        if (SelectedProcess == null) return;
        var name = SelectedProcess.Name;
        var pid = SelectedProcess.Id;

        var success = _monitor.TerminateProcess(pid);
        if (success)
        {
            StatusMessage = $"Terminated process '{name}' (PID: {pid})";
            NotifyUser?.Invoke($"Terminated {name} (PID: {pid})");
            Refresh();
        }
        else
        {
            StatusMessage = $"Failed to terminate '{name}'. It may require elevated permissions.";
            NotifyUser?.Invoke($"Permission denied terminating {name}");
        }
    }

    private void ApplyFilter()
    {
        FilteredProcesses.Clear();
        var q = SearchFilter.Trim().ToLowerInvariant();
        var matches = string.IsNullOrEmpty(q)
            ? AllProcesses
            : AllProcesses.Where(p => p.Name.ToLowerInvariant().Contains(q) || p.Id.ToString().Contains(q));

        foreach (var p in matches.Take(200)) // Cap list to keep UI fast
        {
            FilteredProcesses.Add(p);
        }
    }

    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
