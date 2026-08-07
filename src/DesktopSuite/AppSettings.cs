using System.IO;
using System.Text.Json;
using DesktopSuite.Wallpaper;

namespace DesktopSuite;

/// <summary>
/// Lightweight, per-user settings persisted as JSON under %LocalAppData%\DesktopSuite.
/// No registry, no NuGet deps — just System.Text.Json (part of the net8 base class library).
///
/// Stored fields:
///  - AudioEnabled / Volume : the sound toggle + slider from the Wallpaper UI
///  - LastMedia             : the most recent wallpaper media path (remembered, not auto-started)
///  - RendererPid           : PID of the detached mpv-renderer process, so a relaunched GUI can
///                            re-adopt it (the wallpaper is meant to outlive the app)
///  - RotationEnabled / RotationIntervalMinutes / LibraryPath : time-based wallpaper library
///  - LaunchOnStartup : register the app under HKCU Run so it auto-starts (tray-resident) on login
/// </summary>
public sealed class AppSettings
{
    public bool AudioEnabled { get; set; }
    public int Volume { get; set; } = 80;
    public string? LastMedia { get; set; }
    public int RendererPid { get; set; }

    public bool RotationEnabled { get; set; }
    public int RotationIntervalMinutes { get; set; } = 30;
    public string? LibraryPath { get; set; }

    /// <summary>When true, the app registers itself under HKCU\...\Run so it launches (tray-resident,
    /// via the --background flag) on Windows login.</summary>
    public bool LaunchOnStartup { get; set; }

    /// <summary>User's *intent* for desktop-icon visibility. The real state is always read live from the
    /// shell (SysListView32 WS_VISIBLE); this only records what the user asked for so we can re-apply it
    /// after a --background login and reconcile on startup.</summary>
    public bool DesiredIconsHidden { get; set; }

    /// <summary>Safety default: restore (show) desktop icons on exit. True unless the user opts out.</summary>
    public bool RestoreIconsOnExit { get; set; } = true;

    /// <summary>Name of the last applied desktop scene, if any.</summary>
    public string? ActiveSceneName { get; set; }

    private static readonly string FilePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "DesktopSuite", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                if (loaded != null) return loaded;

                // File exists but deserialized to null — treat as corrupt.
                BackupCorrupt(FilePath);
            }
        }
        catch
        {
            // Corrupt or unreadable settings should never break the app — fall back to defaults.
            BackupCorrupt(FilePath);
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });

            // V14 fix: atomic write — serialize to a temp file first, then replace the original.
            // If the process is killed mid-write, the temp file is left half-written but the
            // original settings.json stays intact. File.Replace is atomic on NTFS.
            string tempFile = FilePath + ".tmp";
            File.WriteAllText(tempFile, json);

            if (File.Exists(FilePath))
            {
                // File.Replace creates a backup of the original, then atomically swaps.
                // We don't need the backup, but Replace requires the destination to exist.
                File.Replace(tempFile, FilePath, null);
            }
            else
            {
                // First-ever save: no original to replace, just move.
                File.Move(tempFile, FilePath);
            }
        }
        catch
        {
            // Best-effort persistence; a failed write must not crash the UI.
            // Clean up the temp file if it's still around.
            try { if (File.Exists(FilePath + ".tmp")) File.Delete(FilePath + ".tmp"); } catch { }
        }
    }

    /// <summary>
    /// V14 fix: move a corrupt settings.json aside so the next Save() can write a clean copy
    /// and the user (or support) can inspect what went wrong.
    /// </summary>
    private static void BackupCorrupt(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                string backup = path + ".corrupt." + DateTime.Now.ToString("yyyyMMddHHmmss");
                File.Move(path, backup);
                HostLog.Write($"settings.json was corrupt — backed up to {Path.GetFileName(backup)}.");
            }
        }
        catch { /* best-effort */ }
    }
}
