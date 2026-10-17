using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security;
using SystemMind.Models;

namespace SystemMind.Services;

public record ScanProgress(
    long ScannedFiles,
    long ScannedDirectories,
    long TotalBytes,
    string CurrentPath,
    double ElapsedSeconds);

public sealed class DirectoryScanner
{
    private static readonly HashSet<string> SkipDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "/proc", "/sys", "/dev", "/run", "/var/run", "/snap", "/sys/kernel"
    };

    public async Task<(FileSystemNode Root, List<ExtensionStat> Extensions)> ScanAsync(
        string rootPath,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        return await Task.Run(() =>
        {
            var rootDir = new DirectoryInfo(rootPath);
            if (!rootDir.Exists)
            {
                throw new DirectoryNotFoundException($"Directory not found: {rootPath}");
            }

            var isRoot = rootDir.FullName is "/" or "\\";
            var rootNode = new FileSystemNode
            {
                Name = isRoot ? "Root (/)" : (string.IsNullOrEmpty(rootDir.Name) ? rootDir.FullName : rootDir.Name),
                FullPath = rootDir.FullName,
                IsDirectory = true,
                LastModified = rootDir.LastWriteTime
            };

            var extCounts = new ConcurrentDictionary<string, (long Bytes, int Count)>(StringComparer.OrdinalIgnoreCase);
            long totalFiles = 0;
            long totalDirs = 0;
            long totalBytes = 0;
            var stopwatch = Stopwatch.StartNew();
            var lastReportTime = 0L;

            void ReportProgress(string currentPath)
            {
                var now = stopwatch.ElapsedMilliseconds;
                if (now - lastReportTime >= 80) // ~12 fps progress update to prevent UI lag
                {
                    lastReportTime = now;
                    progress?.Report(new ScanProgress(
                        Interlocked.Read(ref totalFiles),
                        Interlocked.Read(ref totalDirs),
                        Interlocked.Read(ref totalBytes),
                        currentPath,
                        stopwatch.Elapsed.TotalSeconds));
                }
            }

            // Recursive scan from root
            ScanDirectoryInternal(rootDir, rootNode, extCounts, ref totalFiles, ref totalDirs, ref totalBytes, ReportProgress, cancellationToken);

            // Compute percentages and sort children recursively
            FinalizeTree(rootNode, rootNode.Size);

            // Generate ExtensionStats
            var extensionStats = extCounts.Select(kvp =>
            {
                var ext = kvp.Key;
                var (cat, color) = FileSystemNode.Categorize(ext);
                var pct = rootNode.Size > 0 ? (double)kvp.Value.Bytes / rootNode.Size * 100.0 : 0.0;
                return new ExtensionStat
                {
                    Extension = string.IsNullOrEmpty(ext) ? "(no extension)" : ext,
                    Category = cat,
                    ColorHex = color,
                    TotalBytes = kvp.Value.Bytes,
                    FileCount = kvp.Value.Count,
                    Percentage = pct
                };
            }).OrderByDescending(e => e.TotalBytes).ToList();

            // Final progress report
            progress?.Report(new ScanProgress(
                totalFiles,
                totalDirs,
                totalBytes,
                "Scan complete",
                stopwatch.Elapsed.TotalSeconds));

            return (rootNode, extensionStats);
        }, cancellationToken);
    }

    private static bool ShouldSkip(DirectoryInfo dir)
    {
        var path = dir.FullName.TrimEnd('/', '\\');
        if (string.IsNullOrEmpty(path)) return false; // Never skip root "/"

        // Avoid cyclic directory symlinks
        if (dir.Attributes.HasFlag(FileAttributes.ReparsePoint) || dir.LinkTarget != null)
            return true;

        if (SkipDirectories.Contains(path))
            return true;

        if (path.StartsWith("/proc", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/sys", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/dev", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/run", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/var/run", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static void ScanDirectoryInternal(
        DirectoryInfo dir,
        FileSystemNode dirNode,
        ConcurrentDictionary<string, (long Bytes, int Count)> extCounts,
        ref long totalFiles,
        ref long totalDirs,
        ref long totalBytes,
        Action<string> reportProgress,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (ShouldSkip(dir))
        {
            return;
        }

        reportProgress(dir.FullName);
        Interlocked.Increment(ref totalDirs);

        long currentDirSize = 0;
        int currentFileCount = 0;
        int currentDirCount = 0;

        var fileChildren = new List<FileSystemNode>();

        // 1. Enumerate files in current directory
        try
        {
            foreach (var fi in dir.EnumerateFiles())
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    // Skip dangling symlinks
                    if (fi.Attributes.HasFlag(FileAttributes.ReparsePoint) || fi.LinkTarget != null)
                    {
                        continue;
                    }

                    var len = fi.Length;
                    var ext = fi.Extension.ToLowerInvariant();
                    var (cat, color) = FileSystemNode.Categorize(ext);

                    var fileNode = new FileSystemNode
                    {
                        Name = fi.Name,
                        FullPath = fi.FullName,
                        IsDirectory = false,
                        Size = len,
                        Extension = ext,
                        Category = cat,
                        ColorHex = color,
                        LastModified = fi.LastWriteTime,
                        Parent = dirNode
                    };

                    fileChildren.Add(fileNode);
                    currentDirSize += len;
                    currentFileCount++;

                    Interlocked.Increment(ref totalFiles);
                    Interlocked.Add(ref totalBytes, len);

                    extCounts.AddOrUpdate(ext,
                        _ => (len, 1),
                        (_, existing) => (existing.Bytes + len, existing.Count + 1));
                }
                catch (UnauthorizedAccessException) { }
                catch (FileNotFoundException) { }
                catch (IOException) { }
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (SecurityException) { }
        catch (IOException) { }

        // Keep direct file children optimized for large filesystems (e.g. 1.5M files)
        const int maxFilesKeptPerFolder = 200;
        if (fileChildren.Count > maxFilesKeptPerFolder)
        {
            var sortedFiles = fileChildren.OrderByDescending(f => f.Size).ToList();
            var keptFiles = sortedFiles.Take(maxFilesKeptPerFolder).ToList();
            var remainingSize = sortedFiles.Skip(maxFilesKeptPerFolder).Sum(f => f.Size);
            var remainingCount = sortedFiles.Count - maxFilesKeptPerFolder;

            foreach (var f in keptFiles)
            {
                dirNode.Children.Add(f);
            }

            if (remainingCount > 0)
            {
                dirNode.Children.Add(new FileSystemNode
                {
                    Name = $"... {remainingCount:N0} other smaller files",
                    FullPath = Path.Combine(dir.FullName, "(other smaller files)"),
                    IsDirectory = false,
                    Size = remainingSize,
                    Category = "Other",
                    ColorHex = "#556575",
                    Parent = dirNode
                });
            }
        }
        else
        {
            foreach (var f in fileChildren)
            {
                dirNode.Children.Add(f);
            }
        }

        // 2. Enumerate subdirectories
        try
        {
            foreach (var subDir in dir.EnumerateDirectories())
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (ShouldSkip(subDir))
                    {
                        continue;
                    }

                    var subNode = new FileSystemNode
                    {
                        Name = subDir.Name,
                        FullPath = subDir.FullName,
                        IsDirectory = true,
                        LastModified = subDir.LastWriteTime,
                        Parent = dirNode
                    };

                    ScanDirectoryInternal(subDir, subNode, extCounts, ref totalFiles, ref totalDirs, ref totalBytes, reportProgress, ct);

                    currentDirSize += subNode.Size;
                    currentFileCount += subNode.FileCount;
                    currentDirCount += 1 + subNode.DirectoryCount;

                    dirNode.Children.Add(subNode);
                }
                catch (UnauthorizedAccessException) { }
                catch (DirectoryNotFoundException) { }
                catch (SecurityException) { }
                catch (IOException) { }
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (SecurityException) { }
        catch (IOException) { }

        dirNode.Size = currentDirSize;
        dirNode.FileCount = currentFileCount;
        dirNode.DirectoryCount = currentDirCount;
    }

    private static void FinalizeTree(FileSystemNode node, long rootTotalSize)
    {
        node.PercentageOfRoot = rootTotalSize > 0 ? (double)node.Size / rootTotalSize * 100.0 : 0;

        if (node.Children.Count == 0) return;

        // Sort children descending by size (largest first - classic WinDirStat order)
        var sorted = node.Children.OrderByDescending(c => c.Size).ToList();
        node.Children.Clear();

        foreach (var child in sorted)
        {
            child.PercentageOfParent = node.Size > 0 ? (double)child.Size / node.Size * 100.0 : 0;
            FinalizeTree(child, rootTotalSize);
            node.Children.Add(child);
        }
    }
}
