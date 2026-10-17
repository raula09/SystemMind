using System.Diagnostics;
using System.Globalization;
using SystemMind.Models;

namespace SystemMind.Services;

public sealed class SystemMonitor
{
    public SystemSnapshot Capture()
    {
        var (totalKb, availableKb) = ReadMemory();
        var drives = DriveInfo.GetDrives().Where(d => d.IsReady && d.TotalSize > 0)
            .Select(d => new DiskUsage(d.Name, (d.TotalSize - d.AvailableFreeSpace) / Gb, d.TotalSize / Gb)).ToList();
        var processes = Process.GetProcesses().Select(p =>
        {
            try { return new ProcessUsage(p.ProcessName, p.WorkingSet64 / Mb); }
            catch { return new ProcessUsage(p.ProcessName, 0); }
        }).OrderByDescending(p => p.MemoryMb).Take(6).ToList();
        var cpu = ReadCpu();
        return new SystemSnapshot((totalKb - availableKb) / 1024d / 1024d, totalKb / 1024d / 1024d,
            drives.Sum(d => d.UsedGb), drives.Sum(d => d.TotalGb), cpu, processes, drives);
    }

    private static (double Total, double Available) ReadMemory()
    {
        const string memInfo = "/proc/meminfo";
        if (File.Exists(memInfo))
        {
            var values = File.ReadLines(memInfo).Select(line => line.Split(':', 2))
                .Where(p => p.Length == 2).ToDictionary(p => p[0], p => double.Parse(p[1].Trim().Split(' ')[0], CultureInfo.InvariantCulture));
            return (values.GetValueOrDefault("MemTotal"), values.GetValueOrDefault("MemAvailable"));
        }
        return (0, 0);
    }

    private static double ReadCpu()
    {
        try
        {
            var load = File.ReadLines("/proc/loadavg").First().Split(' ')[0];
            return Math.Min(100, double.Parse(load, CultureInfo.InvariantCulture) / Environment.ProcessorCount * 100);
        }
        catch { return 0; }
    }
    private const double Mb = 1024d * 1024d;
    private const double Gb = Mb * 1024d;
}
