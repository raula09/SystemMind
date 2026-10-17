using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SystemMind.Models;

namespace SystemMind.Services;

public sealed class AdvisorService
{
    // Gemini 3.8 may spend more than 20 seconds reasoning before it streams a response.
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(90) };

    private static readonly string[] OpenRouterFallbackModels =
    [
        "openai/gpt-4o-mini",
        "google/gemini-3.8-flash",
        "openai/gpt-4.1",
        "nvidia/nemotron-3.5-super-vl:free"
    ];

    private static readonly string[] GeminiFallbackModels =
    [
        "gemini-3.8-flash"
    ];

    private static bool IsKnownBrokenModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return true;
        var value = model.Trim();
        return value.Contains("nemotron", StringComparison.OrdinalIgnoreCase)
            || value.Contains("nvidia/", StringComparison.OrdinalIgnoreCase)
            || value.Contains("anthropic/claude-3.5-sonnet", StringComparison.OrdinalIgnoreCase);
    }

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
        var geminiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY")
            ?? Environment.GetEnvironmentVariable("GOOGLE_API_KEY")
            ?? Environment.GetEnvironmentVariable("apiKey");
        var key = GetApiKey();

        // If no cloud key, use our rich local expert engine
        if (string.IsNullOrWhiteSpace(key))
        {
            return "### ⚠️ AI not configured\n\nAdd a valid Gemini/OpenRouter key in Settings or `.env` to enable live responses.";
        }

        var isCustomEndpoint = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AI_API_ENDPOINT"));
        var provider = GetProviderName(
            isCustomEndpoint ? Environment.GetEnvironmentVariable("AI_API_ENDPOINT")! : "",
            openAiKey,
            openRouterKey,
            geminiKey);

        var endpoint = isCustomEndpoint
            ? Environment.GetEnvironmentVariable("AI_API_ENDPOINT")!
            : provider == "gemini"
                ? "https://generativelanguage.googleapis.com/v1beta/models"
                : provider == "openai"
                    ? "https://api.openai.com/v1/chat/completions"
                    : "https://openrouter.ai/api/v1/chat/completions";

        var configuredModel = Environment.GetEnvironmentVariable("AI_MODEL") ??
            (provider == "gemini" ? "gemini-3.8-flash" : provider == "openai" ? "gpt-4o-mini" : "openai/gpt-4o-mini");

        if (string.IsNullOrWhiteSpace(configuredModel) || IsKnownBrokenModel(configuredModel))
        {
            configuredModel = provider == "gemini" ? "gemini-3.8-flash" : provider == "openai" ? "gpt-4o-mini" : "openai/gpt-4o-mini";
        }

        var modelsToTry = new List<string> { configuredModel };
        if (provider == "gemini")
        {
            foreach (var fb in GeminiFallbackModels)
            {
                if (!modelsToTry.Contains(fb, StringComparer.OrdinalIgnoreCase))
                {
                    modelsToTry.Add(fb);
                }
            }
        }
        else if (endpoint.Contains("openrouter.ai"))
        {
            foreach (var fb in OpenRouterFallbackModels)
            {
                if (!modelsToTry.Contains(fb, StringComparer.OrdinalIgnoreCase))
                {
                    modelsToTry.Add(fb);
                }
            }
        }

        modelsToTry = modelsToTry
            .Where(m => !IsKnownBrokenModel(m))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var systemPrompt = BuildSystemPrompt(snapshot, scannedRoot, extensions, isScanning, currentScanPath, scannedFiles, scannedBytes);

        // Try cloud models with seamless fallback
        foreach (var model in modelsToTry)
        {
            try
            {
                var responseText = await CallChatApiAsync(endpoint, key, model, systemPrompt, question, provider);
                if (!string.IsNullOrWhiteSpace(responseText))
                {
                    return FormatFriendlyResponse(ShortenToKeyPoints(responseText));
                }
            }
            catch
            {
                // Silently try next fallback model
            }
        }

        // If all cloud calls failed or rate-limited, fail honestly instead of serving canned text.
        return "### ⚠️ AI provider unavailable\n\nThe live model is failing right now. Try again in a moment or switch to another provider/model.";
    }

    private async Task<string> CallChatApiAsync(string endpoint, string apiKey, string model, string systemPrompt, string userPrompt, string provider)
    {
        if (provider == "gemini")
        {
            var geminiEndpoint = $"{endpoint.TrimEnd('/')}/{model}:generateContent?key={apiKey}";

            var geminiPayload = new
            {
                system_instruction = new { parts = new[] { new { text = systemPrompt } } },
                contents = new[]
                {
                    new { role = "user", parts = new[] { new { text = userPrompt } } }
                },
                generationConfig = new
                {
                    temperature = 0.25,
                    topP = 0.9,
                    maxOutputTokens = 1200
                }
            };

            using var geminiRequest = new HttpRequestMessage(HttpMethod.Post, geminiEndpoint);
            geminiRequest.Content = new StringContent(JsonSerializer.Serialize(geminiPayload), Encoding.UTF8, "application/json");

            var geminiResponse = await _http.SendAsync(geminiRequest);
            var geminiRawJson = await geminiResponse.Content.ReadAsStringAsync();

            if (!geminiResponse.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"Gemini API request failed with status {(int)geminiResponse.StatusCode}: {geminiRawJson}");
            }

            using var geminiDoc = JsonDocument.Parse(geminiRawJson);
            var geminiRoot = geminiDoc.RootElement;
            if (geminiRoot.TryGetProperty("candidates", out var candidates) &&
                candidates.ValueKind == JsonValueKind.Array &&
                candidates.GetArrayLength() > 0)
            {
                var candidate = candidates[0];
                if (candidate.TryGetProperty("content", out var content) &&
                    content.TryGetProperty("parts", out var parts) &&
                    parts.ValueKind == JsonValueKind.Array && parts.GetArrayLength() > 0)
                {
                    foreach (var part in parts.EnumerateArray())
                    {
                        if (part.TryGetProperty("text", out var text))
                        {
                            return text.GetString() ?? "";
                        }
                    }
                }
            }

            return string.Empty;
        }

        var payload = new
        {
            model = model,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            },
            max_tokens = 1200,
            temperature = 0.25,
            top_p = 0.9,
            frequency_penalty = 0.2,
            presence_penalty = 0.1
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
        sb.AppendLine("You are SystemMind AI, an intelligent storage advisor and system copilot for a Linux desktop.");
        sb.AppendLine("Give concise, direct, practical, and safe advice with markdown headings and bullet points.");
        sb.AppendLine("Base your guidance on the live system telemetry and scan data provided. Never invent file scans or system state that is not present.");
        sb.AppendLine("Prefer precise, actionable guidance over generic chat responses. Be careful not to recommend deleting critical system files, boot files, or user data without a warning.");
        sb.AppendLine("For game compatibility, mention uncertainty and ask for specs when needed.");
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

            var repoIndex = GetRepositoryIndex(scanRoot);
            if (repoIndex.Count > 0)
            {
                sb.AppendLine("- Visible Repository Roots: " + string.Join("; ", repoIndex.Take(6).Select(r => $"'{r.Name}' @ {r.FullPath}")));
            }

            var gameIndex = GetGameOrBigFolderSummary(scanRoot);
            if (gameIndex.Count > 0)
            {
                sb.AppendLine("- Large Game / App Folders: " + string.Join("; ", gameIndex.Take(6).Select(g => $"'{g.Name}' ({g.FormattedSize})")));
            }

            if (extensions != null && extensions.Count > 0)
            {
                var topExts = extensions.Take(5).Select(e => $"{e.Extension} ({e.FormattedSize}, {e.FormattedPercent})");
                sb.AppendLine("- Top File Types: " + string.Join(", ", topExts));
            }
        }
        else if (isScanning)
        {
            sb.AppendLine();
            sb.AppendLine($"SCAN IN PROGRESS: Actively scanning '{currentScanPath}' ({scannedFiles:N0} files, {FileSystemNode.FormatBytes(scannedBytes)} discovered so far). Show progress in a calm, factual way and mention live treemap updates.");
        }

        sb.AppendLine();
        sb.AppendLine("RESPONDER STYLE:");
        sb.AppendLine("- Start with a brief verdict or summary.");
        sb.AppendLine("- Give 2–5 highly actionable recommendations.");
        sb.AppendLine("- Call out risk areas like storage pressure, RAM pressure, or cleanup of caches.");
        sb.AppendLine("- If the user asks for a game name, repo, or large folder, answer with the most likely visible folder from the current scan instead of saying you cannot tell.");
        sb.AppendLine("- If there is no strong evidence, say so clearly and avoid speculation.");
        sb.AppendLine("- Keep it helpful, technical, and confidence-based, not fluffy.");

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

        if ((q.Contains("game") && q.Contains("name")) || (q.Contains("what") && q.Contains("game") && (q.Contains("called") || q.Contains("name") || q.Contains("folder"))))
        {
            var likelyGames = GetGameOrBigFolderSummary(scanRoot);
            if (likelyGames.Count > 0)
            {
                var top = likelyGames.First();
                return $"### 🎮 Likely game folder\n\nThe largest game-related folder in the visible scan appears to be **`{top.Name}`**.\n\n- Path: `{top.FullPath}`\n- Size: **{top.FormattedSize}**\n- Share of scan: **{top.FormattedPercent}**\n\nThis is the strongest candidate in the current filesystem map; if you want, I can also break down what is inside it or tell you which files are consuming the most space inside that folder.";
            }

            return "### 🎮 Game folder check\n\nI do not see a clearly named or large game folder in the current scan, so I cannot confidently identify a specific game name from this filesystem snapshot alone.";
        }

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
        if (q.Contains("space") || q.Contains("taking") || q.Contains("eating") || q.Contains("storage") || q.Contains("breakdown") || q.Contains("windirstat") || q.Contains("scan") || q.Contains("game") || q.Contains("holds") || q.Contains("largest"))
        {
            if (scanRoot != null && scanRoot.Size > 0)
            {
                var sb = new StringBuilder();
                sb.AppendLine($"### 📊 Scan Breakdown for `{scanRoot.FullPath}`");
                sb.AppendLine($"- **Total Size**: **{scanRoot.FormattedSize}** across **{scanRoot.FileCount:N0} files**");
                sb.AppendLine();

                var topFolders = scanRoot.Children.Where(c => c.IsDirectory).OrderByDescending(c => c.Size).Take(5).ToList();
                var repoSummary = GetRepositorySummary(scanRoot).Take(5).ToList();
                var gameSummary = GetGameOrBigFolderSummary(scanRoot).Take(5).ToList();

                if (gameSummary.Count > 0)
                {
                    sb.AppendLine("**Largest Game / Large App Areas:**");
                    foreach (var item in gameSummary)
                    {
                        sb.AppendLine($"- **`{item.Name}`**: {item.FormattedSize} ({item.FormattedPercent} of scan)");
                    }
                    sb.AppendLine();
                }

                sb.AppendLine("**Top Folders Consuming Space:**");
                foreach (var folder in topFolders)
                {
                    sb.AppendLine($"- **`{folder.Name}/`**: {folder.FormattedSize} ({folder.FormattedPercent} of scan)");
                }

                if (repoSummary.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("**Visible Repositories / Project Roots:**");
                    foreach (var repo in repoSummary)
                    {
                        sb.AppendLine($"- **`{repo.Name}`**: {repo.FormattedSize} at `{repo.FullPath}`");
                    }
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
                sb.AppendLine("💡 *Click any block in the treemap or open the Storage Explorer tree to inspect individual files or repositories.*");
                return sb.ToString();
            }

            if (isScanning)
            {
                return $"### ⏳ Storage Scan in Progress\n\n" +
                       $"SystemMind is currently actively scanning `{currentScanPath}` (**{scannedFiles:N0} files**, **{FileSystemNode.FormatBytes(scannedBytes)}** discovered so far).\n\n" +
                       "The treemap and repository tree are updating live. Ask again once the scan is complete for the full storage breakdown.\n\n" +
                       $"- Current storage: **{s.DiskUsedGb:F0} / {s.DiskTotalGb:F0} GB** ({s.DiskPercent:F0}% used, **{s.DiskTotalGb - s.DiskUsedGb:F0} GB free**).";
            }

            return $"### 📊 Storage Overview & Scan Started\n\n" +
                   $"Your storage is currently **{s.DiskPercent:F0}% full** ({s.DiskUsedGb:F0} GB used out of {s.DiskTotalGb:F0} GB total, leaving **{s.DiskTotalGb - s.DiskUsedGb:F0} GB free**).\n\n" +
                   "I have initiated a scan of the visible filesystem and repositories. Switch to the **Storage explorer** tab to watch the treemap and project tree build in real time!";
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

    private static void CollectRepositoryCandidates(FileSystemNode node, List<FileSystemNode> results, int max)
    {
        if (results.Count >= max) return;

        if (node.IsDirectory)
        {
            var lower = node.FullPath.ToLowerInvariant();
            var hasRepoMarker = lower.Contains("/.git") || node.Name.Equals(".git", StringComparison.OrdinalIgnoreCase)
                || node.Name.Contains(".git", StringComparison.OrdinalIgnoreCase)
                || File.Exists(Path.Combine(node.FullPath, "package.json"))
                || File.Exists(Path.Combine(node.FullPath, "pyproject.toml"))
                || File.Exists(Path.Combine(node.FullPath, "Cargo.toml"))
                || File.Exists(Path.Combine(node.FullPath, "go.mod"))
                || File.Exists(Path.Combine(node.FullPath, "pom.xml"))
                || File.Exists(Path.Combine(node.FullPath, "Makefile"))
                || File.Exists(Path.Combine(node.FullPath, ".sln"))
                || File.Exists(Path.Combine(node.FullPath, ".csproj"));

            if (hasRepoMarker && node.Size > 0)
            {
                results.Add(node);
            }

            foreach (var child in node.Children)
            {
                CollectRepositoryCandidates(child, results, max);
            }
        }
    }

    private static List<FileSystemNode> GetRepositorySummary(FileSystemNode root)
    {
        var repos = new List<FileSystemNode>();
        CollectRepositoryCandidates(root, repos, 25);
        return repos.OrderByDescending(r => r.Size).ToList();
    }

    private static List<FileSystemNode> GetRepositoryIndex(FileSystemNode root)
    {
        var repos = new List<FileSystemNode>();
        CollectRepositoryCandidates(root, repos, 25);
        return repos.OrderByDescending(r => r.Size).ToList();
    }

    private static List<FileSystemNode> GetGameOrBigFolderSummary(FileSystemNode? root)
    {
        if (root == null) return [];

        var matches = new List<FileSystemNode>();
        CollectGameLikeFolders(root, matches);
        return matches.OrderByDescending(f => f.Size).Take(5).ToList();
    }

    private static void CollectGameLikeFolders(FileSystemNode node, List<FileSystemNode> results)
    {
        if (!node.IsDirectory) return;

        var lower = node.FullPath.ToLowerInvariant();
        var isGameLike = lower.Contains("game") || lower.Contains("steam") || lower.Contains("epic") || lower.Contains("gog") || lower.Contains("mod") || lower.Contains("library");

        if (isGameLike && node.Size > 50 * 1024 * 1024)
        {
            results.Add(node);
        }

        foreach (var child in node.Children)
        {
            CollectGameLikeFolders(child, results);
        }
    }

    public static string FormatFriendlyResponse(string raw)
    {
        var text = raw ?? "";
        if (string.IsNullOrWhiteSpace(text))
        {
            return "### ✅ SystemMind\n\nI checked your system and everything looks healthy right now.";
        }

        text = text.Replace("### ", "\n### ");
        text = text.Replace("- **", "\n- **");
        text = text.Replace("\n\n\n", "\n\n");
        return text.Trim();
    }

    public static string BuildCleanupRecommendationText(IReadOnlyList<CleanupItem>? items)
    {
        if (items == null || items.Count == 0)
        {
            return "### 🧹 Safe cleanup\n\nI did not find any high-confidence junk items to remove right now.";
        }

        var builder = new StringBuilder();
        builder.AppendLine("### 🧹 Safe cleanup suggestions");
        foreach (var item in items.Take(4))
        {
            builder.AppendLine($"- **{item.Title}** — {item.FormattedSize} at `{item.Path}`");
        }
        builder.AppendLine();
        builder.AppendLine("These are low-risk cleanup targets and can be removed after a quick review.");
        return builder.ToString();
    }

    private static string? GetApiKey()
    {
        return Environment.GetEnvironmentVariable("GEMINI_API_KEY")
            ?? Environment.GetEnvironmentVariable("GOOGLE_API_KEY")
            ?? Environment.GetEnvironmentVariable("apiKey")
            ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY")
            ?? Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
    }

    private static string GetProviderName(string endpoint, string? openAiKey, string? openRouterKey, string? geminiKey)
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AI_API_ENDPOINT")))
            return "custom";

        if (!string.IsNullOrWhiteSpace(geminiKey) || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GEMINI_API_KEY")) || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GOOGLE_API_KEY")) || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("apiKey")) || endpoint.Contains("generativelanguage.googleapis.com", StringComparison.OrdinalIgnoreCase))
            return "gemini";

        if (!string.IsNullOrWhiteSpace(openAiKey) || endpoint.Contains("api.openai.com", StringComparison.OrdinalIgnoreCase))
            return "openai";

        if (!string.IsNullOrWhiteSpace(openRouterKey) || endpoint.Contains("openrouter.ai", StringComparison.OrdinalIgnoreCase))
            return "openrouter";

        return "local";
    }

    private static string ShortenToKeyPoints(string raw)
    {
        var text = (raw ?? "").Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return "### ✅ Status\n\nEverything looks healthy and stable right now.";
        }

        var lines = text
            .Replace("\r\n", "\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();

        if (lines.Count == 0) return "### ✅ Status\n\nEverything looks healthy and stable right now.";

        var bullets = lines.Where(l => l.StartsWith("- ", StringComparison.Ordinal) || l.StartsWith("* ", StringComparison.Ordinal)).ToList();
        var heading = lines.FirstOrDefault(l => l.StartsWith("#", StringComparison.Ordinal) || l.Contains("**") || l.Contains("Status") || l.Contains("Verdict"));

        if (heading is null && bullets.Count > 0)
        {
            heading = "### ✅ Key Takeaways";
        }

        var selected = new List<string>();
        if (!string.IsNullOrWhiteSpace(heading)) selected.Add(heading);
        selected.AddRange(bullets.Take(3));

        if (selected.Count == 1)
        {
            var compact = text;
            compact = compact.Replace("### ", "");
            compact = compact.Replace("**", "");
            compact = compact.Replace("\n\n", " ");
            compact = Regex.Replace(compact, "\\s+", " ").Trim();
            return $"### ✅ Key takeaway\n\n{(compact.Length > 220 ? compact[..220].TrimEnd() + "..." : compact)}";
        }

        return string.Join("\n", selected).Trim();
    }
}
