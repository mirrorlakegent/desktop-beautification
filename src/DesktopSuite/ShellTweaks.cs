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
/// as icon slot 29 in the shell's icon table; pointing value 29 at <c>shell32.dll,-50</c> (the system's
/// built-in empty/transparent icon resource) makes the overlay invisible. We write it under
/// <c>HKCU</c> (not HKLM), so it is a per-user override that takes effect with no UAC prompt.
///
/// <para><b>How it works without restarting Explorer:</b></para>
/// <list type="bullet">
///   <item>Delete the user's icon cache database files (IconCache.db, iconcache_*.db).</item>
///   <item>Broadcast <c>WM_SETTINGCHANGE</c> with "Shell Icons" so every top-level window (including
///     Explorer's desktop/Taskbar window) re-reads the registry.</item>
///   <item>Call <c>SHChangeNotify(SHCNE_ASSOCCHANGED)</c> for good measure.</item>
/// </list>
/// This three-step cache invalidation makes the arrow disappear immediately on Windows 10/11 without
/// killing Explorer (which would also tear down the wallpaper WorkerW). A full Explorer restart is
/// still offered as a manual tray action for edge cases where the lightweight refresh doesn't stick.
/// </summary>
public static class ShellTweaks
{
    // Explorer reads both HKLM and HKCU; the current-user key wins for this user and needs no admin.
    private const string ShellIconsKey =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Icons";
    private const string ArrowValueName = "29";

    /// <summary>
    /// The system built-in empty-icon resource used by virtually every "hide arrow" tool.
    /// Resource -50 in shell32.dll is a 16×16 / 32×32 transparent placeholder — no custom .ico needed.
    /// </summary>
    private const string TransparentArrowValue = @"%SystemRoot%\System32\shell32.dll,-50";

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
    /// Apply or remove the shortcut-arrow tweak.
    /// </summary>
    /// <param name="enable">When true, point value 29 at the transparent system icon; when false, delete it.</param>
    /// <param name="forceRestartExplorer">When true and registry changed, restart Explorer unconditionally.
    /// Pass false on startup to use lightweight cache invalidation instead.</param>
    /// <returns>True if the registry was actually changed.</returns>
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
                if (!string.Equals(current, TransparentArrowValue, StringComparison.OrdinalIgnoreCase))
                {
                    key.SetValue(ArrowValueName, TransparentArrowValue, RegistryValueKind.ExpandString);
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

            if (changed)
            {
                if (forceRestartExplorer)
                    RestartExplorer();
                else
                    InvalidateIconCache();
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
    /// Force Windows to rebuild its icon cache so the Shell Icons change takes effect immediately,
    /// without restarting Explorer (and thus without tearing down the wallpaper WorkerW).
    ///
    /// Strategy (ordered by reliability):
    /// <list type="number">
    ///   <item>Delete IconCache.db + iconcache_*.db from the user profile.</item>
    ///   <item>Broadcast WM_SETTINGCHANGE("Shell Icons") to all top-level windows.</item>
    ///   <item>Call SHChangeNotify(SHCNE_ASSOCCHANGED) as a safety net.</item>
    /// </list>
    /// </summary>
    private static void InvalidateIconCache()
    {
        try
        {
            // Step 1: Nuke the on-disk icon cache so Explorer can't serve stale images.
            try
            {
                var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

                // Main legacy cache
                var mainDb = Path.Combine(localAppData, "IconCache.db");
                DeleteQuietly(mainDb);

                // Win10/11 per-size caches
                var explorerCache = Path.Combine(localAppData, "Microsoft", "Windows", "Explorer");
                if (Directory.Exists(explorerCache))
                {
                    foreach (var f in Directory.GetFiles(explorerCache, "iconcache_*"))
                        DeleteQuietly(f);
                    // Also clean thumbcache variants that sometimes hold overlay copies
                    foreach (var f in Directory.GetFiles(explorerCache, "thumbcache_*.db"))
                        DeleteQuietly(f);
                }
            }
            catch (Exception ex)
            {
                HostLog.Write("删除图标缓存文件时部分失败（可忽略）", ex);
            }

            // Step 2: Broadcast WM_SETTINGCHANGE — this is what forces Explorer to re-read Shell Icons.
            const int HWND_BROADCAST = 0xffff;
            const uint WM_SETTINGCHANGE = 0x001A;
            var settingNamePtr = Marshal.StringToHGlobalUni("Shell Icons");
            try
            {
                SendMessageTimeout(
                    new IntPtr(HWND_BROADCAST), WM_SETTINGCHANGE, IntPtr.Zero, settingNamePtr,
                    SMTO_ABORTIFHUNG, 2000, out _);
            }
            finally
            {
                Marshal.FreeHGlobal(settingNamePtr);
            }

            // Step 3: SHChangeNotify as belt-and-suspenders.
            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            HostLog.Write("图标缓存刷新失败（可忽略，用户仍可手动重启资源管理器）", ex);
        }
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch { /* best-effort */ }
    }

    // ---- P/Invoke declarations ----

    private const int SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNF_IDLIST = 0x0000;
    private const uint SMTO_ABORTIFHUNG = 0x0002;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam,
        uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    [DllImport("shell32.dll")]
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
