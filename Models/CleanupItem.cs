using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DiagnoX.Models;

public sealed class CleanupItem : INotifyPropertyChanged
{
    private bool _isSelected = true;

    public string Title { get; set; } = "";
    public string Category { get; set; } = "Cache";
    public string Path { get; set; } = "";
    public long SizeBytes { get; set; }
    public string FormattedSize => FileSystemNode.FormatBytes(SizeBytes);
    public string RiskLevel { get; set; } = "Safe"; // Safe, Review, Warning
    public string Description { get; set; } = "";
    public string BadgeColor => RiskLevel switch
    {
        "Safe" => "#35D3A0",
        "Review" => "#F6B75D",
        "Warning" => "#FF7B7B",
        _ => "#7B8B9E"
    };

    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
