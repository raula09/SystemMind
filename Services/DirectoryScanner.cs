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

            var rootNode = new FileSystemNode
            {
                Name = string.IsNullOrEmpty(rootDir.Name) ? rootDir.FullName : rootDir.Name,
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
                if (now - lastReportTime >= 80) // ~12 fps progress update to prevent UI thrashing
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

            // Recursive scan
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

        // Check for Linux virtual file systems to avoid endless scans or hangs
        if (SkipDirectories.Contains(dir.FullName))
        {
            return;
        }

        // Avoid re-traversing directory symlinks/junctions
        if (dir.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return;
        }

        reportProgress(dir.FullName);
        Interlocked.Increment(ref totalDirs);

        long currentDirSize = 0;
        int currentFileCount = 0;
        int currentDirCount = 0;

        // 1. Enumerate files
        try
        {
            foreach (var fi in dir.EnumerateFiles())
            {
                ct.ThrowIfCancellationRequested();
                try
                {
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

                    dirNode.Children.Add(fileNode);
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

        // 2. Enumerate subdirectories
        try
        {
            foreach (var subDir in dir.EnumerateDirectories())
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    // Skip hidden symlinks / virtual FS mounts
                    if (subDir.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                        SkipDirectories.Contains(subDir.FullName))
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
