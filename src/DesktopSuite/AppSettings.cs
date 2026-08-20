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

    /// <summary>P1: when true, hide the shortcut-arrow overlay on desktop icons via a per-user
    /// Shell Icons registry tweak (no admin needed). Applied on startup and toggleable from the tray.</summary>
    public bool HideShortcutArrows { get; set; }

    // ---- M4-B: fence box appearance customization ----
    /// <summary>Box corner radius in logical (96-DPI) pixels.</summary>
    public int FenceCornerRadius { get; set; } = 10;
    /// <summary>Box body background alpha (0-255). Lower = more wallpaper shows through.</summary>
    public int FenceBodyOpacity { get; set; } = 180;
    /// <summary>Box header background alpha (0-255).</summary>
    public int FenceHeaderOpacity { get; set; } = 200;
    /// <summary>Box title font size in logical pixels.</summary>
    public float FenceTitleFontSize { get; set; } = 13;
    /// <summary>Title horizontal alignment: 0 = Left (near), 1 = Center.</summary>
    public int FenceTitleAlign { get; set; } = 0;
    /// <summary>Whether to draw the category's emoji glyph before the title.</summary>
    public bool FenceShowGlyph { get; set; } = true;
    /// <summary>Frosted-glass (毛玻璃) mode: blur the wallpaper behind each box. Experimental; off by default.</summary>
    public bool FenceFrosted { get; set; } = false;
    /// <summary>Frosted glass tint opacity (0-255). Lower = more transparent.</summary>
    public int FenceFrostOpacity { get; set; } = 50;

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
                if (loaded != null)
                {
                    // Clamp fence appearance values to safe ranges — prevents invisible fences
                    // when settings were saved with extreme slider positions from a buggy build.
                    loaded.FenceCornerRadius =   Math.Clamp(loaded.FenceCornerRadius,   0, 40);
                    loaded.FenceBodyOpacity =    Math.Clamp(loaded.FenceBodyOpacity,    40, 255);   // min 40 → always faintly visible
                    loaded.FenceHeaderOpacity =  Math.Clamp(loaded.FenceHeaderOpacity,  80, 255);   // min 80 → header always readable
                    loaded.FenceTitleFontSize =  Math.Clamp((int)loaded.FenceTitleFontSize, 8, 28);
                    loaded.FenceTitleAlign =     Math.Clamp(loaded.FenceTitleAlign,     0, 1);
                    loaded.FenceFrostOpacity =   Math.Clamp(loaded.FenceFrostOpacity,   0, 200);
                    return loaded;
                }

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
