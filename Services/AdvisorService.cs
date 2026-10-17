using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SystemMind.Models;

namespace SystemMind.Services;

public sealed class AdvisorService
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private static readonly string[] OpenRouterFallbackModels =
    [
        "dots-studio/dots-3-note-preview:free",
        "liquid/lfm-2.5-2.6b:free",
        "google/gemma-4-26b-a4b-it:free",
        "cohere/north-mini-code:free",
        "nvidia/nemotron-3.5-lightning:free"
    ];

    public async Task<string> AskAsync(
        string question,
        SystemSnapshot snapshot,
        FileSystemNode? scannedRoot = null,
        IReadOnlyList<ExtensionStat>? extensions = null,
        bool isScanning = false,
        string currentScanPath = "",
        long scannedFiles = 0,
        long scannedBytes = 0)
    {
        EnvLoader.Load();

        var openAiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        var openRouterKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        var key = !string.IsNullOrWhiteSpace(openAiKey) ? openAiKey : openRouterKey;

        // If no cloud key, use our rich local expert engine
        if (string.IsNullOrWhiteSpace(key))
        {
            return LocalExpertAnswer(question, snapshot, scannedRoot, extensions, isScanning, currentScanPath, scannedFiles, scannedBytes);
        }

        var isCustomEndpoint = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AI_API_ENDPOINT"));
        var endpoint = isCustomEndpoint
            ? Environment.GetEnvironmentVariable("AI_API_ENDPOINT")!
            : !string.IsNullOrWhiteSpace(openAiKey)
                ? "https://api.openai.com/v1/chat/completions"
                : "https://openrouter.ai/api/v1/chat/completions";

        var configuredModel = Environment.GetEnvironmentVariable("AI_MODEL") ??
            (!string.IsNullOrWhiteSpace(openAiKey) ? "gpt-4o-mini" : "dots-studio/dots-3-note-preview:free");

        var modelsToTry = new List<string> { configuredModel };
        if (endpoint.Contains("openrouter.ai"))
        {
            foreach (var fb in OpenRouterFallbackModels)
            {
                if (!modelsToTry.Contains(fb, StringComparer.OrdinalIgnoreCase))
                {
                    modelsToTry.Add(fb);
                }
            }
        }

        var systemPrompt = BuildSystemPrompt(snapshot, scannedRoot, extensions, isScanning, currentScanPath, scannedFiles, scannedBytes);

        // Try cloud models with seamless fallback
        foreach (var model in modelsToTry)
        {
            try
            {
                var responseText = await CallChatApiAsync(endpoint, key, model, systemPrompt, question);
                if (!string.IsNullOrWhiteSpace(responseText))
                {
                    return responseText;
                }
            }
            catch
            {
                // Silently try next fallback model
            }
        }

        // If all cloud calls failed or rate-limited, smoothly fall back to our expert engine
        return LocalExpertAnswer(question, snapshot, scannedRoot, extensions, isScanning, currentScanPath, scannedFiles, scannedBytes);
    }

    private async Task<string> CallChatApiAsync(string endpoint, string apiKey, string model, string systemPrompt, string userPrompt)
    {
        var payload = new
        {
            model = model,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            },
            max_tokens = 900,
            temperature = 0.4
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        if (endpoint.Contains("openrouter.ai"))
        {
            request.Headers.Add("HTTP-Referer", "https://github.com/SystemMind");
            request.Headers.Add("X-Title", "SystemMind Storage Advisor");
        }

        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        var response = await _http.SendAsync(request);
        var rawJson = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            try
            {
                using var errDoc = JsonDocument.Parse(rawJson);
                if (errDoc.RootElement.TryGetProperty("error", out var errElem))
                {
                    if (errElem.TryGetProperty("message", out var msg))
                    {
                        throw new HttpRequestException($"API Error: {msg.GetString()}");
                    }
                }
            }
            catch (JsonException) { }

            throw new HttpRequestException($"API request failed with status {(int)response.StatusCode}");
        }

        using var doc = JsonDocument.Parse(rawJson);
        var root = doc.RootElement;
        if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
        {
            var choice = choices[0];
            if (choice.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var content))
            {
                return content.GetString() ?? "";
            }
        }

        return "";
    }

    private static string BuildSystemPrompt(
        SystemSnapshot s,
        FileSystemNode? scanRoot,
        IReadOnlyList<ExtensionStat>? extensions,
        bool isScanning = false,
        string currentScanPath = "",
        long scannedFiles = 0,
        long scannedBytes = 0)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are SystemMind AI, an intelligent storage advisor and system copilot like WinDirStat equipped with AI.");
        sb.AppendLine("Give concise, direct, helpful, and safe advice with markdown formatting. Never recommend deleting critical system files.");
        sb.AppendLine();
        sb.AppendLine("CURRENT SYSTEM TELEMETRY:");
        sb.AppendLine($"- RAM: {s.MemoryUsedGb:F1}/{s.MemoryTotalGb:F1} GB ({s.MemoryPercent:F0}% in use)");
        sb.AppendLine($"- Disks: {s.DiskUsedGb:F0}/{s.DiskTotalGb:F0} GB ({s.DiskPercent:F0}% in use)");
        sb.AppendLine($"- CPU Load: {s.CpuPercent:F0}% across {Environment.ProcessorCount} cores");
        sb.AppendLine($"- Top Memory Processes: {string.Join(", ", s.Processes.Take(5).Select(p => $"{p.Name} ({p.MemoryMb:F0} MB)"))}");

        if (scanRoot != null && scanRoot.Size > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"CURRENT WINDIRSTAT SCAN REPORT for '{scanRoot.FullPath}':");
            sb.AppendLine($"- Total Scanned Size: {scanRoot.FormattedSize}");
            sb.AppendLine($"- Total Files: {scanRoot.FileCount:N0}, Subfolders: {scanRoot.DirectoryCount:N0}");

            var topDirs = scanRoot.Children.Where(c => c.IsDirectory).OrderByDescending(c => c.Size).Take(6);
            sb.AppendLine("- Largest Folders: " + string.Join("; ", topDirs.Select(d => $"'{d.Name}' ({d.FormattedSize}, {d.FormattedPercent})")));

            var topFiles = new List<FileSystemNode>();
            CollectLargestFiles(scanRoot, topFiles, 5);
            sb.AppendLine("- Largest Files: " + string.Join("; ", topFiles.Select(f => $"'{f.Name}' ({f.FormattedSize})")));

            if (extensions != null && extensions.Count > 0)
            {
                var topExts = extensions.Take(5).Select(e => $"{e.Extension} ({e.FormattedSize}, {e.FormattedPercent})");
                sb.AppendLine("- Top File Types: " + string.Join(", ", topExts));
            }
        }
        else if (isScanning)
        {
            sb.AppendLine();
            sb.AppendLine($"SCAN IN PROGRESS: Actively scanning '{currentScanPath}' ({scannedFiles:N0} files, {FileSystemNode.FormatBytes(scannedBytes)} discovered so far). Mention that live treemap results are populating.");
        }

        return sb.ToString();
    }

    public static string LocalExpertAnswer(
        string question,
        SystemSnapshot s,
        FileSystemNode? scanRoot = null,
        IReadOnlyList<ExtensionStat>? extensions = null,
        bool isScanning = false,
        string currentScanPath = "",
        long scannedFiles = 0,
        long scannedBytes = 0)
    {
        var q = question.ToLowerInvariant();

        // 1. Safe cleanup and junk removal questions
        if (q.Contains("clean") || q.Contains("junk") || q.Contains("trash") || q.Contains("free up") || q.Contains("to free"))
        {
            var sb = new StringBuilder();
            sb.AppendLine("### 🧹 Safe Disk Cleanup Recommendations");
            sb.AppendLine("Here are the safest ways to recover disk space on your system:");
            sb.AppendLine();
            sb.AppendLine("1. **Empty User Trash** (`~/.local/share/Trash`): Safely deletes discarded files.");
            sb.AppendLine("2. **Thumbnail Cache** (`~/.cache/thumbnails`): Safe to clear; will regenerate automatically.");
            sb.AppendLine("3. **Package & Pip Caches** (`~/.cache/pip`, `~/.npm/_cacache`): Safe to clear downloaded install caches.");
            sb.AppendLine("4. **Old Downloads** (`~/Downloads`): Review archives (.iso, .tar.gz, .zip) older than 30 days.");

            if (scanRoot != null)
            {
                var candidateJunk = new List<FileSystemNode>();
                FindJunkCandidates(scanRoot, candidateJunk);
                if (candidateJunk.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("**Detected Candidates in Current Scan:**");
                    foreach (var junk in candidateJunk.Take(4))
                    {
                        sb.AppendLine($"- `{junk.FullPath}` (~{junk.FormattedSize})");
                    }
                }
            }

            sb.AppendLine();
            sb.AppendLine("👉 *Switch to the **Cleanup assistant** tab to scan and clean these items with one click.*");
            return sb.ToString();
        }

        // 2. Storage / WinDirStat scan breakdown queries
        if (q.Contains("space") || q.Contains("taking") || q.Contains("eating") || q.Contains("storage") || q.Contains("breakdown") || q.Contains("windirstat") || q.Contains("scan"))
        {
            if (scanRoot != null && scanRoot.Size > 0)
            {
                var sb = new StringBuilder();
                sb.AppendLine($"### 📊 Scan Breakdown for `{scanRoot.FullPath}`");
                sb.AppendLine($"- **Total Size**: **{scanRoot.FormattedSize}** across **{scanRoot.FileCount:N0} files**");
                sb.AppendLine();
                sb.AppendLine("**Top Folders Consuming Space:**");

                var topFolders = scanRoot.Children.Where(c => c.IsDirectory).OrderByDescending(c => c.Size).Take(5).ToList();
                foreach (var folder in topFolders)
                {
                    sb.AppendLine($"- **`{folder.Name}/`**: {folder.FormattedSize} ({folder.FormattedPercent} of scan)");
                }

                if (extensions != null && extensions.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("**Top File Categories:**");
                    foreach (var ext in extensions.Take(4))
                    {
                        sb.AppendLine($"- **`{ext.Extension}`** ({ext.Category}): {ext.FormattedSize} ({ext.FormattedPercent})");
                    }
                }

                sb.AppendLine();
                sb.AppendLine("💡 *Click any block in the treemap or open the Storage Explorer tree to inspect individual files.*");
                return sb.ToString();
            }

            if (isScanning)
            {
                return $"### ⏳ Storage Scan in Progress\n\n" +
                       $"SystemMind is currently actively scanning `{currentScanPath}` (**{scannedFiles:N0} files**, **{FileSystemNode.FormatBytes(scannedBytes)}** discovered so far).\n\n" +
                       "The squarified treemap and folder hierarchy are updating live in the **Storage explorer** tab. In just a few seconds as the scan finishes, ask me again for the complete file breakdown!\n\n" +
                       $"- Current storage: **{s.DiskUsedGb:F0} / {s.DiskTotalGb:F0} GB** ({s.DiskPercent:F0}% used, **{s.DiskTotalGb - s.DiskUsedGb:F0} GB free**).";
            }

            return $"### 📊 Storage Overview & Scan Started\n\n" +
                   $"Your storage is currently **{s.DiskPercent:F0}% full** ({s.DiskUsedGb:F0} GB used out of {s.DiskTotalGb:F0} GB total, leaving **{s.DiskTotalGb - s.DiskUsedGb:F0} GB free**).\n\n" +
                   "I have initiated a scan on your Home directory. Switch to the **Storage explorer** tab to watch the treemap and file tree build in real-time!";
        }

        // 3. Questions about specific folders (node_modules, .cache, etc.)
        if (q.Contains("node_modules"))
        {
            return "### 📦 About `node_modules`\n\n" +
                   "`node_modules` folders contain installed JavaScript/Node.js dependencies. They often consume gigabytes across multiple projects.\n\n" +
                   "- **Is it safe to delete?** Yes! As long as you have `package.json`, you can safely delete `node_modules` anytime. You can reinstall them whenever needed using `npm install` or `pnpm install`.\n" +
                   "- **Tip**: Run `npx npkill` or use SystemMind's **Cleanup assistant** tab to clean all unused `node_modules`.";
        }

        if (q.Contains(".cache") || q.Contains("cache"))
        {
            return "### 🗄️ About `~/.cache`\n\n" +
                   "`~/.cache` is the standard Linux directory where desktop applications store non-essential cached data (thumbnails, browser assets, IDE indexes, pip wheels).\n\n" +
                   "- **Can you delete it?** You should not delete the entire directory while apps are running, but deleting contents of specific subfolders like `~/.cache/thumbnails`, `~/.cache/pip`, and browser caches is completely safe.";
        }

        // 4. Memory / RAM queries
        if (q.Contains("ram") || q.Contains("memory"))
        {
            var top = s.Processes.FirstOrDefault();
            var status = s.MemoryPercent > 80 ? "⚠️ **High Memory Usage**" : "✅ **Healthy Memory Level**";

            return $"{status}\n\n" +
                   $"- **RAM in Use**: **{s.MemoryUsedGb:F1} GB** / {s.MemoryTotalGb:F1} GB ({s.MemoryPercent:F0}%)\n" +
                   $"- **Free Headroom**: **{Math.Max(0, s.MemoryTotalGb - s.MemoryUsedGb):F1} GB**\n" +
                   $"- **Top Process**: `{top?.Name ?? "N/A"}` ({top?.MemoryMb:F0} MB)\n\n" +
                   "**Recommendations:**\n" +
                   "- Close background browser tabs or idle electron apps.\n" +
                   "- View the **Performance** tab to monitor active processes or end memory-heavy tasks.";
        }

        // 5. Game or software compatibility
        if (q.Contains("game") || q.Contains("cyberpunk") || q.Contains("run") || q.Contains("compatible") || q.Contains("specs"))
        {
            var freeStorage = s.DiskTotalGb - s.DiskUsedGb;
            return "### 🎮 Hardware & Game Readiness\n\n" +
                   $"- **CPU Cores**: {Environment.ProcessorCount} logical threads\n" +
                   $"- **Total RAM**: **{s.MemoryTotalGb:F1} GB** (Available: {s.MemoryTotalGb - s.MemoryUsedGb:F1} GB)\n" +
                   $"- **Available Storage**: **{freeStorage:F0} GB free**\n" +
                   $"- **Current Load**: CPU {s.CpuPercent:F0}%, RAM {s.MemoryPercent:F0}%\n\n" +
                   "Most modern titles require at least 16 GB RAM and 70–120 GB of free SSD space. " +
                   (freeStorage < 50 ? "⚠️ *Storage is low; consider freeing space before installing large games.*" : "✅ *Storage headroom looks sufficient for new installations.*");
        }

        // 6. Default general assistant answer
        return $"### 🤖 SystemMind Status\n\n" +
               $"- **Memory**: {s.MemoryUsedGb:F1} / {s.MemoryTotalGb:F1} GB ({s.MemoryPercent:F0}%)\n" +
               $"- **Storage**: {s.DiskUsedGb:F0} / {s.DiskTotalGb:F0} GB ({s.DiskPercent:F0}%)\n" +
               $"- **CPU**: {s.CpuPercent:F0}% load across {Environment.ProcessorCount} cores\n" +
               (scanRoot != null ? $"- **Active Scan**: `{scanRoot.FullPath}` ({scanRoot.FormattedSize}, {scanRoot.FileCount:N0} files)\n" : "") +
               "\nYou can ask me:\n" +
               "- *\"What is taking up space?\"*\n" +
               "- *\"How can I safely free up 10 GB?\"*\n" +
               "- *\"What is node_modules or ~/.cache?\"*\n" +
               "- *\"Why is RAM high?\"*";
    }

    private static void CollectLargestFiles(FileSystemNode node, List<FileSystemNode> results, int maxCount)
    {
        if (!node.IsDirectory)
        {
            results.Add(node);
            results.Sort((a, b) => b.Size.CompareTo(a.Size));
            if (results.Count > maxCount) results.RemoveAt(results.Count - 1);
            return;
        }

        foreach (var child in node.Children)
        {
            CollectLargestFiles(child, results, maxCount);
        }
    }

    private static void FindJunkCandidates(FileSystemNode node, List<FileSystemNode> results)
    {
        if (results.Count >= 6) return;

        var lower = node.Name.ToLowerInvariant();
        if (lower is "node_modules" or "thumbnails" or ".cache" or "trash" or "tmp" or "target")
        {
            if (node.Size > 10 * 1024 * 1024)
            {
                results.Add(node);
                return;
            }
        }

        if (node.IsDirectory)
        {
            foreach (var child in node.Children)
            {
                FindJunkCandidates(child, results);
            }
        }
    }
}
