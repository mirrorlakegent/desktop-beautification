using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace DesktopSuite;

/// <summary>
/// Manages login auto-start via the current-user Run key (HKCU\Software\Microsoft\Windows\CurrentVersion\Run).
/// HKCU requires no admin rights, is per-user isolated, and is the lightest reliable way to launch on login.
/// The registered command uses the --background flag so the app starts tray-resident without a window.
/// </summary>
public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DesktopSuite";

    /// <summary>True when the Run entry exists (regardless of whether it points at the current EXE).</summary>
    public static bool IsRegistered()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string s && s.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Create or remove the Run entry. The value always carries --background so a login launch
    /// stays tray-resident and silent.</summary>
    public static void SetEnabled(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (enable)
                key.SetValue(ValueName, $"\"{ExePath()}\" --background");
            else
                key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch
        {
            // A failed registry write must not crash the UI; auto-start is a convenience, not critical.
        }
    }

    /// <summary>
    /// On launch, keep a previously-written Run entry pointed at this exact EXE. If the app was moved
    /// (or run from a different build folder), the stale absolute path would fail silently — refresh it.
    /// No-ops when the entry is absent (we never create one here; that is SetEnabled's job).
    /// </summary>
    public static void SelfHeal()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            if (key?.GetValue(ValueName) is not string existing) return;

            string current = $"\"{ExePath()}\" --background";
            if (!string.Equals(existing, current, StringComparison.OrdinalIgnoreCase))
            {
                using var w = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
                w?.SetValue(ValueName, current);
            }
        }
        catch
        {
            // Best-effort; an unreadable/locked key is non-fatal.
        }
    }

    private static string ExePath()
    {
        // Prefer the real apphost EXE in the output folder; fall back to the current process image.
        string apphost = Path.Combine(AppContext.BaseDirectory, "DesktopSuite.exe");
        return File.Exists(apphost)
            ? apphost
            : (Process.GetCurrentProcess().MainModule?.FileName ?? apphost);
    }
}
