namespace DiagnoX.Models;

public sealed class TreemapItem
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }

    public FileSystemNode Node { get; set; } = null!;
    public string ColorHex { get; set; } = "#3B82F6";
    public string Label => Node.Name;
    public string FormattedSize => Node.FormattedSize;

    public bool Contains(double px, double py) =>
        px >= X && px <= (X + Width) && py >= Y && py <= (Y + Height);
}
