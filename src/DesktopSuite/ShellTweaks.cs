using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;
using DesktopSuite.Wallpaper;

namespace DesktopSuite;

/// <summary>
/// Per-user desktop shell tweaks that don't need administrator rights.
///
/// P1 — Hide the shortcut-arrow overlay on desktop/explorer icons. Windows draws that little arrow
/// as icon slot 29 in the shell's icon table; pointing value 29 at <c>shell32.dll,-50</c> (the system's
/// built-in empty/transparent icon resource) makes the overlay invisible. We write it under
/// <c>HKCU</c> (not HKLM), so it is a per-user override that takes effect with no UAC prompt.
///
/// <para><b>Critical (verified on Windows 11 build 26200):</b> the Shell Icons value is only read when
/// Explorer starts. SHChangeNotify / WM_SETTINGCHANGE / icon-cache deletion do NOT make it take effect
/// at runtime. The ONLY reliable path is to restart Explorer via <see cref="RestartExplorer"/>.
/// Callers that depend on the desktop shell (wallpaper WorkerW, hidden icons) must re-apply their
/// state after the restart.</para>
/// </summary>
public static class ShellTweaks
{
    private const string ShellIconsKey =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Icons";
    private const string ArrowValueName = "29";

    /// <summary>
    /// Full path to the system's built-in transparent icon resource. Written as REG_SZ (not
    /// ExpandString) for maximum compatibility — every classic "hide arrow" tool uses this exact format.
    /// </summary>
    private static readonly string TransparentArrowValue =
        Path.Combine(Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows",
            @"System32\shell32.dll,-50");

    // ---- Re-entrancy guard: prevent double-restart if user clicks rapidly ----
    private static int _isRestarting = 0;

    /// <summary>True when value 29 already points at the transparent system icon (tweak is active).</summary>
    public static bool IsHideShortcutArrowsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(ShellIconsKey);
            var v = key?.GetValue(ArrowValueName);
            if (v == null) return false;
            return string.Equals(v.ToString(), TransparentArrowValue, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            HostLog.Write("读取去箭头注册表失败", ex);
            return false;
        }
    }

    /// <summary>
    /// Write (or remove) the Shell Icons value 29. Does NOT restart Explorer — the caller decides
    /// whether a restart is needed based on the returned <c>changed</c> flag.
    /// </summary>
    /// <returns>True if the registry was actually changed.</returns>
    public static bool WriteArrowRegistry(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(ShellIconsKey, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(ShellIconsKey);
            var current = key.GetValue(ArrowValueName) as string;
            bool changed;

            if (enable)
            {
                if (!string.Equals(current, TransparentArrowValue, StringComparison.OrdinalIgnoreCase))
                {
                    key.SetValue(ArrowValueName, TransparentArrowValue, RegistryValueKind.String);
                    changed = true;
                }
                else { changed = false; }
            }
            else
            {
                if (current != null)
                {
                    key.DeleteValue(ArrowValueName, throwOnMissingValue: false);
                    changed = true;
                }
                else { changed = false; }
            }

            return changed;
        }
        catch (Exception ex)
        {
            HostLog.Write("去箭头注册表操作失败", ex);
            return false;
        }
    }

    /// <summary>
    /// Kill and relaunch the Explorer shell. Blocks until Progman window reappears (up to 6 s).
    /// Thread-safe: if already restarting, returns immediately (no-op) to prevent cascading restarts.
    /// </summary>
    public static void RestartExplorer()
    {
        // Guard: don't allow nested or concurrent restarts.
        if (Interlocked.CompareExchange(ref _isRestarting, 1, 0) != 0)
        {
            HostLog.Write("Explorer 重已在进行中，跳过重复重启");
            return;
        }

        try
        {
            using var kill = new Process
            {
                StartInfo = new ProcessStartInfo("taskkill", "/f /im explorer.exe")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                }
            };
            kill.Start();
            kill.WaitForExit(3000);
        }
        catch (Exception ex)
        {
            HostLog.Write("结束 explorer 进程失败（可忽略）", ex);
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            HostLog.Write("重启 explorer 失败", ex);
            Interlocked.Exchange(ref _isRestarting, 0);
            return;
        }

        // Wait for the shell to come back so dependent re-apply finds a valid Progman.
        for (int i = 0; i < 30; i++)
        {
            IntPtr progman = IntPtr.Zero;
            try { progman = FindWindow("Progman", null); } catch { }
            if (progman != IntPtr.Zero) break;
            Thread.Sleep(200);
        }

        Interlocked.Exchange(ref _isRestarting, 0);
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);
}
