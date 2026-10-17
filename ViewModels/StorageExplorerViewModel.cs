using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using SystemMind.Models;
using SystemMind.Services;

namespace SystemMind.ViewModels;

public sealed class StorageExplorerViewModel : INotifyPropertyChanged
{
    private readonly DirectoryScanner _scanner = new();
    private readonly TreemapBuilder _treemapBuilder = new();
    private readonly FileOperationsService _fileOps = new();

    private string _scanPath;
    private bool _isScanning;
    private string _statusMessage = "Ready to scan";
    private string _currentScanningPath = "";
    private double _elapsedSeconds;
    private long _totalFilesScanned;
    private long _totalBytesScanned;
    private FileSystemNode? _rootNode;
    private FileSystemNode? _selectedNode;
    private TreemapItem? _selectedTreemapItem;
    private TreemapItem? _hoveredTreemapItem;
    private IReadOnlyList<TreemapItem>? _treemapItems;
    private double _lastWidth = 800;
    private double _lastHeight = 400;

    private CancellationTokenSource? _scanCts;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<string>? RequestAiQuestion;
    public event Action<string>? NotifyUser;

    public ObservableCollection<FileSystemNode> RootNodes { get; } = [];
    public ObservableCollection<ExtensionStat> Extensions { get; } = [];
    public ObservableCollection<DriveItem> AvailableDrives { get; } = [];

    public string ScanPath
    {
        get => _scanPath;
        set { if (_scanPath != value) { _scanPath = value; OnChanged(); } }
    }

    public bool IsScanning
    {
        get => _isScanning;
        set { if (_isScanning != value) { _isScanning = value; OnChanged(); OnChanged(nameof(CanScan)); } }
    }

    public bool CanScan => !IsScanning;

    public string StatusMessage
    {
        get => _statusMessage;
        set { if (_statusMessage != value) { _statusMessage = value; OnChanged(); } }
    }

    public string CurrentScanningPath
    {
        get => _currentScanningPath;
        set { if (_currentScanningPath != value) { _currentScanningPath = value; OnChanged(); } }
    }

    public string ElapsedText => $"{_elapsedSeconds:F1}s";
    public string ScannedFilesText => $"{_totalFilesScanned:N0} files";
    public string ScannedSizeText => FileSystemNode.FormatBytes(_totalBytesScanned);
    public long TotalFilesScanned => _totalFilesScanned;
    public long TotalBytesScanned => _totalBytesScanned;

    public FileSystemNode? RootNode
    {
        get => _rootNode;
        private set { _rootNode = value; OnChanged(); OnChanged(nameof(HasResults)); }
    }

    public bool HasResults => RootNode != null && RootNode.Size > 0;

    public FileSystemNode? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (_selectedNode != value)
            {
                if (_selectedNode != null) _selectedNode.IsSelected = false;
                _selectedNode = value;
                if (_selectedNode != null) _selectedNode.IsSelected = true;
                OnChanged();
                OnChanged(nameof(SelectedDetails));
                OnChanged(nameof(HasSelection));
            }
        }
    }

    public bool HasSelection => SelectedNode != null;

    public string SelectedDetails => SelectedNode == null
        ? "No file or folder selected"
        : $"{SelectedNode.IconText} {SelectedNode.Name} — {SelectedNode.FormattedSize} ({(SelectedNode.IsDirectory ? $"{SelectedNode.FileCount:N0} files" : SelectedNode.Extension)}) | {SelectedNode.FullPath}";

    public TreemapItem? SelectedTreemapItem
    {
        get => _selectedTreemapItem;
        set
        {
            if (_selectedTreemapItem != value)
            {
                _selectedTreemapItem = value;
                OnChanged();
                if (value?.Node != null)
                {
                    SelectedNode = value.Node;
                }
            }
        }
    }

    public TreemapItem? HoveredTreemapItem
    {
        get => _hoveredTreemapItem;
        set
        {
            if (_hoveredTreemapItem != value)
            {
                _hoveredTreemapItem = value;
                OnChanged();
                OnChanged(nameof(HoveredInfoText));
            }
        }
    }

    public string HoveredInfoText => HoveredTreemapItem == null
        ? (SelectedNode != null ? SelectedDetails : "Hover over blocks to inspect, click to select, double-click to reveal")
        : $"🔎 {HoveredTreemapItem.Node.Name} ({HoveredTreemapItem.FormattedSize}) — {HoveredTreemapItem.Node.FullPath}";

    public IReadOnlyList<TreemapItem>? TreemapItems
    {
        get => _treemapItems;
        private set { _treemapItems = value; OnChanged(); }
    }

    public ICommand ScanCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand SetQuickPathCommand { get; }
    public ICommand BrowseFolderCommand { get; }
    public ICommand OpenFileCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand OpenTerminalCommand { get; }
    public ICommand MoveToTrashCommand { get; }
    public ICommand PermanentDeleteCommand { get; }
    public ICommand CopyPathCommand { get; }
    public ICommand AskAiAboutSelectedCommand { get; }
    public ICommand SelectDriveCommand { get; }
    public ICommand RefreshDrivesCommand { get; }

    public StorageExplorerViewModel()
    {
        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _scanPath = "/"; // Show every single file from root by default

        ScanCommand = new AsyncCommand(_ => StartScanAsync());
        CancelCommand = new RelayCommand(CancelScan);
        SelectDriveCommand = new RelayCommand(p =>
        {
            if (p is DriveItem drive)
            {
                ScanPath = drive.MountPoint;
                _ = StartScanAsync();
            }
        });
        RefreshDrivesCommand = new RelayCommand(RefreshDrives);
        SetQuickPathCommand = new RelayCommand(p =>
        {
            if (p is string path)
            {
                ScanPath = path.StartsWith("~")
                    ? Path.Combine(userHome, path.TrimStart('~', '/', '\\'))
                    : path;
                _ = StartScanAsync();
            }
        });
        BrowseFolderCommand = new AsyncCommand(_ => BrowseFolderAsync());
        OpenFileCommand = new RelayCommand(_ => { if (SelectedNode != null) _fileOps.OpenFile(SelectedNode.FullPath); });
        OpenFolderCommand = new RelayCommand(_ => { if (SelectedNode != null) _fileOps.OpenFolder(SelectedNode.FullPath); });
        OpenTerminalCommand = new RelayCommand(_ => { if (SelectedNode != null) _fileOps.OpenTerminal(SelectedNode.FullPath); });
        MoveToTrashCommand = new RelayCommand(_ => TrashSelected());
        PermanentDeleteCommand = new RelayCommand(_ => DeleteSelectedPermanent());
        CopyPathCommand = new AsyncCommand(_ => CopyPathAsync());
        AskAiAboutSelectedCommand = new RelayCommand(_ =>
        {
            if (SelectedNode != null)
            {
                RequestAiQuestion?.Invoke($"What is '{SelectedNode.FullPath}' ({SelectedNode.FormattedSize}), and is it safe to clean or remove?");
            }
        });

        RefreshDrives();
    }

    public void RefreshDrives()
    {
        AvailableDrives.Clear();
        foreach (var d in SystemMonitor.GetPhysicalDrives())
        {
            AvailableDrives.Add(d);
        }
    }

    public async Task StartScanAsync()
    {
        if (IsScanning) return;
        var path = ScanPath?.Trim();
        if (string.IsNullOrEmpty(path) || (!Directory.Exists(path) && !File.Exists(path)))
        {
            StatusMessage = "Directory not found. Please choose a valid directory.";
            return;
        }

        _scanCts = new CancellationTokenSource();
        IsScanning = true;
        StatusMessage = "Scanning storage hierarchy...";
        CurrentScanningPath = path;

        var progress = new Progress<ScanProgress>(p =>
        {
            _totalFilesScanned = p.ScannedFiles;
            _totalBytesScanned = p.TotalBytes;
            _elapsedSeconds = p.ElapsedSeconds;
            CurrentScanningPath = p.CurrentPath;
            StatusMessage = $"Scanning: {_totalFilesScanned:N0} files ({FileSystemNode.FormatBytes(_totalBytesScanned)})";
            OnChanged(nameof(ElapsedText));
            OnChanged(nameof(ScannedFilesText));
            OnChanged(nameof(ScannedSizeText));
        });

        try
        {
            var (root, extStats) = await _scanner.ScanAsync(path, progress, _scanCts.Token);
            RootNode = root;
            RootNodes.Clear();
            RootNodes.Add(root);

            Extensions.Clear();
            foreach (var ext in extStats) Extensions.Add(ext);

            StatusMessage = $"Scan completed in {ElapsedText}: {RootNode.FileCount:N0} files, {RootNode.FormattedSize}";
            RebuildTreemap(_lastWidth, _lastHeight);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Scan cancelled by user.";
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

    public void CancelScan()
    {
        _scanCts?.Cancel();
    }

    public void UpdateTreemapSize(double width, double height)
    {
        if (width <= 0 || height <= 0) return;
        _lastWidth = width;
        _lastHeight = height;
        if (RootNode != null)
        {
            RebuildTreemap(width, height);
        }
    }

    public void RebuildTreemap(double width, double height)
    {
        if (RootNode == null || width <= 10 || height <= 10) return;
        TreemapItems = _treemapBuilder.Build(RootNode, width, height);
    }

    private async Task BrowseFolderAsync()
    {
        try
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
                desktop.MainWindow != null)
            {
                var storage = desktop.MainWindow.StorageProvider;
                var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = "Select Directory to Scan",
                    AllowMultiple = false
                });

                if (folders.Count > 0)
                {
                    var selected = folders[0].TryGetLocalPath();
                    if (!string.IsNullOrEmpty(selected))
                    {
                        ScanPath = selected;
                        await StartScanAsync();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Folder picker error: {ex.Message}";
        }
    }

    private void TrashSelected()
    {
        if (SelectedNode == null) return;
        var (ok, msg) = _fileOps.MoveToTrash(SelectedNode.FullPath);
        NotifyUser?.Invoke(msg);
        if (ok && SelectedNode.Parent != null)
        {
            SelectedNode.Parent.Children.Remove(SelectedNode);
            RebuildTreemap(_lastWidth, _lastHeight);
            SelectedNode = null;
        }
    }

    private void DeleteSelectedPermanent()
    {
        if (SelectedNode == null) return;
        var (ok, msg) = _fileOps.PermanentDelete(SelectedNode.FullPath);
        NotifyUser?.Invoke(msg);
        if (ok && SelectedNode.Parent != null)
        {
            SelectedNode.Parent.Children.Remove(SelectedNode);
            RebuildTreemap(_lastWidth, _lastHeight);
            SelectedNode = null;
        }
    }

    private async Task CopyPathAsync()
    {
        if (SelectedNode == null) return;
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
            desktop.MainWindow?.Clipboard != null)
        {
            await desktop.MainWindow.Clipboard.SetTextAsync(SelectedNode.FullPath);
            NotifyUser?.Invoke("Copied path to clipboard: " + SelectedNode.FullPath);
        }
    }

    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
