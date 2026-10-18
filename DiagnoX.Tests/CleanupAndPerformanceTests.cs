using DiagnoX.Models;
using DiagnoX.Services;
using DiagnoX.ViewModels;
using Xunit;

namespace DiagnoX.Tests;

public class CleanupAndPerformanceTests
{
    [Fact]
    public async Task CleanupScannerService_ScansTargetsWithoutThrowing()
    {
        var cleaner = new CleanupScannerService();
        var sampleRoot = new FileSystemNode
        {
            Name = "Project",
            FullPath = "/tmp/sample-project",
            IsDirectory = true,
            Size = 50 * 1024 * 1024
        };
        sampleRoot.Children.Add(new FileSystemNode
        {
            Name = "node_modules",
            FullPath = "/tmp/sample-project/node_modules",
            IsDirectory = true,
            Size = 35 * 1024 * 1024
        });

        var targets = await cleaner.ScanForCleanupTargetsAsync(sampleRoot);
        Assert.NotNull(targets);

        // Found node_modules candidate
        var nodeModulesTarget = targets.FirstOrDefault(t => t.Title.Contains("node_modules"));
        Assert.NotNull(nodeModulesTarget);
        Assert.Equal("Developer", nodeModulesTarget.Category);
        Assert.True(nodeModulesTarget.SizeBytes > 0);
    }

    [Fact]
    public void PerformanceViewModel_PopulatesTelemetryAndFilters()
    {
        var vm = new PerformanceViewModel();

        Assert.True(vm.AllProcesses.Count > 0);
        Assert.True(vm.FilteredProcesses.Count > 0);

        // Test filtering
        vm.SearchFilter = "non_existent_process_xyz_123456";
        Assert.Empty(vm.FilteredProcesses);

        vm.SearchFilter = "";
        Assert.True(vm.FilteredProcesses.Count > 0);
    }

    [Fact]
    public void StorageExplorerViewModel_SetsQuickPathsProperly()
    {
        var vm = new StorageExplorerViewModel();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Assert.Equal("/", vm.ScanPath); // Defaults to root /

        vm.SetQuickPathCommand.Execute("~");
        Assert.Equal(home, vm.ScanPath);

        vm.SetQuickPathCommand.Execute("~/Downloads");
        Assert.Equal(Path.Combine(home, "Downloads"), vm.ScanPath);

        vm.SetQuickPathCommand.Execute("/");
        Assert.Equal("/", vm.ScanPath);
    }
}
