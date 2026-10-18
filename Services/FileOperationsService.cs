using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DiagnoX.Services;

public sealed class FileOperationsService
{
    private static readonly HashSet<string> ProtectedLinuxPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/", "/bin", "/boot", "/dev", "/etc", "/lib", "/lib32", "/lib64", "/libx32",
        "/media", "/mnt", "/opt", "/proc", "/root", "/run", "/sbin", "/snap", "/srv",
        "/sys", "/usr", "/usr/bin", "/usr/lib", "/usr/sbin", "/var", "/var/log"
    };

    public bool IsProtected(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return true;
        var fullPath = Path.GetFullPath(path).TrimEnd('/', '\\');
        if (string.IsNullOrEmpty(fullPath)) fullPath = "/";

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            if (fullPath == "/" || ProtectedLinuxPaths.Contains(fullPath)) return true;
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('/', '\\');
            if (string.Equals(fullPath, home, StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(fullPath, "/home", StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    public void OpenFile(string path)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                Process.Start("xdg-open", $"\"{path}\"");
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start("open", $"\"{path}\"");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to open {path}: {ex.Message}");
        }
    }

    public void OpenFolder(string path)
    {
        var targetDir = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(targetDir))
        {
            OpenFile(targetDir);
        }
    }

    public void OpenTerminal(string path)
    {
        var targetDir = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(targetDir)) return;

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                var terminals = new[] { "x-terminal-emulator", "gnome-terminal", "ptyxis", "konsole", "alacritty", "kitty", "xfce4-terminal" };
                foreach (var term in terminals)
                {
                    try
                    {
                        var psi = new ProcessStartInfo(term)
                        {
                            WorkingDirectory = targetDir,
                            UseShellExecute = true
                        };
                        Process.Start(psi);
                        return;
                    }
                    catch { }
                }
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start(new ProcessStartInfo("cmd.exe", $"/K cd /d \"{targetDir}\"") { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to open terminal in {targetDir}: {ex.Message}");
        }
    }

    public (bool Success, string Message) MoveToTrash(string path)
    {
        if (IsProtected(path))
        {
            return (false, "Action blocked: this path is a protected system or root directory.");
        }

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                // Try gio trash first
                try
                {
                    var p = Process.Start(new ProcessStartInfo("gio", $"trash \"{path}\"")
                    {
                        RedirectStandardError = true,
                        RedirectStandardOutput = true,
                        UseShellExecute = false
                    });
                    p?.WaitForExit(3000);
                    if (p?.ExitCode == 0) return (true, "Moved to Trash successfully.");
                }
                catch { }

                // Fallback to manual move to ~/.local/share/Trash/files
                var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var trashFilesDir = Path.Combine(userHome, ".local", "share", "Trash", "files");
                Directory.CreateDirectory(trashFilesDir);

                var targetDest = Path.Combine(trashFilesDir, Path.GetFileName(path) + "_" + DateTime.Now.Ticks);
                if (Directory.Exists(path))
                {
                    Directory.Move(path, targetDest);
                    return (true, "Moved to ~/.local/share/Trash.");
                }
                if (File.Exists(path))
                {
                    File.Move(path, targetDest);
                    return (true, "Moved to ~/.local/share/Trash.");
                }
            }

            return (false, "File or directory not found.");
        }
        catch (Exception ex)
        {
            return (false, $"Failed to move to trash: {ex.Message}");
        }
    }

    public (bool Success, string Message) PermanentDelete(string path)
    {
        if (IsProtected(path))
        {
            return (false, "Permanent deletion blocked: path is protected.");
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                return (true, "File permanently deleted.");
            }
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
                return (true, "Folder permanently deleted.");
            }
            return (false, "Item not found.");
        }
        catch (Exception ex)
        {
            return (false, $"Deletion error: {ex.Message}");
        }
    }
}
