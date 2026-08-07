using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace DesktopSuite.Safety;

/// <summary>
/// Describes a single baseline snapshot so restore can be validated and reported.
/// </summary>
public sealed class BackupManifest
{
    public DateTime CreatedAt { get; set; }
    public string OsVersion { get; set; } = "";
    public List<string> RegFiles { get; set; } = new();
    public int DesktopItemCount { get; set; }
}

/// <summary>
/// Phase -1 baseline capture (L1 in-app entry point).
///
/// Captures the user's CURRENT clean desktop state so that every later
/// beautification phase (wallpaper swap, icon hide, accent change) always has a
/// known-good rollback. This is the non-negotiable "保命" step.
///
/// Keep <see cref="RegKeys"/> in sync with backup.cmd / restore.cmd.
/// </summary>
public sealed class BackupManager
{
    // Registry keys that our app is allowed to touch. Anything outside this list
    // is out of scope and never backed up or restored.
    private static readonly string[] RegKeys =
    {
        @"HKEY_CURRENT_USER\Control Panel\Desktop",
        @"HKEY_CURRENT_USER\Control Panel\Colors",
        @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced",
        @"HKEY_CURRENT_USER\Software\Microsoft\Windows\Shell\Bags",
        @"HKEY_CURRENT_USER\Software\Microsoft\Windows\Shell\BagMRU",
        @"HKEY_CURRENT_USER\Software\Microsoft\Windows\DWM",
        @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
    };

    public static string BackupRoot =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DesktopSuite",
            "backups");

    /// <summary>Create a timestamped baseline snapshot. Returns the backup directory.</summary>
    public string CreateBaseline()
    {
        var dir = Path.Combine(BackupRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(dir);

        var manifest = new BackupManifest
        {
            CreatedAt = DateTime.Now,
            OsVersion = Environment.OSVersion.VersionString,
            DesktopItemCount = CountDesktopItems(),
        };

        foreach (var key in RegKeys)
        {
            var file = Path.Combine(dir, Sanitize(key) + ".reg");
            RunReg("export", $"\"{key}\" \"{file}\" /y");
            manifest.RegFiles.Add(Path.GetFileName(file));
        }

        File.WriteAllText(
            Path.Combine(dir, "manifest.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

        return dir;
    }

    private static int CountDesktopItems()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        try
        {
            return Directory.GetFileSystemEntries(desktop).Length;
        }
        catch
        {
            return 0;
        }
    }

    private static string Sanitize(string key) => key.Replace('\\', '_').Replace(':', '_');

    private static void RunReg(string verb, string args)
    {
        var psi = new ProcessStartInfo("reg.exe", $"{verb} {args}")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        using var p = Process.Start(psi)!;
        p.WaitForExit();
    }
}
