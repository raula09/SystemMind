namespace SystemMind.Models;

public record SystemSnapshot(
    double MemoryUsedGb, double MemoryTotalGb, double DiskUsedGb, double DiskTotalGb,
    double CpuPercent, IReadOnlyList<ProcessUsage> Processes, IReadOnlyList<DiskUsage> Disks,
    IReadOnlyList<ProcessDetailedInfo>? AllProcesses = null,
    double? GpuPercent = null,
    IReadOnlyList<DriveItem>? PhysicalDrives = null,
    HardwareTelemetry? Hardware = null)
{
    public double MemoryPercent => MemoryTotalGb <= 0 ? 0 : MemoryUsedGb / MemoryTotalGb * 100;
    public double DiskPercent => DiskTotalGb <= 0 ? 0 : DiskUsedGb / DiskTotalGb * 100;
}

public record ProcessUsage(string Name, double MemoryMb);

public record DiskUsage(string Name, double UsedGb, double TotalGb);

public record ProcessDetailedInfo(int Id, string Name, double MemoryMb, double CpuPercent, string User = "", string Category = "Other", double? GpuPercent = null);

/// <summary>Hardware values which are not available on every operating system or device.</summary>
public record HardwareTelemetry(
    string? GpuName = null,
    double? GpuMemoryGb = null,
    double? BatteryPercent = null,
    string? BatteryStatus = null,
    double? BatteryHealthPercent = null,
    double? CpuTemperatureC = null,
    double? MaxTemperatureC = null,
    long NetworkReceivedBytes = 0,
    long NetworkSentBytes = 0,
    double NetworkReceiveBytesPerSecond = 0,
    double NetworkSendBytesPerSecond = 0);
