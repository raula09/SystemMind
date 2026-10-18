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

    public string FormattedCapacity => FormatUsage(UsedGb, TotalGb);
    public string FormattedFree => FreeGb < 1 ? $"{FreeGb * 1024:F0} MB free" : $"{FreeGb:F0} GB free";
    public string FormattedPercent => $"{UsagePercent:F0}%";

    /// <summary>"700 / 915 GB", or MB for small volumes such as /boot that would otherwise read "0 / 0 GB".</summary>
    public static string FormatUsage(double usedGb, double totalGb) => totalGb < 1
        ? $"{usedGb * 1024:F0} / {totalGb * 1024:F0} MB"
        : $"{usedGb:F0} / {totalGb:F0} GB";
}
