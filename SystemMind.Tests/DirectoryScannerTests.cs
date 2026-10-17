using SystemMind.Models;
using SystemMind.Services;
using Xunit;

namespace SystemMind.Tests;

public class DirectoryScannerTests : IDisposable
{
    private readonly string _testDir;

    public DirectoryScannerTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "SystemMind_ScannerTest_" + Guid.NewGuid());
        Directory.CreateDirectory(_testDir);

        // Create subdirectories and sample files with known content
        var sub1 = Path.Combine(_testDir, "Sub1");
        var sub2 = Path.Combine(_testDir, "Sub2");
        Directory.CreateDirectory(sub1);
        Directory.CreateDirectory(sub2);

        File.WriteAllBytes(Path.Combine(_testDir, "video.mp4"), new byte[5000]);
        File.WriteAllBytes(Path.Combine(sub1, "doc.pdf"), new byte[2000]);
        File.WriteAllBytes(Path.Combine(sub1, "archive.zip"), new byte[3000]);
        File.WriteAllBytes(Path.Combine(sub2, "script.py"), new byte[1000]);
    }

    public void Dispose()
    {
        try { Directory.Delete(_testDir, true); } catch { }
    }

    [Fact]
    public async Task ScanAsync_ComputesCorrectSizesAndExtensions()
    {
        var scanner = new DirectoryScanner();
        var (root, extensions) = await scanner.ScanAsync(_testDir, null, CancellationToken.None);

        Assert.NotNull(root);
        Assert.True(root.IsDirectory);
        Assert.Equal(11000, root.Size);
        Assert.Equal(4, root.FileCount);
        Assert.Equal(2, root.DirectoryCount);

        // Extensions breakdown
        Assert.NotEmpty(extensions);
        var mp4 = extensions.FirstOrDefault(e => e.Extension == ".mp4");
        Assert.NotNull(mp4);
        Assert.Equal(5000, mp4.TotalBytes);
        Assert.Equal("Video", mp4.Category);

        var zip = extensions.FirstOrDefault(e => e.Extension == ".zip");
        Assert.NotNull(zip);
        Assert.Equal(3000, zip.TotalBytes);
        Assert.Equal("Archive", zip.Category);

        var pdf = extensions.FirstOrDefault(e => e.Extension == ".pdf");
        Assert.NotNull(pdf);
        Assert.Equal(2000, pdf.TotalBytes);
        Assert.Equal("Document", pdf.Category);

        var py = extensions.FirstOrDefault(e => e.Extension == ".py");
        Assert.NotNull(py);
        Assert.Equal(1000, py.TotalBytes);
        Assert.Equal("Code", py.Category);
    }

    [Fact]
    public async Task ScanAsync_SortsChildrenDescendingBySize()
    {
        var scanner = new DirectoryScanner();
        var (root, _) = await scanner.ScanAsync(_testDir, null, CancellationToken.None);

        Assert.True(root.Children.Count >= 2);
        for (int i = 0; i < root.Children.Count - 1; i++)
        {
            Assert.True(root.Children[i].Size >= root.Children[i + 1].Size,
                $"Child {root.Children[i].Name} ({root.Children[i].Size}) should be >= {root.Children[i + 1].Name} ({root.Children[i + 1].Size})");
        }
    }

    [Fact]
    public async Task ScanAsync_RespectsCancellationToken()
    {
        var scanner = new DirectoryScanner();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            scanner.ScanAsync(_testDir, null, cts.Token));
    }
}
