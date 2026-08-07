using System;
using System.IO;
using System.Text;

namespace DesktopSuite.Wallpaper;

/// <summary>
/// Minimal file logger shared by the main process and the wallpaper renderer process.
///
/// WHY A FILE: the renderer is a WinExe (no console). Debug.WriteLine only reaches an
/// attached debugger, and piping stderr is fragile across a GUI-subsystem child.
/// A plain text file is the one channel that always survives, which matters a lot when
/// the failure mode is "nothing visibly happens".
/// </summary>
public static class HostLog
{
    private static readonly object Gate = new();

    public static string LogDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DesktopSuite", "logs");

    public static string LogPath => Path.Combine(LogDirectory, "wallpaper.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogDirectory);

                // Keep the log from growing forever; 512 KB is plenty for diagnosis.
                var fi = new FileInfo(LogPath);
                if (fi.Exists && fi.Length > 512 * 1024)
                    fi.Delete();

                string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [pid {Environment.ProcessId}] {message}{Environment.NewLine}";
                File.AppendAllText(LogPath, line, Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }

    public static void Write(string message, Exception ex) =>
        Write($"{message} :: {ex.GetType().Name}: {ex.Message}");

    /// <summary>Returns the tail of the log, for showing inline in the UI.</summary>
    public static string ReadTail(int lines = 25)
    {
        try
        {
            if (!File.Exists(LogPath)) return "(no log yet)";
            var all = File.ReadAllLines(LogPath);
            int skip = Math.Max(0, all.Length - lines);
            return string.Join(Environment.NewLine, all[skip..]);
        }
        catch (Exception ex)
        {
            return $"(cannot read log: {ex.Message})";
        }
    }
}
