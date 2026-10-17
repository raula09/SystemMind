using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using SystemMind.Models;

namespace SystemMind.Services;

public sealed class SystemMonitor
{
    private const double Mb = 1024d * 1024d;
    private const double Gb = Mb * 1024d;
    private readonly Dictionary<int, (TimeSpan Cpu, DateTime SampledAt)> _processCpu = [];
    private (ulong Idle, ulong Kernel, ulong User)? _previousSystemTimes;
    private (ulong Idle, ulong Total)? _previousLinuxCpu;

    public SystemSnapshot Capture()
    {
        var (totalGb, usedGb) = ReadMemory();
        var gpuUsage = ReadGpu();
        var physicalDrives = GetPhysicalDrives();
        var diskUsageList = physicalDrives.Select(d => new DiskUsage(
            d.DisplayName,
            d.UsedGb,
            d.TotalGb)).ToList();

        var now = DateTime.UtcNow;
        var allProcs = new List<ProcessDetailedInfo>();
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    var cpuTime = p.TotalProcessorTime;
                    var cpu = 0d;
                    if (_processCpu.TryGetValue(p.Id, out var previous))
                    {
                        var seconds = (now - previous.SampledAt).TotalSeconds;
                        if (seconds > 0)
                            cpu = Math.Clamp((cpuTime - previous.Cpu).TotalSeconds / (seconds * Environment.ProcessorCount) * 100, 0, 100);
                    }

                    _processCpu[p.Id] = (cpuTime, now);
                    allProcs.Add(new ProcessDetailedInfo(
                        p.Id,
                        p.ProcessName,
                        p.WorkingSet64 / Mb,
                        cpu,
                        "",
                        Categorize(p.ProcessName),
                        gpuUsage.TotalPercent.HasValue ? gpuUsage.ProcessPercent.GetValueOrDefault(p.Id) : null));
                }
                catch { }
                finally { p.Dispose(); }
            }
        }
        catch { }

        var runningIds = allProcs.Select(p => p.Id).ToHashSet();
        foreach (var staleId in _processCpu.Keys.Where(id => !runningIds.Contains(id)).ToArray())
            _processCpu.Remove(staleId);

        var sortedProcs = allProcs.OrderByDescending(p => p.MemoryMb).ToList();
        var topProcs = allProcs.OrderByDescending(p => p.CpuPercent).Take(8)
            .Select(p => new ProcessUsage(p.Name, p.MemoryMb)).ToList();
        var cpuTotal = ReadCpu();
        var diskUsed = physicalDrives.Sum(d => d.UsedGb);
        var diskTotal = physicalDrives.Sum(d => d.TotalGb);

        return new SystemSnapshot(
            usedGb,
            totalGb,
            diskUsed,
            diskTotal,
            cpuTotal,
            topProcs,
            diskUsageList,
            sortedProcs,
            gpuUsage.TotalPercent,
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
            using var p = Process.GetProcessById(pid);
            p.Kill();
            return true;
        }
        catch { return false; }
    }

    private static string Categorize(string name)
    {
        var process = name.ToLowerInvariant();
        if (new[] { "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "safari" }.Any(process.Contains)) return "Browsing";
        if (new[] { "steam", "game", "epic", "battle.net", "riot", "minecraft", "valorant", "unity", "unreal" }.Any(process.Contains)) return "Gaming";
        if (new[] { "code", "devenv", "rider", "idea", "pycharm", "webstorm", "dotnet", "node", "python", "java", "git", "docker", "wsl" }.Any(process.Contains)) return "Development";
        if (new[] { "system", "idle", "services", "svchost", "csrss", "wininit", "winlogon", "explorer", "dwm", "lsass", "registry", "kernel", "launchd", "kworker", "systemd" }.Any(process.Contains)) return "System";
        return "Other";
    }

    private static (double TotalGb, double UsedGb) ReadMemory()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (GlobalMemoryStatusEx(ref status))
                return (status.TotalPhysical / Gb, (status.TotalPhysical - status.AvailablePhysical) / Gb);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && File.Exists("/proc/meminfo"))
        {
            try
            {
                var dict = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in File.ReadLines("/proc/meminfo"))
                {
                    var parts = line.Split(':', 2);
                    if (parts.Length == 2 && double.TryParse(parts[1].Trim().Split(' ')[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var kb))
                        dict[parts[0].Trim()] = kb;
                }
                var totalKb = dict.GetValueOrDefault("MemTotal", 0);
                var availKb = dict.GetValueOrDefault("MemAvailable", 0);
                if (totalKb > 0) return (totalKb / 1024d / 1024d, Math.Max(0, totalKb - availKb) / 1024d / 1024d);
            }
            catch { }
        }

        var total = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / Gb;
        if (total <= 0) total = 16;
        return (total, Math.Min(total, GC.GetTotalMemory(false) / Gb));
    }

    private double ReadCpu()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (GetSystemTimes(out var idle, out var kernel, out var user))
            {
                var current = (idle.ToUInt64(), kernel.ToUInt64(), user.ToUInt64());
                var result = 0d;
                if (_previousSystemTimes is { } previous)
                {
                    var total = (current.Item2 - previous.Kernel) + (current.Item3 - previous.User);
                    var idleDelta = current.Item1 - previous.Idle;
                    if (total > 0) result = Math.Clamp((double)(total - idleDelta) / total * 100, 0, 100);
                }
                _previousSystemTimes = current;
                return result;
            }
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            try
            {
                var parts = File.ReadLines("/proc/stat").First().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var values = parts.Skip(1).Select(v => ulong.Parse(v, CultureInfo.InvariantCulture)).ToArray();
                var idle = values[3] + (values.Length > 4 ? values[4] : 0);
                var total = values.Aggregate(0UL, (sum, value) => sum + value);
                var result = 0d;
                if (_previousLinuxCpu is { } previous && total > previous.Total)
                    result = Math.Clamp((double)((total - previous.Total) - (idle - previous.Idle)) / (total - previous.Total) * 100, 0, 100);
                _previousLinuxCpu = (idle, total);
                return result;
            }
            catch { }
        }

        return 0;
    }

    private static GpuUsage ReadGpu()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return new GpuUsage(null, new Dictionary<int, double>());
        IntPtr query = IntPtr.Zero, counter = IntPtr.Zero;
        try
        {
            if (PdhOpenQueryW(null, IntPtr.Zero, out query) != 0) return new GpuUsage(null, new Dictionary<int, double>());
            if (PdhAddEnglishCounterW(query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out counter) != 0) return new GpuUsage(null, new Dictionary<int, double>());
            PdhCollectQueryData(query);
            Thread.Sleep(80);
            PdhCollectQueryData(query);
            uint bytes = 0, count = 0;
            var status = PdhGetFormattedCounterArrayW(counter, 0x00000200, ref bytes, ref count, IntPtr.Zero);
            if (status != 0x800007D2 || bytes == 0) return new GpuUsage(null, new Dictionary<int, double>());
            var buffer = Marshal.AllocHGlobal((int)bytes);
            try
            {
                if (PdhGetFormattedCounterArrayW(counter, 0x00000200, ref bytes, ref count, buffer) != 0)
                    return new GpuUsage(null, new Dictionary<int, double>());
                var entrySize = Marshal.SizeOf<PdhFormattedCounterItem>();
                var result = 0d;
                var byProcess = new Dictionary<int, double>();
                for (var i = 0; i < count; i++)
                {
                    var value = Marshal.PtrToStructure<PdhFormattedCounterItem>(IntPtr.Add(buffer, i * entrySize));
                    if (value.Value.Status != 0) continue;
                    var utilization = Math.Max(0, value.Value.DoubleValue);
                    result += utilization;

                    try
                    {
                        if (value.Name == IntPtr.Zero) continue;
                        var instanceName = Marshal.PtrToStringUni(value.Name) ?? "";
                        var pidStart = instanceName.IndexOf("pid_", StringComparison.OrdinalIgnoreCase);
                        if (pidStart < 0) continue;
                        pidStart += 4;
                        var pidEnd = instanceName.IndexOf('_', pidStart);
                        if (pidEnd <= pidStart || !int.TryParse(instanceName.AsSpan(pidStart, pidEnd - pidStart), out var pid)) continue;
                        byProcess[pid] = byProcess.GetValueOrDefault(pid) + utilization;
                    }
                    catch { /* skip entries with bad name pointers */ }
                }
                return new GpuUsage(
                    Math.Clamp(result, 0, 100),
                    byProcess.ToDictionary(pair => pair.Key, pair => Math.Clamp(pair.Value, 0, 100)));
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        catch { return new GpuUsage(null, new Dictionary<int, double>()); }
        finally { if (query != IntPtr.Zero) PdhCloseQuery(query); }
    }

    private sealed record GpuUsage(double? TotalPercent, IReadOnlyDictionary<int, double> ProcessPercent);

    [StructLayout(LayoutKind.Sequential)] private struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [StructLayout(LayoutKind.Sequential)] private struct PdhCounterValue { public uint Status; public double DoubleValue; }
    [StructLayout(LayoutKind.Sequential)] private struct PdhFormattedCounterItem { public IntPtr Name; public PdhCounterValue Value; }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);
    [StructLayout(LayoutKind.Sequential)] private struct FileTime { public uint Low, High; public ulong ToUInt64() => ((ulong)High << 32) | Low; }
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, ref uint itemCount, IntPtr items);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(IntPtr query);
}
