using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DiagnoX.Models;

public enum AnomalySeverity { Info, Warning, Critical }

/// <summary>A persisted Windows reliability or event-log issue.</summary>
public sealed class AnomalyRecord : INotifyPropertyChanged
{
    private bool _isExplaining;
    private string _explanation = "";

    public event PropertyChangedEventHandler? PropertyChanged;
    public long Id { get; set; }
    public DateTime TimestampUtc { get; set; }
    public AnomalySeverity Severity { get; set; }
    public string Category { get; set; } = "";
    public string Title { get; set; } = "";
    public string Source { get; set; } = "";
    public int EventId { get; set; }
    public string Details { get; set; } = "";
    public string RawMessage { get; set; } = "";
    public string Fingerprint { get; set; } = "";

    public string CategoryLabel => Category switch
    {
        "UnexpectedShutdown" => "Unexpected shutdowns",
        "BSOD" => "Blue screens",
        "Crash" => "Application crashes",
        "AppHang" => "Application hangs",
        "GpuTimeout" => "Graphics timeouts",
        "DiskError" => "Storage errors",
        "DriverError" => "Driver errors",
        "MemoryExhaustion" => "Memory pressure",
        "HardwareError" => "Hardware errors",
        _ => "Other events"
    };
    public string SeverityBadge => Severity.ToString().ToUpperInvariant();
    public string SeverityColor => Severity switch
    {
        AnomalySeverity.Critical => "#F0525F",
        AnomalySeverity.Warning => "#F5B335",
        _ => "#4D96F0"
    };
    public string RelativeTime => (DateTime.UtcNow - TimestampUtc) switch
    {
        var age when age.TotalMinutes < 1 => "just now",
        var age when age.TotalHours < 1 => $"{(int)age.TotalMinutes}m ago",
        var age when age.TotalDays < 1 => $"{(int)age.TotalHours}h ago",
        var age when age.TotalDays < 7 => $"{(int)age.TotalDays}d ago",
        _ => TimestampUtc.ToLocalTime().ToString("MMM dd")
    };
    public bool IsExplaining { get => _isExplaining; set { if (_isExplaining != value) { _isExplaining = value; OnChanged(); } } }
    public string Explanation
    {
        get => _explanation;
        set
        {
            if (_explanation == value) return;
            _explanation = value;
            OnChanged();
            OnChanged(nameof(HasExplanation));
        }
    }
    public bool HasExplanation => !string.IsNullOrWhiteSpace(Explanation);
    private void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
