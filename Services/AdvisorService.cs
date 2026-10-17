using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SystemMind.Models;

namespace SystemMind.Services;

public sealed class AdvisorService
{
    private readonly HttpClient _http = new();
    public async Task<string> AskAsync(string question, SystemSnapshot snapshot)
    {
        var openAiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        var openRouterKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        var key = openAiKey ?? openRouterKey;
        if (string.IsNullOrWhiteSpace(key)) return LocalAnswer(question, snapshot);
        var useOpenRouter = string.IsNullOrWhiteSpace(openAiKey) && !string.IsNullOrWhiteSpace(openRouterKey);
        var url = useOpenRouter ? "https://api.openrouter.ai/v1/responses" : "https://api.openai.com/v1/responses";
        var model = useOpenRouter
            ? Environment.GetEnvironmentVariable("OPENROUTER_MODEL") ?? "gpt-4o-mini"
            : Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "gpt-4.1-mini";
        try
        {
            var context = $"RAM {snapshot.MemoryUsedGb:F1}/{snapshot.MemoryTotalGb:F1} GB ({snapshot.MemoryPercent:F0}%). Disk {snapshot.DiskUsedGb:F0}/{snapshot.DiskTotalGb:F0} GB ({snapshot.DiskPercent:F0}%). CPU {snapshot.CpuPercent:F0}%. Top processes: {string.Join(", ", snapshot.Processes.Take(4).Select(p => $"{p.Name} {p.MemoryMb:F0}MB"))}.";
            var body = new { model, input = new[] { new { role = "system", content = "You are SystemMind, a concise desktop system advisor. Give safe, actionable advice. Never claim to have scanned files you have not been given. Mention uncertainty for game compatibility." }, new { role = "user", content = $"Live snapshot: {context}\nQuestion: {question}" } } };
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            var response = await _http.SendAsync(request);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return json.RootElement.GetProperty("output")[0].GetProperty("content")[0].GetProperty("text").GetString() ?? "I couldn't generate a response.";
        }
        catch (Exception ex) { return $"The AI service could not be reached ({ex.Message}). {LocalAnswer(question, snapshot)}"; }
    }

    private static string LocalAnswer(string question, SystemSnapshot s)
    {
        var q = question.ToLowerInvariant();
        if (q.Contains("ram") || q.Contains("memory"))
            return s.MemoryPercent > 80 ? $"Memory use is high at {s.MemoryPercent:F0}%. Start with {s.Processes.FirstOrDefault()?.Name ?? "the largest process"}; close unused browser tabs and background apps. Avoid force-closing system processes." : $"Memory is at {s.MemoryPercent:F0}%, which is within a comfortable range. The largest current user is {s.Processes.FirstOrDefault()?.Name ?? "not available"}.";
        if (q.Contains("storage") || q.Contains("clean") || q.Contains("free"))
            return s.DiskPercent > 80 ? $"Storage is {s.DiskPercent:F0}% full. Review Downloads, old installers, duplicate media, and unused games first. Empty Trash only after checking it, and keep at least 15–20% free for updates and performance." : $"You have roughly {s.DiskTotalGb - s.DiskUsedGb:F0} GB free. I’d inspect Downloads and unused applications before deleting anything.";
        if (q.Contains("game") || q.Contains("run") || q.Contains("compatible"))
            return $"I can assess a specific game if you paste its minimum/recommended CPU, GPU, RAM, and storage requirements. Right now you have {s.MemoryTotalGb:F0} GB RAM and {s.DiskTotalGb - s.DiskUsedGb:F0} GB free; GPU details need a platform-specific hardware probe.";
        return $"Your live snapshot: CPU {s.CpuPercent:F0}%, memory {s.MemoryPercent:F0}%, and storage {s.DiskPercent:F0}% used. Ask me about RAM, cleanup, or a game’s requirements.";
    }
}
