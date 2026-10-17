using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using SystemMind.Models;

namespace SystemMind.Services;

public sealed class SystemMonitor
{
    private const double Mb = 1024d * 1024d;
    private const double Gb = Mb * 1024d;

    public SystemSnapshot Capture()
    {
        var (totalGb, usedGb) = ReadMemory();
        var drives = DriveInfo.GetDrives()
            .Where(d => d.IsReady && d.TotalSize > 0)
            .Select(d => new DiskUsage(
                string.IsNullOrEmpty(d.VolumeLabel) ? d.Name : $"{d.Name} ({d.VolumeLabel})",
                (d.TotalSize - d.AvailableFreeSpace) / Gb,
                d.TotalSize / Gb))
            .ToList();

        var allProcs = new List<ProcessDetailedInfo>();
        try
        {
            var processes = Process.GetProcesses();
            foreach (var p in processes)
            {
                try
                {
                    var mem = p.WorkingSet64 / Mb;
                    allProcs.Add(new ProcessDetailedInfo(p.Id, p.ProcessName, mem, 0, ""));
                }
                catch { }
            }
        }
        catch { }

        var sortedProcs = allProcs.OrderByDescending(p => p.MemoryMb).ToList();
        var topProcs = sortedProcs.Take(8).Select(p => new ProcessUsage(p.Name, p.MemoryMb)).ToList();

        var cpu = ReadCpu();
        var diskUsed = drives.Sum(d => d.UsedGb);
        var diskTotal = drives.Sum(d => d.TotalGb);

        return new SystemSnapshot(
            usedGb,
            totalGb,
            diskUsed,
            diskTotal,
            cpu,
            topProcs,
            drives,
            sortedProcs);
    }

    public bool TerminateProcess(int pid)
    {
        try
        {
            var p = Process.GetProcessById(pid);
            p.Kill();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static (double TotalGb, double UsedGb) ReadMemory()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            const string memInfo = "/proc/meminfo";
            if (File.Exists(memInfo))
            {
                try
                {
                    var dict = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                    foreach (var line in File.ReadLines(memInfo))
                    {
                        var parts = line.Split(':', 2);
                        if (parts.Length == 2)
                        {
                            var key = parts[0].Trim();
                            var valStr = parts[1].Trim().Split(' ')[0];
                            if (double.TryParse(valStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var kb))
                            {
                                dict[key] = kb;
                            }
                        }
                    }

                    var totalKb = dict.GetValueOrDefault("MemTotal", 0);
                    var availKb = dict.GetValueOrDefault("MemAvailable", 0);
                    var usedKb = Math.Max(0, totalKb - availKb);

                    return (totalKb / 1024d / 1024d, usedKb / 1024d / 1024d);
                }
                catch { }
            }
        }

        // Generic fallback using GC / Process
        var total = 16.0; // Estimate
        var used = GC.GetTotalMemory(false) / Gb;
        return (total, used);
    }

    private static double ReadCpu()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            try
            {
                var load = File.ReadLines("/proc/loadavg").FirstOrDefault()?.Split(' ')[0];
                if (double.TryParse(load, NumberStyles.Any, CultureInfo.InvariantCulture, out var val))
                {
                    return Math.Clamp((val / Environment.ProcessorCount) * 100.0, 0, 100);
                }
            }
            catch { }
        }

        return 0;
    }
}
