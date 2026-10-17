using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using SystemMind.Models;
using SystemMind.Services;

namespace SystemMind.ViewModels;

public sealed class AnomalyViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly AnomalyDatabase _database = new();
    private readonly AnomalyScanner _scanner;
    private readonly Func<AnomalyRecord, Task<string>> _explain;
    private readonly (string Label, TimeSpan Window)[] _ranges = [("24 hours", TimeSpan.FromHours(24)), ("7 days", TimeSpan.FromDays(7)), ("30 days", TimeSpan.FromDays(30)), ("All history", TimeSpan.FromDays(36500))];
    private List<AnomalyRecord> _all = [];
    private int _rangeIndex = 2;
    private string _searchText = "";
    private string _selectedCategory = "All categories";
    private string _statusMessage = "Saved event history loaded.";
    private bool _isScanning;

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<AnomalyRecord> Events { get; } = [];
    public ObservableCollection<string> CategoryFilters { get; } = ["All categories"];
    public ObservableCollection<AnomalyCategorySummary> Categories { get; } = [];
    public ICommand ScanCommand { get; }
    public ICommand CycleRangeCommand { get; }
    public ICommand ExplainCommand { get; }
    public int TotalCount => _all.Count;
    public int CriticalCount => _all.Count(item => item.Severity == AnomalySeverity.Critical);
    public int WarningCount => _all.Count(item => item.Severity == AnomalySeverity.Warning);
    public string RangeLabel => _ranges[_rangeIndex].Label;
    public string StatusMessage { get => _statusMessage; private set { _statusMessage = value; OnChanged(); } }
    public bool IsScanning { get => _isScanning; private set { _isScanning = value; OnChanged(); } }
    public bool HasEvents => Events.Count > 0;
    public string SearchText { get => _searchText; set { _searchText = value; OnChanged(); ApplyFilters(); } }
    public string SelectedCategory { get => _selectedCategory; set { _selectedCategory = value ?? "All categories"; OnChanged(); ApplyFilters(); } }

    public AnomalyViewModel(Func<AnomalyRecord, Task<string>> explain)
    {
        _explain = explain;
        _scanner = new AnomalyScanner(_database);
        ScanCommand = new AsyncCommand(_ => ScanAsync());
        CycleRangeCommand = new RelayCommand(CycleRange);
        ExplainCommand = new AsyncCommand(ExplainAsync);
        Refresh();
    }

    private async Task ScanAsync()
    {
        if (IsScanning) return;
        IsScanning = true;
        try
        {
            var progress = new Progress<string>(text => StatusMessage = $"Scanning {text}…");
            var added = await _scanner.ScanAsync(_ranges[_rangeIndex].Window, progress);
            Refresh();
            StatusMessage = OperatingSystem.IsWindows() || OperatingSystem.IsLinux()
                ? $"Scan complete — {added} new event(s) saved."
                : "Event-log scanning is unavailable on this operating system.";
        }
        catch (Exception ex) { StatusMessage = $"Could not scan event history: {ex.Message}"; }
        finally { IsScanning = false; }
    }

    private async Task ExplainAsync(object? parameter)
    {
        if (parameter is not AnomalyRecord record || record.IsExplaining) return;
        record.IsExplaining = true;
        record.Explanation = "Explaining this event…";
        try { record.Explanation = await _explain(record); }
        catch (Exception ex) { record.Explanation = $"Could not explain this event: {ex.Message}"; }
        finally { record.IsExplaining = false; }
    }

    private void CycleRange() { _rangeIndex = (_rangeIndex + 1) % _ranges.Length; Refresh(); }
    private void Refresh()
    {
        _all = _database.GetRecent(_ranges[_rangeIndex].Window);
        CategoryFilters.Clear(); CategoryFilters.Add("All categories");
        foreach (var category in _all.Select(item => item.CategoryLabel).Distinct().Order()) CategoryFilters.Add(category);
        if (!CategoryFilters.Contains(SelectedCategory)) _selectedCategory = "All categories";
        Categories.Clear();
        foreach (var group in _all.GroupBy(item => item.CategoryLabel).OrderByDescending(group => group.Count()))
            Categories.Add(new AnomalyCategorySummary(group.Key, group.Count(), group.First().SeverityColor));
        ApplyFilters();
        OnChanged(nameof(TotalCount)); OnChanged(nameof(CriticalCount)); OnChanged(nameof(WarningCount)); OnChanged(nameof(RangeLabel));
    }
    private void ApplyFilters()
    {
        var query = SearchText.Trim();
        var matches = _all.Where(item => (SelectedCategory == "All categories" || item.CategoryLabel == SelectedCategory) &&
            (query.Length == 0 || item.Title.Contains(query, StringComparison.OrdinalIgnoreCase) || item.CategoryLabel.Contains(query, StringComparison.OrdinalIgnoreCase) || item.Source.Contains(query, StringComparison.OrdinalIgnoreCase))).ToList();
        Events.Clear(); foreach (var item in matches) Events.Add(item);
        OnChanged(nameof(HasEvents));
    }
    private void OnChanged([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    public void Dispose() => _database.Dispose();
}

public sealed record AnomalyCategorySummary(string Name, int Count, string Color);
