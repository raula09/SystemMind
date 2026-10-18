using DiagnoX.Models;
using DiagnoX.Services;
using Xunit;

namespace DiagnoX.Tests;

public class AdvisorServiceTests
{
    private readonly SystemSnapshot _sampleSnapshot = new(
        MemoryUsedGb: 12.0,
        MemoryTotalGb: 16.0,
        DiskUsedGb: 180.0,
        DiskTotalGb: 250.0,
        CpuPercent: 35.0,
        Processes: new[] { new ProcessUsage("code", 1400), new ProcessUsage("chrome", 1200) },
        Disks: new[] { new DiskUsage("/", 180, 250) });

    [Fact]
    public void LocalExpertAnswer_WhenAskedAboutSpace_IncludesScanDetails()
    {
        var scanRoot = new FileSystemNode
        {
            Name = "Raul",
            FullPath = "/home/Raul",
            IsDirectory = true,
            Size = 50L * 1024 * 1024 * 1024, // 50 GB
            FileCount = 12500
        };

        var downloads = new FileSystemNode
        {
            Name = "Downloads",
            FullPath = "/home/Raul/Downloads",
            IsDirectory = true,
            Size = 25L * 1024 * 1024 * 1024,
            PercentageOfRoot = 50.0
        };
        scanRoot.Children.Add(downloads);

        var answer = AdvisorService.LocalExpertAnswer("What is taking up all my space?", _sampleSnapshot, scanRoot);

        Assert.Contains("Scan Breakdown", answer);
        Assert.Contains("Downloads", answer);
        Assert.Contains("50 GB", answer);
    }

    [Fact]
    public void LocalExpertAnswer_WhenAskedAboutCleanup_ProvidesSafeAdvice()
    {
        var answer = AdvisorService.LocalExpertAnswer("What can I safely clean to free space?", _sampleSnapshot);

        Assert.Contains("Safe Disk Cleanup Recommendations", answer);
        Assert.Contains("Trash", answer);
        Assert.Contains("Thumbnail Cache", answer);
    }

    [Fact]
    public void LocalExpertAnswer_WhenAskedAboutNodeModules_ExplainsAndAdvises()
    {
        var answer = AdvisorService.LocalExpertAnswer("Can I delete node_modules?", _sampleSnapshot);

        Assert.Contains("node_modules", answer);
        Assert.Contains("safe to delete", answer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LocalExpertAnswer_WhenAskedAboutRAM_ReportsMemoryMetrics()
    {
        var answer = AdvisorService.LocalExpertAnswer("Why is my RAM usage high?", _sampleSnapshot);

        Assert.Contains("12.0 GB", answer);
        Assert.Contains("16.0 GB", answer);
    }

    [Fact]
    public void LocalExpertAnswer_WhenAskedAboutGame_ChecksSpecs()
    {
        var answer = AdvisorService.LocalExpertAnswer("Can I run modern games like Cyberpunk?", _sampleSnapshot);

        Assert.Contains("Hardware & Game Readiness", answer);
        Assert.Contains("16.0 GB", answer);
        Assert.Contains("Storage", answer);
    }

    [Fact]
    public void LocalExpertAnswer_WhenScanningInProgress_ReportsProgress()
    {
        var answer = AdvisorService.LocalExpertAnswer(
            "What is taking up all my space? Break down the scan.",
            _sampleSnapshot,
            scanRoot: null,
            extensions: null,
            isScanning: true,
            currentScanPath: "/home/Raul",
            scannedFiles: 14200,
            scannedBytes: 15L * 1024 * 1024 * 1024);

        Assert.Contains("Scan in Progress", answer);
        Assert.Contains("14,200 files", answer);
        Assert.Contains("15 GB", answer);
    }

    [Fact]
    public async Task AskAsync_WithKeyFromEnv_ProducesCleanAnswer()
    {
        var advisor = new AdvisorService();
        var answer = await advisor.AskAsync("Say hello in three words", _sampleSnapshot);

        Assert.False(string.IsNullOrWhiteSpace(answer));
        Assert.DoesNotContain("API Error", answer);
        Assert.DoesNotContain("Provider returned error", answer);
    }
}
