using System.Diagnostics;
using DiagnoX.Models;

namespace DiagnoX.Services;

public sealed class CleanupScannerService
{
    private readonly FileOperationsService _fileOps = new();

    public async Task<List<CleanupItem>> ScanForCleanupTargetsAsync(FileSystemNode? scannedRoot = null)
    {
        return await Task.Run(() =>
        {
            var items = new List<CleanupItem>();
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            // 1. Well-known cache targets
            var knownCacheTargets = new (string RelPath, string Name, string Category, string Risk, string Desc)[]
            {
                (".local/share/Trash", "Trash Can", "Trash", "Safe", "Deleted files waiting in the trash bin"),
                (".cache/thumbnails", "Thumbnail Cache", "Cache", "Safe", "Image and video previews, will regenerate as needed"),
                (".cache/pip", "Python Pip Cache", "Developer", "Safe", "Cached pip wheels and downloaded python packages"),
                (".npm/_cacache", "NPM Package Cache", "Developer", "Safe", "Cached npm packages and tarballs"),
                (".cache/yarn", "Yarn Cache", "Developer", "Safe", "Cached yarn dependencies"),
                (".cache/go-build", "Go Build Cache", "Developer", "Safe", "Cached Go compile outputs"),
                (".cargo/registry/cache", "Rust Cargo Cache", "Developer", "Safe", "Cached cargo crates"),
                (".cache/google-chrome", "Chrome Browser Cache", "Browser", "Review", "Temporary web pages, images, and script cache"),
                (".cache/mozilla", "Firefox Browser Cache", "Browser", "Review", "Browser disk cache and web resources"),
                (".cache/BraveSoftware", "Brave Browser Cache", "Browser", "Review", "Browser cached resources"),
                (".cache/vscode-cpptools", "VS Code C++ Cache", "Developer", "Safe", "IntelliSense symbol databases"),
                (".cache/JetBrains", "JetBrains IDE Cache", "Developer", "Review", "IDE caches and indexing data"),
                (".var/app", "Flatpak App Data/Cache", "System", "Review", "Flatpak sandboxed application data"),
            };

            foreach (var target in knownCacheTargets)
            {
                var full = Path.Combine(home, target.RelPath);
                if (Directory.Exists(full))
                {
                    var size = CalculateDirectorySizeFast(full, 10000);
                    if (size > 1024 * 1024) // Only show if > 1 MB
                    {
                        items.Add(new CleanupItem
                        {
                            Title = target.Name,
                            Category = target.Category,
                            Path = full,
                            SizeBytes = size,
                            RiskLevel = target.Risk,
                            Description = target.Desc,
                            IsSelected = target.Risk == "Safe"
                        });
                    }
                }
            }

            // 2. Temp directories
            var tempDirs = new[] { "/tmp", "/var/tmp" };
            foreach (var temp in tempDirs)
            {
                if (Directory.Exists(temp))
                {
                    try
                    {
                        // Check user-owned files in /tmp
                        long userTempSize = 0;
                        var di = new DirectoryInfo(temp);
                        foreach (var f in di.EnumerateFiles("*", new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false }))
                        {
                            try { userTempSize += f.Length; } catch { }
                        }

                        if (userTempSize > 5 * 1024 * 1024)
                        {
                            items.Add(new CleanupItem
                            {
                                Title = $"Temporary Files ({temp})",
                                Category = "System",
                                Path = temp,
                                SizeBytes = userTempSize,
                                RiskLevel = "Review",
                                Description = "Temporary system and user scratch files",
                                IsSelected = false
                            });
                        }
                    }
                    catch { }
                }
            }

            // 3. Downloads folder check
            var downloadsPath = Path.Combine(home, "Downloads");
            if (Directory.Exists(downloadsPath))
            {
                try
                {
                    var dInfo = new DirectoryInfo(downloadsPath);
                    var oldFiles = dInfo.EnumerateFiles("*", new EnumerationOptions { IgnoreInaccessible = true })
                        .Where(f => (DateTime.Now - f.LastWriteTime).TotalDays > 30)
                        .ToList();

                    var oldSize = oldFiles.Sum(f => f.Length);
                    if (oldSize > 50 * 1024 * 1024) // > 50 MB
                    {
                        items.Add(new CleanupItem
                        {
                            Title = "Downloads Older Than 30 Days",
                            Category = "Downloads",
                            Path = downloadsPath,
                            SizeBytes = oldSize,
                            RiskLevel = "Review",
                            Description = $"{oldFiles.Count} downloaded files not modified in the past 30 days",
                            IsSelected = false
                        });
                    }
                }
                catch { }
            }

            // 4. If a scan was already performed, search for node_modules and build artifacts!
            if (scannedRoot != null)
            {
                FindDeveloperArtifacts(scannedRoot, items);
            }

            return items.OrderByDescending(i => i.SizeBytes).ToList();
        });
    }

    private static void FindDeveloperArtifacts(FileSystemNode node, List<CleanupItem> results)
    {
        if (results.Count >= 25) return; // avoid overwhelming list

        if (node.IsDirectory)
        {
            var lowerName = node.Name.ToLowerInvariant();
            if (lowerName is "node_modules" or "target" or "bin" or "obj")
            {
                if (node.Size > 20 * 1024 * 1024 && !results.Any(r => r.Path == node.FullPath))
                {
                    results.Add(new CleanupItem
                    {
                        Title = $"{node.Name} ({Path.GetFileName(Path.GetDirectoryName(node.FullPath))})",
                        Category = "Developer",
                        Path = node.FullPath,
                        SizeBytes = node.Size,
                        RiskLevel = "Review",
                        Description = $"Dependencies or build artifacts in {node.FullPath}",
                        IsSelected = false
                    });
                }
                return; // don't recurse inside node_modules
            }

            foreach (var child in node.Children)
            {
                FindDeveloperArtifacts(child, results);
            }
        }
    }

    private static long CalculateDirectorySizeFast(string path, int maxFiles = 20000)
    {
        long total = 0;
        int count = 0;
        var stack = new Stack<string>();
        stack.Push(path);

        while (stack.Count > 0 && count < maxFiles)
        {
            var cur = stack.Pop();
            try
            {
                foreach (var f in Directory.EnumerateFiles(cur))
                {
                    try
                    {
                        total += new FileInfo(f).Length;
                        count++;
                        if (count >= maxFiles) break;
                    }
                    catch { }
                }

                foreach (var d in Directory.EnumerateDirectories(cur))
                {
                    stack.Push(d);
                }
            }
            catch { }
        }

        return total;
    }

    public async Task<(long CleanedBytes, int CleanedCount, List<string> Errors)> ExecuteCleanupAsync(
        IEnumerable<CleanupItem> selectedItems,
        IProgress<string>? progress = null)
    {
        return await Task.Run(() =>
        {
            long cleanedBytes = 0;
            int cleanedCount = 0;
            var errors = new List<string>();

            foreach (var item in selectedItems.Where(i => i.IsSelected))
            {
                progress?.Report($"Cleaning {item.Title}...");

                if (_fileOps.IsProtected(item.Path))
                {
                    errors.Add($"Skipped protected path: {item.Path}");
                    continue;
                }

                try
                {
                    if (item.Title.Contains("Trash Can", StringComparison.OrdinalIgnoreCase))
                    {
                        // Clean trash files
                        var trashFiles = Path.Combine(item.Path, "files");
                        var trashInfo = Path.Combine(item.Path, "info");
                        if (Directory.Exists(trashFiles))
                        {
                            foreach (var f in Directory.EnumerateFiles(trashFiles)) { try { File.Delete(f); } catch { } }
                            foreach (var d in Directory.EnumerateDirectories(trashFiles)) { try { Directory.Delete(d, true); } catch { } }
                        }
                        if (Directory.Exists(trashInfo))
                        {
                            foreach (var f in Directory.EnumerateFiles(trashInfo)) { try { File.Delete(f); } catch { } }
                        }
                        cleanedBytes += item.SizeBytes;
                        cleanedCount++;
                    }
                    else if (item.Title.Contains("Downloads Older Than 30 Days", StringComparison.OrdinalIgnoreCase))
                    {
                        // Clean individual old files in downloads to trash
                        var dInfo = new DirectoryInfo(item.Path);
                        foreach (var f in dInfo.EnumerateFiles("*", new EnumerationOptions { IgnoreInaccessible = true }))
                        {
                            if ((DateTime.Now - f.LastWriteTime).TotalDays > 30)
                            {
                                try
                                {
                                    var len = f.Length;
                                    _fileOps.MoveToTrash(f.FullName);
                                    cleanedBytes += len;
                                }
                                catch { }
                            }
                        }
                        cleanedCount++;
                    }
                    else if (Directory.Exists(item.Path))
                    {
                        // Clear contents of cache directory rather than deleting the root folder itself
                        var d = new DirectoryInfo(item.Path);
                        foreach (var fi in d.EnumerateFiles()) { try { fi.Delete(); } catch { } }
                        foreach (var sub in d.EnumerateDirectories()) { try { sub.Delete(true); } catch { } }

                        cleanedBytes += item.SizeBytes;
                        cleanedCount++;
                    }
                    else if (File.Exists(item.Path))
                    {
                        File.Delete(item.Path);
                        cleanedBytes += item.SizeBytes;
                        cleanedCount++;
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"{item.Title}: {ex.Message}");
                }
            }

            return (cleanedBytes, cleanedCount, errors);
        });
    }
}
