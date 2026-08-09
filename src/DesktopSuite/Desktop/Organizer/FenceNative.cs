using System;
using System.Runtime.InteropServices;

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

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    public static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    public const int GWL_EXSTYLE = -20;
    public const uint WS_EX_TOOLWINDOW = 0x00000080;
    public const uint WS_EX_NOACTIVATE = 0x08000000;

    /// <summary>RGN_OR — union of the two regions (used to merge all box rectangles).</summary>
    public const int RGN_OR = 2;
}
