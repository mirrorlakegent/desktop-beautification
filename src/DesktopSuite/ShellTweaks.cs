using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using DesktopSuite.Wallpaper;
using Microsoft.Win32;

namespace DesktopSuite;

/// <summary>
/// Per-user desktop shell tweaks.
///
/// P1 — Hide the shortcut-arrow overlay on desktop/explorer icons.
///
/// <para><b>Compatibility notice (2026-08-18):</b> ALL known programmatic methods for hiding
/// shortcut arrows have been verified as <b>non-functional</b> on Windows 11 builds 26100+
/// (including 26100.8972). Microsoft refactored the overlay rendering pipeline in shell32.dll
/// to hard-code the arrow during icon composition, bypassing both the legacy
/// <c>Shell Icons</c> value 29 and the <c>IsShortcut</c> registry marker. The
/// <see cref="IsShortcutSupported"/> guard reflects this reality.</para>
///
/// <para><b>Historical mechanisms tested (all failed on 26100+):</b></para>
/// <list type="bullet">
///   <item>Shell Icons value 29 → ignored (build 26200 first, now 26100 too)</item>
///   <item>shell32.dll,-50 → ignored</item>
///   <item>Transparent .ico file → ignored</item>
///   <item>Deleting IsShortcut from HKCR progids → ignored (overlay no longer checks it)</item>
/// </list>
///
/// <para><b>Admin requirement (for older Windows where supported):</b> the progid keys live under
/// <c>HKLM</c>, so writing needs administrator rights; reading does not.</para>
/// </summary>
public static class ShellTweaks
{
    // Minimum build number where the hide-arrows feature is known broken.
    // 26100 = Win11 24H2 RTM; confirmed broken on 26100.8972 (2026-08 cumulative update).
    private const int MinBrokenBuild = 26100;

    // Progids whose IsShortcut marker we toggle. These cover the common shortcut types seen on the
    // desktop (.lnk files, .pif, and .url internet shortcuts).
    private static readonly string[] IsShortcutProgIds =
    {
        @"Software\Classes\lnkfile",
        @"Software\Classes\piffile",
        @"Software\Classes\InternetShortcut",
    };

    // Legacy Shell Icons value 29 from the earlier (build-26200-broken) approach — removed on launch.
    private const string LegacyShellIconsKey =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Icons";
    private const string LegacyArrowValueName = "29";

    // Re-entrancy guard: prevent double Explorer restart if the user clicks rapidly.
    private static int _isRestarting = 0;

    /// <summary>True when the current process holds administrator rights.</summary>
    public static bool IsRunningAsAdmin()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>
    /// Returns true if the hide-shortcut-arrows mechanism is potentially supported on this
    /// Windows build. On builds ≥ 26100 (Win11 24H2+ with 2026-08 cumulative updates) Microsoft
    /// hard-codes the arrow in shell32.dll's icon pipeline, making all registry methods ineffective.
    /// </summary>
    public static bool IsShortcutSupported => Environment.OSVersion.Version.Build < MinBrokenBuild;

    /// <summary>
    /// True when the arrow is currently hidden (only meaningful on supported builds).
    /// Checks whether the <c>IsShortcut</c> marker is ABSENT under
    /// <c>HKLM\Software\Classes\lnkfile</c>. Reading HKLM does not require admin.
    /// Always returns <c>false</c> on unsupported builds.
    /// </summary>
    public static bool IsHideShortcutArrowsEnabled()
    {
        if (!IsShortcutSupported) return false;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"Software\Classes\lnkfile");
            // Enabled == the IsShortcut value does NOT exist.
            return key?.GetValue("IsShortcut") == null;
        }
        catch (Exception ex)
        {
            HostLog.Write("读取去箭头状态失败", ex);
            return false;
        }
    }

    /// <summary>
    /// Delete (enable=true) or recreate (enable=false) the <c>IsShortcut</c> marker under HKLM for
    /// every tracked progid. <b>Requires administrator rights</b> — invoke from an elevated helper.
    /// Returns true on success; returns false without side-effects on unsupported builds.
    /// </summary>
    public static bool ApplyIsShortcut(bool enable)
    {
        if (!IsShortcutSupported)
        {
            HostLog.Write($"去箭头：当前 Windows 版本 (Build {Environment.OSVersion.Version.Build}) 不支持此功能，跳过");
            return false;
        }

        try
        {
            foreach (var progId in IsShortcutProgIds)
            {
                using var key = Registry.LocalMachine.OpenSubKey(progId, writable: true);
                if (key == null)
                {
                    HostLog.Write($"去箭头：注册表项不存在，跳过 {progId}");
                    continue;
                }
                bool present = key.GetValue("IsShortcut") != null;
                if (enable && present)
                    key.DeleteValue("IsShortcut", throwOnMissingValue: false);
                else if (!enable && !present)
                    key.SetValue("IsShortcut", "", RegistryValueKind.String);
            }
            HostLog.Write($"去箭头：IsShortcut 操作完成（enable={enable}）");
            return true;
        }
        catch (Exception ex)
        {
            HostLog.Write("去箭头：IsShortcut 注册表操作失败（需管理员）", ex);
            return false;
        }
    }

    /// <summary>Remove the legacy Shell Icons value 29 written by earlier builds (HKCU — no admin).</summary>
    public static void CleanupLegacyShellIcons()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(LegacyShellIconsKey, writable: true);
            if (key == null) return;
            if (key.GetValue(LegacyArrowValueName) != null)
            {
                key.DeleteValue(LegacyArrowValueName, throwOnMissingValue: false);
                HostLog.Write("去箭头：已清理旧版 Shell Icons 值 29");
            }
        }
        catch (Exception ex)
        {
            HostLog.Write("去箭头：清理旧版 Shell Icons 失败（可忽略）", ex);
        }
    }

    /// <summary>
    /// Kill and relaunch the Explorer shell. Blocks until the Progman window reappears (up to 6 s).
    /// Thread-safe: if already restarting, returns immediately (no-op) to prevent cascading restarts.
    /// </summary>
    public static void RestartExplorer()
    {
        // Guard: don't allow nested or concurrent restarts.
        if (Interlocked.CompareExchange(ref _isRestarting, 1, 0) != 0)
        {
            HostLog.Write("Explorer 重启已在进行中，跳过重复重启");
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
