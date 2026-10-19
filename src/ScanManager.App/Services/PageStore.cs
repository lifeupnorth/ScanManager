using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;
using ScanManager.App.Imaging;

namespace ScanManager.App.Services;

/// <summary>
/// Keeps page images on disk (lossless PNG) instead of in memory, so a 100-page feeder
/// job doesn't need gigabytes of RAM. Files are never overwritten, which is what makes
/// undo cheap: an edit writes a new file and undo points the page back at the old one.
/// </summary>
public sealed class PageStore : IDisposable
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromDays(2);

    public PageStore()
    {
        string root = Path.Combine(Path.GetTempPath(), "ScanManager");
        DeleteStaleSessions(root);
        // The process id stays last: cleanup reads it to spare folders of running windows.
        SessionDirectory = Path.Combine(root, $"session-{DateTime.Now:yyyyMMdd-HHmmss}-{Random.Shared.Next():x8}-{Environment.ProcessId}");
        Directory.CreateDirectory(SessionDirectory);
    }

    public string SessionDirectory { get; }

    /// <summary>Writes the image to a new file and returns its path. Thread-safe.</summary>
    public string Save(BitmapSource image)
    {
        string path = Path.Combine(SessionDirectory, $"{Guid.NewGuid():N}.png");
        ImageTools.SavePng(image, path);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(SessionDirectory, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    internal static void DeleteStaleSessions(string root)
    {
        if (!Directory.Exists(root)) return;
        foreach (var dir in Directory.EnumerateDirectories(root, "session-*"))
        {
            try
            {
                if (DateTime.Now - Directory.GetLastWriteTime(dir) > StaleAfter && !IsOwnerRunning(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// A window left open for days doesn't touch its folder, so age alone would delete live pages.
    /// Folder names end in the owner's process id; a reused id only keeps a stale folder a while longer.
    /// </summary>
    private static bool IsOwnerRunning(string dir)
    {
        string name = Path.GetFileName(dir);
        if (!int.TryParse(name[(name.LastIndexOf('-') + 1)..], out int pid)) return false;
        try
        {
            using var process = Process.GetProcessById(pid);
            return true;
        }
        catch (ArgumentException)
        {
            return false; // no such process
        }
    }
}
