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
/// Mechanism (verified on Windows 11 build 26200): we delete the <c>IsShortcut</c> registry value
/// under the relevant HKCR progids (<c>lnkfile</c>, <c>piffile</c>, <c>InternetShortcut</c>). Windows
/// keys off the *presence* of that value to decide whether to paint the little arrow overlay; removing
/// it tells the shell "this is not a shortcut", so the arrow is never drawn. This is a completely
/// different — and on build 26200, the only working — path versus the classic <c>Shell Icons</c> value
/// 29, which that build ignores entirely (we tested shell32.dll,-50, a transparent .ico, and a manual
/// Explorer restart; the arrow never disappeared via value 29).
///
/// <para><b>Admin requirement:</b> the progid keys live under <c>HKLM</c>, so writing needs
/// administrator rights; reading (to reflect tray state) does not. The GUI therefore launches a
/// short-lived elevated copy of itself (via <c>runas</c>) to perform the write — see <see cref="App"/>'s
/// <c>--apply-ishortcut</c> branch.</para>
///
/// <para><b>Explorer restart:</b> the overlay change only takes effect when Explorer restarts. Callers
/// must restart Explorer and re-apply shell-dependent state (wallpaper WorkerW, hidden icons)
/// afterwards — <see cref="RestartExplorer"/> plus the caller's own recovery step.</para>
/// </summary>
public static class ShellTweaks
{
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
    /// True when the arrow is currently hidden, i.e. the <c>IsShortcut</c> marker is ABSENT under
    /// <c>HKLM\Software\Classes\lnkfile</c>. Reading HKLM does not require admin.
    /// </summary>
    public static bool IsHideShortcutArrowsEnabled()
    {
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
    /// Returns true on success.
    /// </summary>
    public static bool ApplyIsShortcut(bool enable)
    {
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
