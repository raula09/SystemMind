using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using DiagnoX.Models;
using DiagnoX.Services;

namespace DiagnoX.ViewModels;

public sealed class CleanupViewModel : INotifyPropertyChanged
{
    private readonly CleanupScannerService _cleaner = new();
    private readonly FileOperationsService _fileOps = new();

    private bool _isScanning;
    private bool _isCleaning;
    private string _statusMessage = "Scan to find reclaimable storage";
    private string _filterCategory = "All";

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<string>? RequestAiQuestion;
    public event Action<string>? NotifyUser;

    public ObservableCollection<CleanupItem> AllItems { get; } = [];
    public ObservableCollection<CleanupItem> FilteredItems { get; } = [];

    public bool IsScanning
    {
        get => _isScanning;
        set { if (_isScanning != value) { _isScanning = value; OnChanged(); OnChanged(nameof(CanOperate)); } }
    }

    public bool IsCleaning
    {
        get => _isCleaning;
        set { if (_isCleaning != value) { _isCleaning = value; OnChanged(); OnChanged(nameof(CanOperate)); } }
    }

    public bool CanOperate => !IsScanning && !IsCleaning;

    public string StatusMessage
    {
        get => _statusMessage;
        set { if (_statusMessage != value) { _statusMessage = value; OnChanged(); } }
    }

    public string FilterCategory
    {
        get => _filterCategory;
        set
        {
            if (_filterCategory != value)
            {
                _filterCategory = value;
                OnChanged();
                ApplyFilter();
            }
        }
    }

    public long SelectedBytes => AllItems.Where(i => i.IsSelected).Sum(i => i.SizeBytes);
    public long TotalBytes => AllItems.Sum(i => i.SizeBytes);

    public string SelectedReclaimableText => FileSystemNode.FormatBytes(SelectedBytes);
    public string TotalReclaimableText => FileSystemNode.FormatBytes(TotalBytes);

    public ICommand ScanCommand { get; }
    public ICommand CleanSelectedCommand { get; }
    public ICommand SelectAllCommand { get; }
    public ICommand DeselectAllCommand { get; }
    public ICommand OpenItemLocationCommand { get; }
    public ICommand AskAiAboutItemCommand { get; }

    public CleanupViewModel()
    {
        ScanCommand = new AsyncCommand(_ => ScanJunkAsync());
        CleanSelectedCommand = new AsyncCommand(_ => CleanSelectedAsync());
        SelectAllCommand = new RelayCommand(() => SetSelection(true));
        DeselectAllCommand = new RelayCommand(() => SetSelection(false));
        OpenItemLocationCommand = new RelayCommand(p =>
        {
            if (p is CleanupItem item) _fileOps.OpenFolder(item.Path);
        });
        AskAiAboutItemCommand = new RelayCommand(p =>
        {
            if (p is CleanupItem item)
            {
                RequestAiQuestion?.Invoke($"Is it safe to clean {item.Title} at '{item.Path}' ({item.FormattedSize})?");
            }
        });
    }

    public async Task ScanJunkAsync(FileSystemNode? scanRoot = null)
    {
        if (IsScanning || IsCleaning) return;
        IsScanning = true;
        StatusMessage = "Scanning for reclaimable junk, caches, and dev artifacts...";

        try
        {
            var results = await _cleaner.ScanForCleanupTargetsAsync(scanRoot);
            AllItems.Clear();
            foreach (var item in results)
            {
                item.PropertyChanged += (_, _) => UpdateTotals();
                AllItems.Add(item);
            }

            ApplyFilter();
            UpdateTotals();
            StatusMessage = $"Scan found {AllItems.Count} items — {TotalReclaimableText} potential space to reclaim.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Scan error: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
        }
    }

    public async Task CleanSelectedAsync()
    {
        if (IsCleaning || IsScanning) return;
        var selected = AllItems.Where(i => i.IsSelected).ToList();
        if (selected.Count == 0)
        {
            StatusMessage = "No items selected to clean.";
            return;
        }

        IsCleaning = true;
        StatusMessage = $"Cleaning {selected.Count} items...";

        var progress = new Progress<string>(msg => StatusMessage = msg);

        try
        {
            var (cleanedBytes, cleanedCount, errors) = await _cleaner.ExecuteCleanupAsync(selected, progress);

            // Remove successfully cleaned items
            foreach (var item in selected)
            {
                AllItems.Remove(item);
            }
            ApplyFilter();
            UpdateTotals();

            var cleanedStr = FileSystemNode.FormatBytes(cleanedBytes);
            if (errors.Count == 0)
            {
                StatusMessage = $"Clean complete! Reclaimed {cleanedStr} across {cleanedCount} items.";
                NotifyUser?.Invoke($"Successfully freed {cleanedStr} of disk space!");
            }
            else
            {
                StatusMessage = $"Cleaned {cleanedCount} items ({cleanedStr}), with {errors.Count} warnings.";
                NotifyUser?.Invoke($"Cleaned {cleanedStr} with some permissions warnings.");
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Cleanup failed: {ex.Message}";
        }
        finally
        {
            IsCleaning = false;
        }
    }

    private void SetSelection(bool selected)
    {
        foreach (var item in FilteredItems)
        {
            item.IsSelected = selected;
        }
        UpdateTotals();
    }

    private void UpdateTotals()
    {
        OnChanged(nameof(SelectedBytes));
        OnChanged(nameof(TotalBytes));
        OnChanged(nameof(SelectedReclaimableText));
        OnChanged(nameof(TotalReclaimableText));
    }

    private void ApplyFilter()
    {
        FilteredItems.Clear();
        var query = FilterCategory == "All"
            ? AllItems
            : AllItems.Where(i => string.Equals(i.Category, FilterCategory, StringComparison.OrdinalIgnoreCase));

        foreach (var item in query)
        {
            FilteredItems.Add(item);
        }
    }

    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
