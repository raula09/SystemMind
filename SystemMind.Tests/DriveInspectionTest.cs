using System.IO;
using Xunit;
using Xunit.Abstractions;

namespace SystemMind.Tests;

public class DriveInspectionTest(ITestOutputHelper output)
{
    [Fact]
    public void PrintDrives()
    {
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                output.WriteLine($"Drive: {d.Name} | Ready: {d.IsReady} | Size: {d.TotalSize / (1024*1024*1024)} GB | Free: {d.AvailableFreeSpace / (1024*1024*1024)} GB | Label: {d.VolumeLabel}");
            }
            catch (Exception ex)
            {
                output.WriteLine($"Drive: {d.Name} error: {ex.Message}");
            }
        }
    }
}
