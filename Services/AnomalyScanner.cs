using System.Diagnostics.Eventing.Reader;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.Versioning;
using SystemMind.Models;

namespace SystemMind.Services;

/// <summary>Collects high-signal Windows Event Log or Linux system-journal reliability events.</summary>
public sealed class AnomalyScanner
{
    private readonly AnomalyDatabase _database;
    public AnomalyScanner(AnomalyDatabase database) => _database = database;

    public async Task<int> ScanAsync(TimeSpan window, IProgress<string>? progress = null)
    {
        if (OperatingSystem.IsLinux())
        {
            progress?.Report("Linux system journal");
            var records = await Task.Run(() => ReadLinuxJournal(DateTime.UtcNow - window));
            var linuxAdded = 0;
            foreach (var record in records)
                if (_database.TryInsert(record)) linuxAdded++;
            return linuxAdded;
        }

        if (!OperatingSystem.IsWindows()) { progress?.Report("Event-log scanning is unavailable on this operating system."); return 0; }

        var since = DateTime.UtcNow - window;
        var scans = new (string Label, string Log, string Query, string Category, AnomalySeverity Severity, string Title)[]
        {
            ("unexpected shutdowns", "System", "*[System[(Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=41) or (Provider[@Name='EventLog'] and EventID=6008)]]", "UnexpectedShutdown", AnomalySeverity.Critical, "Unexpected shutdown"),
            ("blue screens", "System", "*[System[EventID=1001]]", "BSOD", AnomalySeverity.Critical, "Possible blue-screen crash"),
            ("disk errors", "System", "*[System[Provider[@Name='disk'] and (EventID=7 or EventID=11 or EventID=51 or EventID=153)]]", "DiskError", AnomalySeverity.Warning, "Storage or disk error"),
            ("GPU timeouts", "System", "*[System[(Provider[@Name='Display'] or Provider[@Name='nvlddmkm'] or Provider[@Name='dxgkrnl']) and (EventID=4101 or EventID=4097)]]", "GpuTimeout", AnomalySeverity.Warning, "Graphics driver timeout/recovery"),
            ("application crashes", "Application", "*[System[Provider[@Name='Application Error'] and EventID=1000]]", "Crash", AnomalySeverity.Warning, "Application crash"),
            ("application hangs", "Application", "*[System[Provider[@Name='Application Hang'] and EventID=1002]]", "AppHang", AnomalySeverity.Warning, "Application stopped responding"),
            ("memory exhaustion", "System", "*[System[Provider[@Name='Microsoft-Windows-Resource-Exhaustion-Detector'] and EventID=2004]]", "MemoryExhaustion", AnomalySeverity.Critical, "System ran low on memory"),
            ("hardware errors", "System", "*[System[Provider[@Name='Microsoft-Windows-WHEA-Logger']]]", "HardwareError", AnomalySeverity.Critical, "Hardware error reported")
        };

        var added = 0;
        foreach (var scan in scans)
        {
            progress?.Report(scan.Label);
            foreach (var record in await Task.Run(() => Read(scan.Log, scan.Query, since, scan.Category, scan.Severity, scan.Title)))
                if (_database.TryInsert(record)) added++;
        }
        return added;
    }

    [SupportedOSPlatform("windows")]
    private static List<AnomalyRecord> Read(string log, string query, DateTime since, string category, AnomalySeverity severity, string title)
    {
        var results = new List<AnomalyRecord>();
        try
        {
            using var reader = new EventLogReader(new EventLogQuery(log, PathType.LogName, query));
            while (reader.ReadEvent() is EventLogRecord item)
            {
                using (item)
                {
                    var timestamp = (item.TimeCreated ?? DateTime.UtcNow).ToUniversalTime();
                    if (timestamp < since) continue;
                    var raw = Description(item);
                    if (category == "BSOD" && !raw.Contains("bugcheck", StringComparison.OrdinalIgnoreCase) && !raw.Contains("blue screen", StringComparison.OrdinalIgnoreCase)) continue;
                    results.Add(new AnomalyRecord
                    {
                        TimestampUtc = timestamp, Severity = severity, Category = category,
                        Title = title, Source = $"{log}/{item.ProviderName} {item.Id}", EventId = item.Id,
                        Details = $"Provider: {item.ProviderName}\nEvent ID: {item.Id}\n{raw}", RawMessage = raw,
                        Fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{item.ProviderName}-{item.Id}-{item.RecordId}-{timestamp:O}")))[..32]
                    });
                }
            }
        }
        catch (EventLogException) { }
        catch (UnauthorizedAccessException) { }
        return results;
    }

    [SupportedOSPlatform("windows")]
    private static string Description(EventLogRecord item)
    {
        try { var text = item.FormatDescription() ?? ""; return text.Length > 1500 ? text[..1500] : text; }
        catch { return "The event message is unavailable on this system."; }
    }

    private static List<AnomalyRecord> ReadLinuxJournal(DateTime since)
    {
        var results = new List<AnomalyRecord>();
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "journalctl",
                Arguments = $"--no-pager --output=short-iso --since=\"{since.ToLocalTime():yyyy-MM-dd HH:mm:ss}\" -p warning",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process is null) return results;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            if (process.ExitCode != 0) return results;

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var record = ClassifyLinuxJournalLine(line);
                if (record is not null) results.Add(record);
            }
        }
        catch { }
        return results;
    }

    private static AnomalyRecord? ClassifyLinuxJournalLine(string line)
    {
        var firstSpace = line.IndexOf(' ');
        var timestamp = firstSpace > 0 && DateTimeOffset.TryParse(line[..firstSpace], out var parsed) ? parsed.UtcDateTime : DateTime.UtcNow;
        var lower = line.ToLowerInvariant();
        var (category, severity, title) = lower switch
        {
            _ when ContainsAny(lower, "out of memory", "oom-kill", "killed process") => ("MemoryExhaustion", AnomalySeverity.Critical, "System ran low on memory"),
            _ when ContainsAny(lower, "nvrm:", "gpu hang", "gpu reset", "amdgpu", "i915.*error", "drm:.*error") => ("GpuTimeout", AnomalySeverity.Warning, "Graphics driver problem"),
            _ when ContainsAny(lower, "i/o error", "blk_update_request", "buffer i/o", "ext4-fs error", "btrfs error", "xfs.*error") => ("DiskError", AnomalySeverity.Warning, "Storage or filesystem error"),
            _ when ContainsAny(lower, "kernel panic", "bug:", "oops:", "soft lockup", "watchdog") => ("HardwareError", AnomalySeverity.Critical, "Kernel or hardware fault"),
            _ when ContainsAny(lower, "segfault", "core dumped", "coredump") => ("Crash", AnomalySeverity.Warning, "Application crash"),
            _ when ContainsAny(lower, "journal corrupted", "uncleanly shut down") => ("UnexpectedShutdown", AnomalySeverity.Warning, "Possible unclean shutdown"),
            _ => ("", AnomalySeverity.Info, "")
        };
        if (string.IsNullOrEmpty(category)) return null;
        return new AnomalyRecord
        {
            TimestampUtc = timestamp, Severity = severity, Category = category, Title = title,
            Source = "Linux system journal", Details = line, RawMessage = line,
            Fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(line)))[..32]
        };
    }

    private static bool ContainsAny(string text, params string[] patterns) => patterns.Any(pattern =>
        pattern.Contains('*')
            ? text.Contains(pattern.Replace("*", ""), StringComparison.OrdinalIgnoreCase)
            : text.Contains(pattern, StringComparison.OrdinalIgnoreCase));
}
