namespace DiagnoX.Models;

public sealed class ExtensionStat
{
    public string Extension { get; set; } = "";
    public string Category { get; set; } = "Other";
    public string ColorHex { get; set; } = "#7B8B9E";
    public long TotalBytes { get; set; }
    public int FileCount { get; set; }
    public double Percentage { get; set; }

    public string FormattedSize => FileSystemNode.FormatBytes(TotalBytes);
    public string FormattedPercent => $"{Percentage:F1}%";
}
