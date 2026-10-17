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
        var physicalDrives = GetPhysicalDrives();

        var diskUsageList = physicalDrives.Select(d => new DiskUsage(
            d.DisplayName,
            d.UsedGb,
            d.TotalGb)).ToList();

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
        var diskUsed = physicalDrives.Sum(d => d.UsedGb);
        var diskTotal = physicalDrives.Sum(d => d.TotalGb);

        return new SystemSnapshot(
            usedGb,
            totalGb,
            diskUsed,
            diskTotal,
            cpu,
            topProcs,
            diskUsageList,
            sortedProcs,
            physicalDrives);
    }

    public static List<DriveItem> GetPhysicalDrives()
    {
        var results = new List<DriveItem>();
        var seenMounts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. On Linux, enumerate mounted filesystems and include real mounts beyond the root overlay.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var diskLabels = ReadLinuxDiskLabels();
            var pseudoFs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "tmpfs", "devtmpfs", "proc", "sysfs", "efivarfs", "cgroup", "cgroup2",
                "securityfs", "pstore", "bpf", "autofs", "fusectl", "configfs", "debugfs",
                "tracefs", "hugetlbfs", "mqueue", "fuse.portal", "ramfs", "devpts", "binfmt_misc", "overlay"
            };

            foreach (var mount in ReadLinuxMounts())
            {
                var dev = mount.Device;
                var mountPoint = mount.MountPoint;
                var fstype = mount.FileSystemType;

                if (seenMounts.Contains(mountPoint)) continue;
                seenMounts.Add(mountPoint);

                if (string.IsNullOrWhiteSpace(mountPoint)) continue;
                if (mountPoint.StartsWith("/proc", StringComparison.Ordinal) ||
                    mountPoint.StartsWith("/sys", StringComparison.Ordinal) ||
                    mountPoint.StartsWith("/dev", StringComparison.Ordinal) && fstype == "devtmpfs")
                {
                    continue;
                }

                if (pseudoFs.Contains(fstype) && mountPoint != "/") continue;
                if (mountPoint.StartsWith("/var/lib/docker", StringComparison.Ordinal) || mountPoint.StartsWith("/run", StringComparison.Ordinal)) continue;

                try
                {
                    var di = new DriveInfo(mountPoint);
                    if (!di.IsReady || di.TotalSize <= 100 * 1024 * 1024) continue;

                    var total = di.TotalSize / Gb;
                    var free = di.AvailableFreeSpace / Gb;
                    var used = Math.Max(0, total - free);

                    var label = diskLabels.GetValueOrDefault(dev, "");
                    if (string.IsNullOrEmpty(label) && !string.IsNullOrEmpty(di.VolumeLabel) && di.VolumeLabel != di.Name)
                    {
                        label = di.VolumeLabel;
                    }
                    if (string.IsNullOrEmpty(label) && mountPoint == "/")
                    {
                        label = "System Root";
                    }
                    if (string.IsNullOrEmpty(label) && mountPoint.StartsWith("/mnt/", StringComparison.Ordinal))
                    {
                        label = mountPoint[5..];
                    }
                    if (string.IsNullOrEmpty(label) && mountPoint.StartsWith("/media/", StringComparison.Ordinal))
                    {
                        label = Path.GetFileName(mountPoint);
                    }

                    results.Add(new DriveItem
                    {
                        Device = dev,
                        MountPoint = mountPoint,
                        Label = label,
                        FileSystem = fstype,
                        TotalGb = total,
                        UsedGb = used
                    });
                }
                catch
                {
                    // For overlay or bind mounts, add a lightweight fallback entry so the mount still appears.
                    if (mountPoint == "/")
                    {
                        results.Add(new DriveItem
                        {
                            Device = dev,
                            MountPoint = mountPoint,
                            Label = "System Root",
                            FileSystem = fstype,
                            TotalGb = 0,
                            UsedGb = 0
                        });
                    }
                }
            }
        }

        // 2. Cross-platform / Windows / macOS fallback
        if (results.Count == 0)
        {
            try
            {
                foreach (var d in DriveInfo.GetDrives())
                {
                    try
                    {
                        if (!d.IsReady || d.TotalSize <= 0) continue;
                        if (d.DriveType != DriveType.Fixed && d.DriveType != DriveType.Removable && d.DriveType != DriveType.Network) continue;

                        var total = d.TotalSize / Gb;
                        var free = d.AvailableFreeSpace / Gb;
                        var used = Math.Max(0, total - free);

                        var label = !string.IsNullOrEmpty(d.VolumeLabel) && d.VolumeLabel != d.Name ? d.VolumeLabel : "";

                        results.Add(new DriveItem
                        {
                            Device = d.Name,
                            MountPoint = d.Name,
                            Label = label,
                            FileSystem = d.DriveFormat,
                            TotalGb = total,
                            UsedGb = used
                        });
                    }
                    catch { }
                }
            }
            catch { }
        }

        // Sort so system root is first, followed by others sorted descending by size.
        return results
            .GroupBy(d => d.MountPoint, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderByDescending(d => d.IsRoot)
            .ThenByDescending(d => d.TotalGb)
            .ToList();
    }

    private static List<(string Device, string MountPoint, string FileSystemType)> ReadLinuxMounts()
    {
        var results = new List<(string Device, string MountPoint, string FileSystemType)>();
        var mountFiles = new[]
        {
            "/proc/self/mountinfo",
            "/proc/mounts"
        };

        foreach (var file in mountFiles)
        {
            if (!File.Exists(file)) continue;

            try
            {
                foreach (var line in File.ReadLines(file))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    if (file.EndsWith("/mounts", StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length < 3) continue;
                        var device = parts[0];
                        var mountPoint = parts[1];
                        var fstype = parts[2];
                        results.Add((device, mountPoint, fstype));
                    }
                    else
                    {
                        var parts = line.Split(' ');
                        if (parts.Length < 10) continue;
                        var mountPoint = parts[4];
                        var sep = Array.IndexOf(parts, "-");
                        if (sep < 0 || sep + 2 >= parts.Length) continue;
                        var fstype = parts[sep + 1];
                        var device = parts[sep + 2];
                        results.Add((device, mountPoint, fstype));
                    }
                }
            }
            catch { }
        }

        return results;
    }

    private static Dictionary<string, string> ReadLinuxDiskLabels()
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        const string byLabelDir = "/dev/disk/by-label";
        if (Directory.Exists(byLabelDir))
        {
            try
            {
                foreach (var link in Directory.EnumerateFiles(byLabelDir))
                {
                    try
                    {
                        var label = Path.GetFileName(link);
                        var target = new FileInfo(link).LinkTarget;
                        if (!string.IsNullOrEmpty(target))
                        {
                            var fullTarget = Path.GetFullPath(Path.Combine(byLabelDir, target));
                            dict[fullTarget] = label;
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }
        return dict;
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

        var total = 16.0;
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
