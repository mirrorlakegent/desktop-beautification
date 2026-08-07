using System;
using DesktopSuite.Wallpaper;

namespace DesktopSuite.Desktop;

/// <summary>
/// Pure Win32 locator for the desktop shell's icon layer. Stateless: every call re-resolves the
/// window handles (Explorer may migrate DefView between Progman and a WorkerW when a wallpaper
/// surface is spawned — see WorkerWHost — so caching handles here would go stale). Every method
/// returns IntPtr.Zero / null on failure and NEVER throws, so callers can use it from the UI thread.
///
/// NOTE: this deliberately duplicates ~20 lines from WorkerWHost.HoldsDefView. That duplication is
/// intentional — WorkerWHost is the only shell-resolution code we have verified on real machines,
/// and refactoring it for DRY would risk breaking the wallpaper path. Practicality beats purity here.
/// </summary>
public static class DesktopShell
{
    /// <summary>Locate SHELLDLL_DefView (the window that hosts the desktop icon listview).</summary>
    public static IntPtr FindDefView()
    {
        try
        {
            IntPtr progman = NativeMethods.FindWindow("Progman", null);
            if (progman != IntPtr.Zero)
            {
                IntPtr dv = NativeMethods.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (dv != IntPtr.Zero) return dv;
            }

            // Fallback path: on some shells DefView lives inside a WorkerW under the desktop window.
            IntPtr desktop = NativeMethods.GetDesktopWindow();
            IntPtr ww = IntPtr.Zero;
            while (true)
            {
                ww = NativeMethods.FindWindowEx(desktop, ww, "WorkerW", null);
                if (ww == IntPtr.Zero) break;
                IntPtr dv = NativeMethods.FindWindowEx(ww, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (dv != IntPtr.Zero) return dv;
            }
        }
        catch (Exception ex)
        {
            HostLog.Write("DesktopShell.FindDefView failed", ex);
        }
        return IntPtr.Zero;
    }

    /// <summary>The SysListView32 (FolderView) that actually draws the icons. May be Zero.</summary>
    public static IntPtr FindIconListView()
    {
        IntPtr dv = FindDefView();
        if (dv == IntPtr.Zero) return IntPtr.Zero;
        return NativeMethods.FindWindowEx(dv, IntPtr.Zero, "SysListView32", null);
    }

    /// <summary>True if the given window currently has WS_VISIBLE.</summary>
    public static bool IsVisible(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        IntPtr style = NativeMethods.GetWindowLongPtrW(hwnd, NativeMethods.GWL_STYLE);
        return (style.ToInt64() & (long)NativeMethods.WS_VISIBLE) != 0;
    }

    /// <summary>
    /// Live state of the desktop icons. null means the shell layer could not be located (e.g.
    /// Explorer not ready, or unsupported Windows build) — callers must treat null as "unknown",
    /// never as "visible".
    /// </summary>
    public static bool? AreIconsVisible()
    {
        IntPtr lv = FindIconListView();
        if (lv == IntPtr.Zero) return null;
        return IsVisible(lv);
    }
}
