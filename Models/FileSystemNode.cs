using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SystemMind.Models;

public sealed class FileSystemNode : INotifyPropertyChanged
{
    private bool _isExpanded;
    private bool _isSelected;

    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public bool IsDirectory { get; set; }
    public long Size { get; set; }
    public int FileCount { get; set; }
    public int DirectoryCount { get; set; }
    public DateTime LastModified { get; set; }
    public string Extension { get; set; } = "";
    public string Category { get; set; } = "Other";
    public string ColorHex { get; set; } = "#7B8B9E";
    public double PercentageOfRoot { get; set; }
    public double PercentageOfParent { get; set; }

    public FileSystemNode? Parent { get; set; }
    public ObservableCollection<FileSystemNode> Children { get; } = [];

    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (_isExpanded != value) { _isExpanded = value; OnPropertyChanged(); } }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(); } }
    }

    public string FormattedSize => FormatBytes(Size);
    public string FormattedPercent => $"{PercentageOfRoot:F1}%";
    public string SubtreeSummary => IsDirectory 
        ? $"{FileCount:N0} files, {DirectoryCount:N0} folders" 
        : LastModified.ToString("yyyy-MM-dd HH:mm");

    public string IconText => IsDirectory ? "📁" : GetIconForCategory(Category);

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        int digitGroups = (int)(Math.Log10(Math.Max(1, bytes)) / Math.Log10(1024));
        if (digitGroups >= units.Length) digitGroups = units.Length - 1;
        double num = bytes / Math.Pow(1024, digitGroups);
        return $"{num:0.##} {units[digitGroups]}";
    }

    private static string GetIconForCategory(string category) => category switch
    {
        "Video" => "🎬",
        "Audio" => "🎵",
        "Image" => "🖼️",
        "Archive" => "📦",
        "Code" => "💻",
        "Document" => "📄",
        "Binary" => "⚙️",
        "Cache" => "🧹",
        _ => "📎"
    };

    public static (string Category, string ColorHex) Categorize(string ext)
    {
        var e = ext.ToLowerInvariant();
        return e switch
        {
            ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" or ".flv" or ".webm" or ".m4v"
                => ("Video", "#9B51E0"),
            ".mp3" or ".wav" or ".flac" or ".aac" or ".ogg" or ".m4a" or ".wma"
                => ("Audio", "#27AE60"),
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".svg" or ".ico" or ".tiff"
                => ("Image", "#2D9CDB"),
            ".zip" or ".tar" or ".gz" or ".bz2" or ".xz" or ".7z" or ".rar" or ".tgz" or ".iso" or ".dmg"
                => ("Archive", "#F2994A"),
            ".cs" or ".js" or ".ts" or ".py" or ".java" or ".cpp" or ".c" or ".h" or ".rs" or ".go"
            or ".html" or ".css" or ".scss" or ".json" or ".xml" or ".yaml" or ".yml" or ".sql"
                => ("Code", "#00B4D8"),
            ".pdf" or ".doc" or ".docx" or ".xls" or ".xlsx" or ".ppt" or ".pptx" or ".txt" or ".md" or ".rtf"
                => ("Document", "#56CCF2"),
            ".exe" or ".dll" or ".so" or ".dylib" or ".bin" or ".appimage" or ".deb" or ".rpm"
                => ("Binary", "#EB5757"),
            ".cache" or ".tmp" or ".temp" or ".log" or ".bak" or ".old" or ".swp"
                => ("Cache", "#828282"),
            _ => ("Other", "#6C7A89")
        };
    }
}
