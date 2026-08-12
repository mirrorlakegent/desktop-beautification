using System;
using System.Runtime.InteropServices;
using System.Text;

namespace DesktopSuite.Desktop.Organizer;

/// <summary>
/// Extra P/Invoke surface needed by the Fences layer. Kept separate from
/// <c>DesktopSuite.Wallpaper.NativeMethods</c> so we do not disturb the (verified) wallpaper path.
/// Everything here is best-effort: callers must tolerate failure (null/zero/exception) and degrade
/// gracefully (a plain top-level transparent window is still perfectly usable).
/// </summary>
internal static class FenceNative
{
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr CreateRectRgn(int nLeftRect, int nTopRect, int nRightRect, int nBottomRect);

    /// <summary>Combines two regions. fnCombineMode=2 is RGN_OR (union). Returns the result region type.</summary>
    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern int CombineRgn(IntPtr hrgnDest, IntPtr hrgnSrc1, IntPtr hrgnSrc2, int fnCombineMode);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    public static extern IntPtr GetWindowLongPtrW(IntPtr hWnd, int nIndex);

    /// <summary>Per-monitor DPI of a given window (dots per inch; 96 = 100%). Returns 0 on older
    /// OSes / when the process is not DPI-aware — callers fall back to 96.</summary>
    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    public static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    // ---- Round 4-bis fixup: SetWindowPos + SWP flags (clearing WS_EX_TOOLWINDOW needs SWP_FRAMECHANGED) ----
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    public const int GWL_EXSTYLE = -20;
    public const uint WS_EX_TOOLWINDOW = 0x00000080;
    public const uint WS_EX_NOACTIVATE = 0x08000000;

    // SetWindowPos uFlags (used by the EXSTYLE fixup to force a DWM re-evaluation after clearing
    // WS_EX_TOOLWINDOW; SWP_FRAMECHANGED is what makes DWM re-composite the WPF child window).
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_FRAMECHANGED = 0x0020;

    /// <summary>RGN_OR — union of the two regions (used to merge all box rectangles).</summary>
    public const int RGN_OR = 2;

    // ---- Round 4-bis window-style probe surface ----

    public const int GWL_STYLE = -16;

    // Standard window styles (GWL_STYLE)
    public const uint WS_VISIBLE = 0x10000000;
    public const uint WS_CHILD = 0x40000000;
    public const uint WS_POPUP = 0x80000000;
    public const uint WS_DISABLED = 0x08000000;
    public const uint WS_CLIPSIBLINGS = 0x04000000;
    public const uint WS_CLIPCHILDREN = 0x02000000;
    public const uint WS_CAPTION = 0x00C00000;
    public const uint WS_BORDER = 0x00800000;

    // Extended window styles (GWL_EXSTYLE) — WS_EX_TOOLWINDOW / WS_EX_NOACTIVATE defined above
    public const uint WS_EX_LAYERED = 0x00080000;
    public const uint WS_EX_TRANSPARENT = 0x00000020;
    public const uint WS_EX_APPWINDOW = 0x00040000;

    public const uint GW_CHILD = 5;
    public const uint GW_HWNDNEXT = 2;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("dwmapi.dll", SetLastError = true)]
    public static extern int DwmIsCompositionEnabled(out bool pfEnabled);

    /// <summary>Human-readable list of the standard styles present in <paramref name="style"/>.</summary>
    public static string DecodeStyle(uint style)
    {
        var parts = new System.Collections.Generic.List<string>();
        if ((style & WS_VISIBLE) != 0) parts.Add("WS_VISIBLE");
        if ((style & WS_CHILD) != 0) parts.Add("WS_CHILD");
        if ((style & WS_POPUP) != 0) parts.Add("WS_POPUP");
        if ((style & WS_DISABLED) != 0) parts.Add("WS_DISABLED");
        if ((style & WS_CLIPSIBLINGS) != 0) parts.Add("WS_CLIPSIBLINGS");
        if ((style & WS_CLIPCHILDREN) != 0) parts.Add("WS_CLIPCHILDREN");
        if ((style & WS_CAPTION) != 0) parts.Add("WS_CAPTION");
        if ((style & WS_BORDER) != 0) parts.Add("WS_BORDER");
        return parts.Count == 0 ? "(none)" : string.Join("|", parts);
    }

    /// <summary>Human-readable list of the extended styles present in <paramref name="ex"/>.</summary>
    public static string DecodeExStyle(uint ex)
    {
        var parts = new System.Collections.Generic.List<string>();
        if ((ex & WS_EX_LAYERED) != 0) parts.Add("WS_EX_LAYERED");
        if ((ex & WS_EX_TRANSPARENT) != 0) parts.Add("WS_EX_TRANSPARENT");
        if ((ex & WS_EX_TOOLWINDOW) != 0) parts.Add("WS_EX_TOOLWINDOW");
        if ((ex & WS_EX_NOACTIVATE) != 0) parts.Add("WS_EX_NOACTIVATE");
        if ((ex & WS_EX_APPWINDOW) != 0) parts.Add("WS_EX_APPWINDOW");
        return parts.Count == 0 ? "(none)" : string.Join("|", parts);
    }
}
