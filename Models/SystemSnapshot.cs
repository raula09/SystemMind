namespace SystemMind.Models;

public record SystemSnapshot(
    double MemoryUsedGb, double MemoryTotalGb, double DiskUsedGb, double DiskTotalGb,
    double CpuPercent, IReadOnlyList<ProcessUsage> Processes, IReadOnlyList<DiskUsage> Disks,
    IReadOnlyList<ProcessDetailedInfo>? AllProcesses = null)
{
    public double MemoryPercent => MemoryTotalGb <= 0 ? 0 : MemoryUsedGb / MemoryTotalGb * 100;
    public double DiskPercent => DiskTotalGb <= 0 ? 0 : DiskUsedGb / DiskTotalGb * 100;
}

public record ProcessUsage(string Name, double MemoryMb);

public record DiskUsage(string Name, double UsedGb, double TotalGb);

public record ProcessDetailedInfo(int Id, string Name, double MemoryMb, double CpuPercent, string User = "");
