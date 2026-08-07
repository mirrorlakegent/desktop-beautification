using System.Diagnostics;
using System.IO;

namespace DesktopSuite.Safety;

/// <summary>
/// Phase -1 restore (L1 in-app / L2 CLI entry point).
///
/// Restore strategy:
///  - Import every *.reg from the chosen baseline (reverts wallpaper, colors,
///    Explorer advanced, Shell Bags / icon layout, DWM accent, Personalize).
///  - Restart Explorer so the desktop window is recreated in its default
///    (visible) state. This is what makes a virtual-view icon-hide fully
///    reversible even from the pure-batch L3 path.
/// </summary>
public sealed class RestoreManager
{
    /// <summary>Find the most recent baseline directory, or null if none exist.</summary>
    public static string? FindLatestBackup()
    {
        var root = BackupManager.BackupRoot;
        if (!Directory.Exists(root)) return null;
        var dirs = Directory.GetDirectories(root);
        if (dirs.Length == 0) return null;

        // Sort by creation time so the result is correct regardless of dir-name format.
        Array.Sort(dirs, (a, b) => Directory.GetCreationTime(a).CompareTo(Directory.GetCreationTime(b)));
        return dirs[^1];
    }

    /// <summary>Full restore from a specific baseline directory.</summary>
    public void RestoreFrom(string backupDir)
    {
        foreach (var reg in Directory.GetFiles(backupDir, "*.reg"))
        {
            RunReg("import", $"\"{reg}\"");
        }
        RestartExplorer();
    }

    /// <summary>Find latest baseline and restore it. No-op if none exists.</summary>
    public void RestoreLatest()
    {
        var latest = FindLatestBackup();
        if (latest is not null) RestoreFrom(latest);
    }

    private static void RestartExplorer()
    {
        Run("taskkill.exe", "/f /im explorer.exe");
        Run("explorer.exe", "");
    }

    private static void Run(string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        };
        using var p = Process.Start(psi)!;
        p.WaitForExit();
    }

    private static void RunReg(string verb, string args)
    {
        var psi = new ProcessStartInfo("reg.exe", $"{verb} {args}")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        };
        using var p = Process.Start(psi)!;
        p.WaitForExit();
    }
}
