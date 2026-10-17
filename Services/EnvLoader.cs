namespace SystemMind.Services;

public static class EnvLoader
{
    public static void Load(string? directory = null)
    {
        try
        {
            var dir = directory ?? Directory.GetCurrentDirectory();
            var searchDirs = new[]
            {
                dir,
                AppContext.BaseDirectory,
                Path.Combine(dir, ".."),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..")
            };

            foreach (var d in searchDirs)
            {
                var envPath = Path.Combine(d, ".env");
                if (File.Exists(envPath))
                {
                    LoadFromFile(envPath);
                    return;
                }
            }
        }
        catch
        {
            // Ignore environment file load errors
        }
    }

    public static void LoadFromFile(string path)
    {
        if (!File.Exists(path)) return;

        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;

            var parts = line.Split('=', 2);
            if (parts.Length == 2)
            {
                var key = parts[0].Trim();
                var val = parts[1].Trim().Trim('"', '\'');
                if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(val))
                {
                    Environment.SetEnvironmentVariable(key, val);
                }
            }
        }
    }
}
