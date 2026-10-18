namespace DiagnoX.Models;

public sealed class DriveItem
{
    public string Device { get; set; } = "";
    public string MountPoint { get; set; } = "/";
    public string Label { get; set; } = "";
    public string FileSystem { get; set; } = "ext4";
    public double TotalGb { get; set; }
    public double UsedGb { get; set; }
    public double FreeGb => Math.Max(0, TotalGb - UsedGb);
    public double UsagePercent => TotalGb <= 0 ? 0 : Math.Clamp(UsedGb / TotalGb * 100.0, 0, 100);

    public bool IsRoot => MountPoint == "/" || MountPoint == "C:\\";

    public string DisplayName => !string.IsNullOrEmpty(Label)
        ? $"{Label} ({MountPoint})"
        : (IsRoot ? $"System Root ({MountPoint})" : MountPoint);

    public string Icon => Label.Contains("Game", StringComparison.OrdinalIgnoreCase)
        ? "🎮"
        : (IsRoot ? "🖥️" : "💾");

    public string FormattedCapacity => $"{UsedGb:F0} / {TotalGb:F0} GB";
    public string FormattedFree => $"{FreeGb:F0} GB free";
    public string FormattedPercent => $"{UsagePercent:F0}%";
}
