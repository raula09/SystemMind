using System.Diagnostics;
using System.Text.Json;
using DiagnoX.Models;

namespace DiagnoX.Services;

/// <summary>
/// Host-side diagnostics exposed to the chat model through OpenAI-compatible function tools.
/// Output is deliberately bounded so logs cannot exhaust the chat context.
/// </summary>
public sealed class DiagnosticToolService : IDisposable
{
    private const int MaxOutputCharacters = 24_000;
    private readonly AnomalyDatabase _database = new();
    private readonly AnomalyScanner _scanner;

    public DiagnosticToolService() => _scanner = new AnomalyScanner(_database);

    public IReadOnlyList<object> OpenAiTools { get; } =
    [
        Function("scan_anomalies", "Run DiagnoX's AnomalyScanner against Windows Event Viewer or the Linux system journal. Use this first for crashes, disk faults, GPU resets, OOMs, unexpected shutdowns, and hardware errors.", new { hours = new { type = "integer", description = "Look-back window in hours, from 1 to 720. Defaults to 24." } }),
        Function("get_scanned_anomalies", "Read anomalies already found by DiagnoX's AnomalyScanner without starting a new scan.", new { hours = new { type = "integer", description = "Look-back window in hours, from 1 to 720. Defaults to 24." } }),
        Function("read_event_viewer", "Read recent Windows Event Viewer records. Available only on Windows. Use standard log names such as System, Application, or Setup.", new { log_name = new { type = "string", description = "Windows event log name; defaults to System." }, hours = new { type = "integer", description = "Look-back window in hours, from 1 to 720. Defaults to 24." }, max_events = new { type = "integer", description = "Maximum records to return, 1 to 100. Defaults to 40." } }),
        Function("read_journalctl", "Read Linux system-journal records with journalctl. Available only on Linux. Prefer priority warning or err for diagnosis.", new { hours = new { type = "integer", description = "Look-back window in hours, from 1 to 720. Defaults to 24." }, priority = new { type = "string", description = "journalctl priority: emerg, alert, crit, err, warning, notice, info, or debug. Defaults to warning." }, max_lines = new { type = "integer", description = "Maximum lines to return, 1 to 200. Defaults to 80." } }),
        Function("run_cmd", "Run a host-system diagnostic command and return stdout/stderr. Use it only when the dedicated scanner, Event Viewer, or journal tool cannot answer the hardware question. Commands run with the current user's permissions; do not use destructive, state-changing, or network-download commands unless the user explicitly asks for that action.", new { command = new { type = "string", description = "A shell command to run on the host." }, timeout_seconds = new { type = "integer", description = "Timeout from 1 to 30 seconds; defaults to 15." } })
    ];

    public async Task<string> InvokeAsync(string name, string argumentsJson)
    {
        using var arguments = ParseArguments(argumentsJson);
        var root = arguments.RootElement;
        return name switch
        {
            "scan_anomalies" => await ScanAsync(GetBoundedInt(root, "hours", 24, 1, 720)),
            "get_scanned_anomalies" => ReadScanned(GetBoundedInt(root, "hours", 24, 1, 720)),
            "read_event_viewer" => await ReadEventViewerAsync(GetString(root, "log_name", "System"), GetBoundedInt(root, "hours", 24, 1, 720), GetBoundedInt(root, "max_events", 40, 1, 100)),
            "read_journalctl" => await ReadJournalAsync(GetBoundedInt(root, "hours", 24, 1, 720), GetString(root, "priority", "warning"), GetBoundedInt(root, "max_lines", 80, 1, 200)),
            "run_cmd" => await RunCommandAsync(GetRequiredString(root, "command"), GetBoundedInt(root, "timeout_seconds", 15, 1, 30)),
            _ => $"Unknown diagnostic tool: {name}."
        };
    }

    private async Task<string> ScanAsync(int hours)
    {
        var added = await _scanner.ScanAsync(TimeSpan.FromHours(hours));
        return $"AnomalyScanner completed for the last {hours} hour(s); {added} new record(s) saved.\n" + ReadScanned(hours);
    }

    private string ReadScanned(int hours)
    {
        var records = _database.GetRecent(TimeSpan.FromHours(hours));
        if (records.Count == 0) return $"No saved anomalies found in the last {hours} hour(s).";
        return $"{records.Count} saved anomaly record(s), newest first:\n" + string.Join("\n\n", records.Take(100).Select(FormatRecord));
    }

    private static async Task<string> ReadEventViewerAsync(string logName, int hours, int maxEvents)
    {
        if (!OperatingSystem.IsWindows()) return "Event Viewer is only available on Windows.";
        if (!System.Text.RegularExpressions.Regex.IsMatch(logName, "^[A-Za-z0-9 /._-]{1,128}$")) return "Invalid Windows event log name.";
        var since = DateTime.Now.AddHours(-hours).ToString("o");
        var script = $"Get-WinEvent -FilterHashtable @{{LogName='{logName.Replace("'", "''")}'; StartTime=[datetime]'{since}'}} -MaxEvents {maxEvents} -ErrorAction Stop | Select-Object TimeCreated,Id,LevelDisplayName,ProviderName,Message | Format-List | Out-String -Width 240";
        return await RunProcessAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", script], 15);
    }

    private static async Task<string> ReadJournalAsync(int hours, string priority, int maxLines)
    {
        if (!OperatingSystem.IsLinux()) return "journalctl is only available on Linux.";
        var allowedPriorities = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "emerg", "alert", "crit", "err", "warning", "notice", "info", "debug" };
        if (!allowedPriorities.Contains(priority)) return "Invalid journalctl priority.";
        return await RunProcessAsync("journalctl", ["--no-pager", "--output=short-iso", "--since", $"{hours} hours ago", "-p", priority, "-n", maxLines.ToString()], 15);
    }

    private static Task<string> RunCommandAsync(string command, int timeoutSeconds)
    {
        if (string.IsNullOrWhiteSpace(command)) return Task.FromResult("The command is empty.");
        return OperatingSystem.IsWindows()
            ? RunProcessAsync("cmd.exe", ["/d", "/s", "/c", command], timeoutSeconds)
            : RunProcessAsync("/bin/bash", ["-lc", command], timeoutSeconds);
    }

    private static async Task<string> RunProcessAsync(string fileName, IReadOnlyList<string> arguments, int timeoutSeconds)
    {
        try
        {
            var startInfo = new ProcessStartInfo(fileName) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
            using var process = new Process { StartInfo = startInfo };
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            var completion = process.WaitForExitAsync();
            if (await Task.WhenAny(completion, Task.Delay(TimeSpan.FromSeconds(timeoutSeconds))) != completion)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return $"Command timed out after {timeoutSeconds} seconds.";
            }
            await completion;
            return TrimOutput($"Exit code: {process.ExitCode}\nSTDOUT:\n{await stdout}\nSTDERR:\n{await stderr}");
        }
        catch (Exception ex) { return $"Unable to run command: {ex.Message}"; }
    }

    private static object Function(string name, string description, object properties) => new { type = "function", function = new { name, description, parameters = new { type = "object", properties, additionalProperties = false } } };
    private static JsonDocument ParseArguments(string json) { try { return JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json); } catch (JsonException) { return JsonDocument.Parse("{}"); } }
    private static int GetBoundedInt(JsonElement root, string name, int fallback, int min, int max) => root.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? Math.Clamp(result, min, max) : fallback;
    private static string GetString(JsonElement root, string name, string fallback) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : fallback;
    private static string GetRequiredString(JsonElement root, string name) => GetString(root, name, "");
    private static string TrimOutput(string value) => value.Length <= MaxOutputCharacters ? value : value[..MaxOutputCharacters] + "\n[output truncated]";
    private static string FormatRecord(AnomalyRecord r) => $"{r.TimestampUtc.ToLocalTime():O} | {r.Severity} | {r.CategoryLabel} | {r.Source}\n{r.Details}";
    public void Dispose() => _database.Dispose();
}
