using System.Diagnostics;
using System.Globalization;
using Microsoft.Win32;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using DiagnoX.Models;

namespace DiagnoX.Services;

public sealed class SystemMonitor
{
    private const double Mb = 1024d * 1024d;
    private const double Gb = Mb * 1024d;
    private readonly Dictionary<int, (TimeSpan Cpu, DateTime SampledAt)> _processCpu = [];
    private (ulong Idle, ulong Kernel, ulong User)? _previousSystemTimes;
    private (ulong Idle, ulong Total)? _previousLinuxCpu;
    private (long Received, long Sent, DateTime SampledAt)? _previousNetwork;
    private (double? Celsius, DateTime SampledAt) _nvidiaTemperature;
    private static readonly string? NvidiaSmiPath = FindNvidiaSmi();

    public SystemSnapshot Capture()
    {
        var (totalGb, usedGb) = ReadMemory();
        var gpuUsage = ReadGpu();
        var hardware = ReadHardwareTelemetry(gpuUsage);
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
            physicalDrives,
            hardware);
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
                        var device = DecodeMountField(parts[0]);
                        var mountPoint = DecodeMountField(parts[1]);
                        var fstype = parts[2];
                        results.Add((device, mountPoint, fstype));
                    }
                    else
                    {
                        var parts = line.Split(' ');
                        if (parts.Length < 10) continue;
                        var mountPoint = DecodeMountField(parts[4]);
                        var sep = Array.IndexOf(parts, "-");
                        if (sep < 0 || sep + 2 >= parts.Length) continue;
                        var fstype = parts[sep + 1];
                        var device = DecodeMountField(parts[sep + 2]);
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
                        var label = DecodeUdevName(Path.GetFileName(link));
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

    /// <summary>Decodes the octal escapes (<c>\040</c> = space) the kernel uses in /proc mount tables.</summary>
    public static string DecodeMountField(string value)
    {
        if (!value.Contains('\\')) return value;

        var result = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 3 < value.Length &&
                value.AsSpan(i + 1, 3).IndexOfAnyExcept("01234567") < 0)
            {
                result.Append((char)Convert.ToInt32(value.Substring(i + 1, 3), 8));
                i += 3;
                continue;
            }
            result.Append(value[i]);
        }
        return result.ToString();
    }

    /// <summary>Decodes udev's <c>\xHH</c> escapes in /dev/disk/by-label names (<c>NVME\x201TB</c> = "NVME 1TB").</summary>
    public static string DecodeUdevName(string value)
    {
        if (!value.Contains("\\x", StringComparison.Ordinal)) return value;

        // Escapes are raw UTF-8 bytes, so collect consecutive ones before decoding.
        var result = new StringBuilder(value.Length);
        var pending = new List<byte>();
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 3 < value.Length && value[i + 1] == 'x' &&
                byte.TryParse(value.AsSpan(i + 2, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var b))
            {
                pending.Add(b);
                i += 3;
                continue;
            }
            FlushPending();
            result.Append(value[i]);
        }
        FlushPending();
        return result.ToString();

        void FlushPending()
        {
            if (pending.Count == 0) return;
            result.Append(Encoding.UTF8.GetString(pending.ToArray()));
            pending.Clear();
        }
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

    private HardwareTelemetry ReadHardwareTelemetry(GpuUsage gpuUsage)
    {
        var (received, sent, receiveRate, sendRate) = ReadNetwork();
        var battery = ReadBattery();
        var temperatures = ReadTemperatures(gpuUsage);
        var detectedGpu = ReadGpuDetails();
        var gpu = (Name: gpuUsage.Name ?? detectedGpu.Name, MemoryGb: gpuUsage.MemoryGb ?? detectedGpu.MemoryGb);

        return new HardwareTelemetry(
            gpu.Name,
            gpu.MemoryGb,
            battery.Percent,
            battery.Status,
            battery.HealthPercent,
            ThermalSensors.CpuTemperature(temperatures),
            temperatures.Count == 0 ? null : temperatures.Max(reading => reading.Celsius),
            received,
            sent,
            receiveRate,
            sendRate,
            temperatures);
    }

    private (long Received, long Sent, double ReceiveRate, double SendRate) ReadNetwork()
    {
        long received = 0, sent = 0;
        try
        {
            foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (network.OperationalStatus != OperationalStatus.Up ||
                    network.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;

                var stats = network.GetIPv4Statistics();
                received += stats.BytesReceived;
                sent += stats.BytesSent;
            }
        }
        catch { }

        var now = DateTime.UtcNow;
        var receiveRate = 0d;
        var sendRate = 0d;
        if (_previousNetwork is { } previous)
        {
            var seconds = (now - previous.SampledAt).TotalSeconds;
            if (seconds > 0)
            {
                receiveRate = Math.Max(0, received - previous.Received) / seconds;
                sendRate = Math.Max(0, sent - previous.Sent) / seconds;
            }
        }
        _previousNetwork = (received, sent, now);
        return (received, sent, receiveRate, sendRate);
    }

    private static (double? Percent, string? Status, double? HealthPercent) ReadBattery()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                if (GetSystemPowerStatus(out var status) && status.BatteryLifePercent != byte.MaxValue)
                {
                    var state = (status.BatteryFlag & 128) != 0 ? "No battery" :
                        (status.ACLineStatus == 1 ? "Charging" : "Discharging");
                    return (status.BatteryLifePercent, state, null);
                }
            }
            catch { }
        }

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return (null, null, null);
        try
        {
            // Wireless mice, keyboards and headsets also report type=Battery, but with scope=Device.
            var batteries = Directory.EnumerateDirectories("/sys/class/power_supply")
                .Where(path => ReadText(Path.Combine(path, "type")).Equals("Battery", StringComparison.OrdinalIgnoreCase) &&
                               !ReadText(Path.Combine(path, "scope")).Equals("Device", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (batteries.Count == 0) return (null, null, null);

            var capacities = batteries.Select(path => ReadDouble(Path.Combine(path, "capacity"))).Where(value => value.HasValue).ToList();
            var statuses = batteries.Select(path => ReadText(Path.Combine(path, "status"))).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct().ToList();
            var health = batteries.Select(path => RatioPercent(path, "energy_full", "energy_full_design") ?? RatioPercent(path, "charge_full", "charge_full_design"))
                .Where(value => value.HasValue).ToList();

            return (
                capacities.Count == 0 ? null : capacities.Average(),
                statuses.Contains("Charging", StringComparer.OrdinalIgnoreCase) ? "Charging" : statuses.FirstOrDefault() ?? "Unknown",
                health.Count == 0 ? null : health.Average());
        }
        catch { return (null, null, null); }
    }

    private List<TemperatureReading> ReadTemperatures(GpuUsage gpuUsage)
    {
        var readings = RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? ReadLinuxTemperatures()
            : RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ReadWindowsTemperatures()
            : [];

        // The proprietary NVIDIA driver exposes no hwmon or ACPI sensor; nvidia-smi is the only source.
        var nvidiaTemperature = gpuUsage.TemperatureC ??
            (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ReadWindowsNvidiaTemperature() : null);
        if (nvidiaTemperature is { } celsius && readings.All(reading => reading.Kind != ThermalSensors.Gpu))
            AddTemperature(readings, ThermalSensors.Gpu, "nvidia-smi", "", celsius);

        return readings;
    }

    private static List<TemperatureReading> ReadLinuxTemperatures()
    {
        var readings = new List<TemperatureReading>();
        var hwmonChips = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            // hwmon is where desktop CPUs (k10temp, zenpower, coretemp), GPUs and NVMe drives report.
            if (Directory.Exists("/sys/class/hwmon"))
            {
                foreach (var device in Directory.EnumerateDirectories("/sys/class/hwmon"))
                {
                    var chip = ReadText(Path.Combine(device, "name"));
                    hwmonChips.Add(chip.Replace('-', '_'));
                    foreach (var input in Directory.EnumerateFiles(device, "temp*_input"))
                    {
                        var label = ReadText(input[..^"input".Length] + "label");
                        if (ReadDouble(input) is { } milliCelsius && ThermalSensors.Classify(chip, label) is { } kind)
                            AddTemperature(readings, kind, chip, label, milliCelsius / 1000d);
                    }
                }
            }

            // ARM boards and some laptops expose sensors only as thermal zones. Skip zones hwmon already covered.
            if (Directory.Exists("/sys/class/thermal"))
            {
                foreach (var zone in Directory.EnumerateDirectories("/sys/class/thermal", "thermal_zone*"))
                {
                    var type = ReadText(Path.Combine(zone, "type"));
                    if (hwmonChips.Contains(type.Replace('-', '_')) || ReadDouble(Path.Combine(zone, "temp")) is not { } raw) continue;
                    var isCpu = new[] { "cpu", "core", "pkg", "package", "soc" }
                        .Any(token => type.Contains(token, StringComparison.OrdinalIgnoreCase));
                    AddTemperature(readings, isCpu ? ThermalSensors.Cpu : ThermalSensors.System, "thermal", type, NormalizeTemperature(raw));
                }
            }
        }
        catch { }
        return readings;
    }

    private static List<TemperatureReading> ReadWindowsTemperatures()
    {
        var readings = new List<TemperatureReading>();
        // ACPI thermal zones are readable without administrator rights. Laptops usually expose one that
        // tracks the CPU package; many desktops expose none. Values are tenths of Kelvin / Kelvin.
        var zones = ReadPdhCounterArray(@"\Thermal Zone Information(*)\High Precision Temperature", 0)
            .Select(zone => (zone.Instance, Celsius: zone.Value / 10d - 273.15)).ToList();
        if (zones.Count == 0)
        {
            zones = ReadPdhCounterArray(@"\Thermal Zone Information(*)\Temperature", 0)
                .Select(zone => (zone.Instance, Celsius: zone.Value - 273.15)).ToList();
        }

        foreach (var (instance, celsius) in zones)
        {
            var kind = instance.Contains("CPU", StringComparison.OrdinalIgnoreCase) ? ThermalSensors.Cpu : ThermalSensors.System;
            AddTemperature(readings, kind, "ACPI", instance, celsius);
        }
        return readings;
    }

    private double? ReadWindowsNvidiaTemperature()
    {
        // nvidia-smi takes a noticeable fraction of a second on Windows, so sample it less often than the 2 s refresh.
        if (DateTime.UtcNow - _nvidiaTemperature.SampledAt > TimeSpan.FromSeconds(10))
            _nvidiaTemperature = (ReadNvidiaSmi()?.TemperatureC, DateTime.UtcNow);
        return _nvidiaTemperature.Celsius;
    }

    private static void AddTemperature(List<TemperatureReading> readings, string kind, string chip, string label, double celsius)
    {
        if (ThermalSensors.IsPlausible(celsius))
            readings.Add(new TemperatureReading(kind, chip, label, Math.Round(celsius, 1)));
    }

    private static (string? Name, double? MemoryGb) ReadGpuDetails()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return ReadWindowsGpuDetails();
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || !Directory.Exists("/sys/class/drm")) return (null, null);
        try
        {
            var device = EnumerateLinuxGpuDevices().FirstOrDefault();
            if (device is null) return (null, null);

            var driver = ReadText(Path.Combine(device, "uevent")).Split('\n')
                .FirstOrDefault(line => line.StartsWith("DRIVER=", StringComparison.OrdinalIgnoreCase))?[7..];
            var vendor = ReadText(Path.Combine(device, "vendor"));
            var deviceId = ReadText(Path.Combine(device, "device"));
            var name = ReadPciGpuName(vendor, deviceId) ?? GpuVendorName(vendor) ?? driver;
            var memoryBytes = ReadDouble(Path.Combine(device, "mem_info_vram_total"));
            return (string.IsNullOrWhiteSpace(name) ? null : name, memoryBytes is > 0 ? memoryBytes / Gb : null);
        }
        catch { return (null, null); }
    }

    private static IEnumerable<string> EnumerateLinuxGpuDevices()
    {
        if (!Directory.Exists("/sys/class/drm")) return [];
        return Directory.EnumerateDirectories("/sys/class/drm")
            .Where(path => Path.GetFileName(path) is var name && name.Length > 4 &&
                name.StartsWith("card", StringComparison.Ordinal) && name[4..].All(char.IsAsciiDigit))
            .Select(path => Path.Combine(path, "device"))
            .Where(Directory.Exists)
            // Prefer a discrete adapter when both integrated and discrete graphics are present.
            .OrderByDescending(path => ReadText(Path.Combine(path, "vendor")).Equals("0x10de", StringComparison.OrdinalIgnoreCase) ||
                                       ReadText(Path.Combine(path, "vendor")).Equals("0x1002", StringComparison.OrdinalIgnoreCase));
    }

    private static string? ReadPciGpuName(string vendorId, string deviceId)
    {
        var normalizedVendor = vendorId.Trim().TrimStart('0', 'x').PadLeft(4, '0');
        var normalizedDevice = deviceId.Trim().TrimStart('0', 'x').PadLeft(4, '0');
        foreach (var file in new[] { "/usr/share/hwdata/pci.ids", "/usr/share/misc/pci.ids", "/usr/share/pci.ids" })
        {
            if (!File.Exists(file)) continue;
            try
            {
                var inVendor = false;
                foreach (var line in File.ReadLines(file))
                {
                    if (line.Length == 0 || line[0] == '#') continue;
                    if (!char.IsWhiteSpace(line[0]))
                    {
                        inVendor = line.StartsWith(normalizedVendor + " ", StringComparison.OrdinalIgnoreCase);
                        continue;
                    }
                    if (!inVendor || !line.StartsWith('\t')) continue;
                    var trimmed = line.TrimStart();
                    if (trimmed.StartsWith(normalizedDevice + " ", StringComparison.OrdinalIgnoreCase))
                        return trimmed[(normalizedDevice.Length + 1)..].Trim();
                }
            }
            catch { }
        }
        return null;
    }

    [SupportedOSPlatform("windows")]
    private static (string? Name, double? MemoryGb) ReadWindowsGpuDetails()
    {
        const string displayClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(displayClass);
            if (root is null) return (null, null);
            foreach (var childName in root.GetSubKeyNames().OrderBy(name => name))
            {
                using var child = root.OpenSubKey(childName);
                var name = child?.GetValue("DriverDesc")?.ToString();
                if (string.IsNullOrWhiteSpace(name)) continue;
                var rawMemory = child!.GetValue("AdapterRAM");
                double? memory = rawMemory is null ? null : Convert.ToUInt64(rawMemory, CultureInfo.InvariantCulture) / Gb;
                return (name, memory is > 0 ? memory : null);
            }
        }
        catch { }
        return (null, null);
    }

    private static string ReadText(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path).Trim() : ""; }
        catch { return ""; }
    }

    private static double? ReadDouble(string path) => double.TryParse(ReadText(path), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
    private static double? RatioPercent(string directory, string current, string design)
    {
        var numerator = ReadDouble(Path.Combine(directory, current));
        var denominator = ReadDouble(Path.Combine(directory, design));
        return numerator is > 0 && denominator is > 0 ? Math.Clamp(numerator.Value / denominator.Value * 100, 0, 100) : null;
    }
    private static double NormalizeTemperature(double value) => value > 500 ? value / 1000d : value;
    private static string? GpuVendorName(string vendor) => vendor.ToLowerInvariant() switch
    {
        "0x10de" => "NVIDIA GPU",
        "0x1002" or "0x1022" => "AMD GPU",
        "0x8086" => "Intel GPU",
        _ => null
    };

    private static GpuUsage ReadGpu()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return ReadLinuxGpuUsage();
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return new GpuUsage(null, new Dictionary<int, double>());

        var engines = ReadPdhCounterArray(@"\GPU Engine(*)\Utilization Percentage", 80);
        if (engines.Count == 0) return new GpuUsage(null, new Dictionary<int, double>());

        var result = 0d;
        var byProcess = new Dictionary<int, double>();
        foreach (var (instanceName, value) in engines)
        {
            var utilization = Math.Max(0, value);
            result += utilization;

            var pidStart = instanceName.IndexOf("pid_", StringComparison.OrdinalIgnoreCase);
            if (pidStart < 0) continue;
            pidStart += 4;
            var pidEnd = instanceName.IndexOf('_', pidStart);
            if (pidEnd <= pidStart || !int.TryParse(instanceName.AsSpan(pidStart, pidEnd - pidStart), out var pid)) continue;
            byProcess[pid] = byProcess.GetValueOrDefault(pid) + utilization;
        }
        return new GpuUsage(
            Math.Clamp(result, 0, 100),
            byProcess.ToDictionary(pair => pair.Key, pair => Math.Clamp(pair.Value, 0, 100)));
    }

    /// <summary>
    /// Reads every instance of a wildcard PDH counter. Rate counters need two samples
    /// <paramref name="sampleIntervalMs"/> apart; instantaneous counters (0) are read after one.
    /// </summary>
    private static List<(string Instance, double Value)> ReadPdhCounterArray(string counterPath, int sampleIntervalMs)
    {
        const uint pdhFmtDouble = 0x00000200, pdhMoreData = 0x800007D2;
        var results = new List<(string Instance, double Value)>();
        IntPtr query = IntPtr.Zero;
        try
        {
            if (PdhOpenQueryW(null, IntPtr.Zero, out query) != 0) return results;
            if (PdhAddEnglishCounterW(query, counterPath, IntPtr.Zero, out var counter) != 0) return results;
            PdhCollectQueryData(query);
            if (sampleIntervalMs > 0)
            {
                Thread.Sleep(sampleIntervalMs);
                PdhCollectQueryData(query);
            }

            uint bytes = 0, count = 0;
            if (PdhGetFormattedCounterArrayW(counter, pdhFmtDouble, ref bytes, ref count, IntPtr.Zero) != pdhMoreData || bytes == 0)
                return results;
            var buffer = Marshal.AllocHGlobal((int)bytes);
            try
            {
                if (PdhGetFormattedCounterArrayW(counter, pdhFmtDouble, ref bytes, ref count, buffer) != 0) return results;
                var entrySize = Marshal.SizeOf<PdhFormattedCounterItem>();
                for (var i = 0; i < count; i++)
                {
                    var item = Marshal.PtrToStructure<PdhFormattedCounterItem>(IntPtr.Add(buffer, i * entrySize));
                    if (item.Value.Status != 0) continue;
                    var name = item.Name == IntPtr.Zero ? "" : Marshal.PtrToStringUni(item.Name) ?? "";
                    results.Add((name, item.Value.DoubleValue));
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        catch { }
        finally { if (query != IntPtr.Zero) PdhCloseQuery(query); }
        return results;
    }

    private static GpuUsage ReadLinuxGpuUsage()
    {
        try
        {
            var nvidia = ReadNvidiaSmi();
            if (nvidia is not null) return nvidia;

            // DRM drivers that export gpu_busy_percent provide the same instantaneous metric
            // shown by their native tools. Not every driver (notably some NVIDIA setups) does.
            var values = EnumerateLinuxGpuDevices()
                .Select(path => ReadDouble(Path.Combine(path, "gpu_busy_percent")) ?? ReadDouble(Path.Combine(path, "busy_percent")))
                .Where(value => value.HasValue)
                .Select(value => value!.Value)
                .ToList();
            return values.Count == 0
                ? new GpuUsage(null, new Dictionary<int, double>())
                : new GpuUsage(Math.Clamp(values.Sum(), 0, 100), new Dictionary<int, double>());
        }
        catch { return new GpuUsage(null, new Dictionary<int, double>()); }
    }

    private static string? FindNvidiaSmi()
    {
        var candidates = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new[] { Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe"), @"C:\Program Files\NVIDIA Corporation\NVSMI\nvidia-smi.exe" }
            : ["/usr/bin/nvidia-smi", "/usr/sbin/nvidia-smi", "/usr/local/bin/nvidia-smi"];
        return candidates.FirstOrDefault(File.Exists);
    }

    private static GpuUsage? ReadNvidiaSmi()
    {
        if (NvidiaSmiPath is not { } command) return null;
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = command,
                Arguments = "--query-gpu=name,utilization.gpu,memory.total,temperature.gpu --format=csv,noheader,nounits",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(750) || process.ExitCode != 0) return null;

            var gpus = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line => line.Split(',', StringSplitOptions.TrimEntries))
                .Where(parts => parts.Length >= 3 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out _) &&
                                                    double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                .Select(parts => (
                    Name: parts[0],
                    Usage: double.Parse(parts[1], CultureInfo.InvariantCulture),
                    MemoryMiB: double.Parse(parts[2], CultureInfo.InvariantCulture),
                    TemperatureC: parts.Length >= 4 && double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var celsius) ? celsius : (double?)null))
                .ToList();
            if (gpus.Count == 0) return null;

            var primary = gpus[0];
            return new GpuUsage(Math.Clamp(primary.Usage, 0, 100), new Dictionary<int, double>(), primary.Name, primary.MemoryMiB / 1024d, primary.TemperatureC);
        }
        catch { return null; }
    }

    private sealed record GpuUsage(
        double? TotalPercent,
        IReadOnlyDictionary<int, double> ProcessPercent,
        string? Name = null,
        double? MemoryGb = null,
        double? TemperatureC = null);

    [StructLayout(LayoutKind.Sequential)] private struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [StructLayout(LayoutKind.Sequential)] private struct SystemPowerStatus
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public uint BatteryLifeTime, BatteryFullLifeTime;
    }
    [StructLayout(LayoutKind.Sequential)] private struct PdhCounterValue { public uint Status; public double DoubleValue; }
    [StructLayout(LayoutKind.Sequential)] private struct PdhFormattedCounterItem { public IntPtr Name; public PdhCounterValue Value; }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetSystemPowerStatus(out SystemPowerStatus systemPowerStatus);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);
    [StructLayout(LayoutKind.Sequential)] private struct FileTime { public uint Low, High; public ulong ToUInt64() => ((ulong)High << 32) | Low; }
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, ref uint itemCount, IntPtr items);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(IntPtr query);
}
