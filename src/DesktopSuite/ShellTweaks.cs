using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using DesktopSuite.Wallpaper;

namespace DesktopSuite;

/// <summary>
/// Per-user desktop shell tweaks that don't need administrator rights.
///
/// P1 — Hide the shortcut-arrow overlay on desktop/explorer icons. Windows draws that little arrow
/// as icon slot 29 in the shell's icon table; pointing Shell Icons value 29 at a fully-transparent
/// .ico makes the overlay invisible. We write it under <c>HKCU</c> (not HKLM), so it is a
/// per-user override that takes effect with no UAC prompt. The change is applied via a lightweight
/// <c>SHChangeNotify</c> icon-cache refresh; a full Explorer restart is offered as a manual tray
/// action only when a given Windows build still caches the old arrow.
/// </summary>
public static class ShellTweaks
{
    // Explorer reads both HKLM and HKCU; the current-user key wins for this user and needs no admin.
    private const string ShellIconsKey =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Icons";
    private const string ArrowValueName = "29";

    /// <summary>Absolute path to the transparent .ico shipped next to the EXE (copied by the build).</summary>
    private static string ArrowIconPath() =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "hide_arrow.ico");

    /// <summary>True when value 29 already points at our transparent .ico (i.e. the tweak is applied).</summary>
    public static bool IsHideShortcutArrowsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(ShellIconsKey);
            var v = key?.GetValue(ArrowValueName);
            if (v == null) return false;
            return string.Equals(v.ToString(), ArrowIconPath(), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            HostLog.Write("读取去箭头注册表失败", ex);
            return false;
        }
    }

    /// <summary>
    /// Apply or remove the shortcut-arrow tweak.
    /// </summary>
    /// <param name="enable">When true, point value 29 at the transparent .ico; when false, delete it.</param>
    /// <param name="forceRestartExplorer">When true and the registry actually changed, restart Explorer
    /// so the new icon renders. Pass false on startup when you'd rather not restart Explorer unless needed.</param>
    /// <returns>True if the registry was changed (so a restart was/will be triggered).</returns>
    public static bool ApplyHideShortcutArrows(bool enable, bool forceRestartExplorer)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(ShellIconsKey, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(ShellIconsKey);
            var current = key.GetValue(ArrowValueName) as string;
            bool changed;

            if (enable)
            {
                string desired = ArrowIconPath();
                if (!string.Equals(current, desired, StringComparison.OrdinalIgnoreCase))
                {
                    key.SetValue(ArrowValueName, desired, RegistryValueKind.String);
                    changed = true;
                }
                else
                {
                    changed = false;
                }
            }
            else
            {
                if (current != null)
                {
                    key.DeleteValue(ArrowValueName, throwOnMissingValue: false);
                    changed = true;
                }
                else
                {
                    changed = false;
                }
            }

            if (changed)
            {
                // Lightweight refresh: tell the shell its icon table changed so the shortcut-arrow
                // overlay rebuilds. This avoids killing Explorer (which would also tear down the
                // wallpaper WorkerW and any open File Explorer windows). If a given Windows build
                // still caches the old arrow, the tray "重启资源管理器" item forces a full reload.
                NotifyShellIconChange();
                if (forceRestartExplorer)
                    RestartExplorer();
            }

            return changed;
        }
        catch (Exception ex)
        {
            HostLog.Write("去箭头注册表操作失败", ex);
            return false;
        }
    }

    /// <summary>Notify the shell that its icon associations changed, prompting an icon-cache rebuild
    /// (this is what makes the shortcut-arrow change show up without restarting Explorer).</summary>
    private static void NotifyShellIconChange()
    {
        try
        {
            const int SHCNE_ASSOCCHANGED = 0x08000000;
            const uint SHCNF_IDLIST = 0x0000;
            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            HostLog.Write("SHChangeNotify 调用失败（可忽略）", ex);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    /// <summary>Kill and relaunch the Explorer shell so the icon-cache change becomes visible.</summary>
    public static void RestartExplorer()
    {
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
            kill.WaitForExit(2000);
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
        }
    }
}
