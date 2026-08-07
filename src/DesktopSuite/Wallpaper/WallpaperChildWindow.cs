using System;
using System.Runtime.InteropServices;

namespace DesktopSuite.Wallpaper;

/// <summary>
/// Creates a native child window attached to the wallpaper layer (WorkerW or Progman).
/// Used as the render target for mpv / WebView2 / other external renderers.
///
/// NOTE: the window itself is intentionally blank. The wallpaper layer (WorkerW under Progman)
/// is a layered window on modern shells, and a plain GDI self-drawn child is NOT composited
/// inside a layered parent -- so we do not paint here. mpv drives the surface itself and shows
/// correctly; the test pattern therefore reuses mpv with a solid-colour BMP.
/// </summary>
public sealed class WallpaperChildWindow : IDisposable
{
    private readonly string _className;
    private readonly NativeMethods.WndProc _wndProc; // kept alive: the OS holds a raw pointer
    private IntPtr _hWnd = IntPtr.Zero;

    public IntPtr Handle => _hWnd;

    public WallpaperChildWindow()
    {
        _className = $"DSWallpaperHost_{Guid.NewGuid():N}";
        _wndProc = WndProc;
    }

    /// <summary>
    /// Creates a visible child window filling the parent's client area.
    /// Must be called on a thread that pumps Win32 messages.
    /// </summary>
    public IntPtr Create(IntPtr parentHwnd)
    {
        if (_hWnd != IntPtr.Zero) return _hWnd;

        if (!NativeMethods.IsWindow(parentHwnd))
            throw new ArgumentException($"Parent HWND 0x{parentHwnd.ToInt64():X} is not a valid window.", nameof(parentHwnd));

        IntPtr hInstance = NativeMethods.GetModuleHandle(null);

        var wcex = new NativeMethods.WNDCLASSEX
        {
            cbSize = Marshal.SizeOf(typeof(NativeMethods.WNDCLASSEX)),
            lpfnWndProc = _wndProc,
            hInstance = hInstance,
            lpszClassName = _className,
            style = 0
        };

        ushort atom = NativeMethods.RegisterClassEx(ref wcex);
        if (atom == 0)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "RegisterClassEx failed.");

        // Child window coordinates are relative to the PARENT client area, so they must start
        // at 0,0 -- not at the virtual-screen origin (which is negative when a monitor sits
        // to the left of the primary one, pushing the wallpaper off-screen).
        int w, h;
        if (NativeMethods.GetClientRect(parentHwnd, out var pr) && pr.Width > 0 && pr.Height > 0)
        {
            w = pr.Width;
            h = pr.Height;
        }
        else
        {
            w = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
            h = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);
        }

        _hWnd = NativeMethods.CreateWindowEx(
            NativeMethods.WS_EX_NOACTIVATE,
            _className,
            "DesktopSuite Wallpaper Host",
            NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE | NativeMethods.WS_CLIPSIBLINGS | NativeMethods.WS_CLIPCHILDREN,
            0, 0, w, h,
            parentHwnd,
            IntPtr.Zero,
            hInstance,
            IntPtr.Zero);

        if (_hWnd == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "CreateWindowEx failed.");

        // Z-order depends on what the parent is.
        //
        // Real WorkerW  -> it has no other children, and sitting at the bottom keeps us out of the
        //                  way of anything Explorer adds later. Icons live in a different window,
        //                  above ours, so they stay visible. This is the good path.
        //
        // Progman       -> SHELLDLL_DefView is a *sibling* here. HWND_BOTTOM would put us behind it
        //                  and we would never be seen; that was the old silent-failure bug. Go on
        //                  top instead: content shows, at the cost of covering the icons.
        bool parentHostsIcons =
            NativeMethods.FindWindowEx(parentHwnd, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero;

        IntPtr insertAfter = parentHostsIcons ? NativeMethods.HWND_TOP : NativeMethods.HWND_BOTTOM;

        NativeMethods.SetWindowPos(_hWnd, insertAfter, 0, 0, w, h,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);

        NativeMethods.GetWindowRect(_hWnd, out var self);
        HostLog.Write(
            $"Child window created: 0x{_hWnd.ToInt64():X} under parent 0x{parentHwnd.ToInt64():X} " +
            $"size {w}x{h} screenRect=({self.Left},{self.Top})-({self.Right},{self.Bottom}) " +
            $"visible={NativeMethods.IsWindowVisible(_hWnd)} " +
            $"zorder={(parentHostsIcons ? "TOP (parent hosts icons)" : "BOTTOM")}");

        return _hWnd;
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        const uint WM_CLOSE = 0x0010;

        if (msg == WM_CLOSE)
        {
            Dispose();
            return IntPtr.Zero;
        }

        // The window is a render host only; mpv composites into it. DefWindowProc handles the rest.
        return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hWnd != IntPtr.Zero)
        {
            NativeMethods.DestroyWindow(_hWnd);
            _hWnd = IntPtr.Zero;
        }
        try
        {
            NativeMethods.UnregisterClass(_className, NativeMethods.GetModuleHandle(null));
        }
        catch { }
    }
}
