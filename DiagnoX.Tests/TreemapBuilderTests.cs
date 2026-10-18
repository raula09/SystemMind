using DiagnoX.Models;
using DiagnoX.Services;
using Xunit;

namespace DiagnoX.Tests;

public class TreemapBuilderTests
{
    [Fact]
    public void Build_WithEmptyTree_ReturnsEmpty()
    {
        var builder = new TreemapBuilder();
        var empty = new FileSystemNode { Size = 0 };
        var items = builder.Build(empty, 800, 600);
        Assert.Empty(items);
    }

    [Fact]
    public void Build_GeneratesValidBoundingRectanglesWithinCanvas()
    {
        var builder = new TreemapBuilder();

        var root = new FileSystemNode
        {
            Name = "Root",
            IsDirectory = true,
            Size = 100000
        };

        var f1 = new FileSystemNode { Name = "BigVideo.mp4", Size = 50000, Category = "Video", ColorHex = "#9B51E0" };
        var f2 = new FileSystemNode { Name = "Archive.zip", Size = 30000, Category = "Archive", ColorHex = "#F2994A" };
        var f3 = new FileSystemNode { Name = "Docs.pdf", Size = 20000, Category = "Document", ColorHex = "#56CCF2" };

        root.Children.Add(f1);
        root.Children.Add(f2);
        root.Children.Add(f3);

        double width = 800;
        double height = 500;
        var items = builder.Build(root, width, height);

        Assert.NotEmpty(items);
        Assert.Equal(3, items.Count);

        foreach (var item in items)
        {
            Assert.True(item.X >= 0, $"X should be >= 0, was {item.X}");
            Assert.True(item.Y >= 0, $"Y should be >= 0, was {item.Y}");
            Assert.True(item.X + item.Width <= width + 1.0, $"Right edge {item.X + item.Width} exceeded canvas {width}");
            Assert.True(item.Y + item.Height <= height + 1.0, $"Bottom edge {item.Y + item.Height} exceeded canvas {height}");
            Assert.True(item.Width > 0, "Width should be positive");
            Assert.True(item.Height > 0, "Height should be positive");
            Assert.False(string.IsNullOrEmpty(item.ColorHex));
        }

        // The largest item (BigVideo.mp4) should have the largest rectangle area
        var bigVideoItem = items.First(i => i.Node.Name == "BigVideo.mp4");
        var docsItem = items.First(i => i.Node.Name == "Docs.pdf");
        Assert.True(bigVideoItem.Width * bigVideoItem.Height > docsItem.Width * docsItem.Height);
    }
}
