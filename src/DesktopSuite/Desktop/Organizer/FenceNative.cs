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

    /// <summary>System (primary-monitor) DPI, available without a window handle. Returns 0 on
    /// pre-Win10 — callers fall back to 96.</summary>
    [DllImport("user32.dll")]
    public static extern uint GetDpiForSystem();

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
    public const uint SWP_SHOWWINDOW = 0x0040;
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
    public const uint WS_THICKFRAME = 0x00040000;  // resizable border (for user drag-resize)

    // Extended window styles (GWL_EXSTYLE) — WS_EX_TOOLWINDOW / WS_EX_NOACTIVATE defined above
    public const uint WS_EX_LAYERED = 0x00080000;
    public const uint WS_EX_TRANSPARENT = 0x00000020;
    public const uint WS_EX_TOPMOST = 0x00000008;
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

    // ---- M3 interaction surface (mouse, capture, context menu) ----

    // Window-class style bit: enables WM_LBUTTONDBLCLK (otherwise double-clicks are coalesced
    // into two WM_LBUTTONDOWN and the double-click message never fires).
    public const uint CS_DBLCLKS = 0x0008;

    // Mouse messages
    public const uint WM_MOUSEMOVE = 0x0200;
    public const uint WM_LBUTTONDOWN = 0x0201;
    public const uint WM_LBUTTONUP = 0x0202;
    public const uint WM_LBUTTONDBLCLK = 0x0203;
    public const uint WM_RBUTTONDOWN = 0x0204;
    public const uint WM_RBUTTONUP = 0x0205;
    public const uint WM_CONTEXTMENU = 0x007B;
    public const uint WM_CAPTURECHANGED = 0x0215;
    public const uint WM_COMMAND = 0x0111;
    public const uint WM_NCHITTEST = 0x0084;
    public const uint WM_SETCURSOR = 0x0020;

    // System cursor IDs (LoadCursor(NULL, id) — shared handles, never DestroyCursor'd).
    public const int IDC_ARROW = 32512;
    public const int IDC_HAND = 32649;
    public const int IDC_SIZEALL = 32646;     // 4-way move arrow (drag box)
    public const int IDC_SIZENWSE = 32642;    // diagonal resize (bottom-right corner)

    // NCHITTEST return values — enable resize on edges/corners, HTCLIENT for interior (drag-move)
    public const int HTNOWHERE = 0;
    public const int HTCLIENT = 1;
    public const int HTLEFT = 10;
    public const int HTRIGHT = 11;
    public const int HTTOP = 12;
    public const int HTTOPLEFT = 13;
    public const int HTTOPRIGHT = 14;
    public const int HTBOTTOM = 15;
    public const int HTBOTTOMLEFT = 16;
    public const int HTBOTTOMRIGHT = 17;

    public const uint MK_LBUTTON = 0x0001;

    [DllImport("user32.dll")]
    public static extern IntPtr SetCapture(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    public static extern IntPtr GetCapture();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);

    [DllImport("user32.dll")]
    public static extern IntPtr SetCursor(IntPtr hCursor);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool AppendMenu(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string lpNewItem);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hWnd, IntPtr lpTPMParams);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyMenu(IntPtr hMenu);

    // TrackPopupMenuEx flags
    public const uint TPM_RETURNCMD = 0x0100;
    public const uint TPM_RIGHTBUTTON = 0x0002;
    public const uint TPM_NONOTIFY = 0x0080;

    // ---- M3 desktop-icon drag-drop detector surface ----
    // Drop proxy z-order / visibility
    public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    public const int SW_HIDE = 0;
    public const int SW_SHOW = 5;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    public const int VK_LBUTTON = 0x01;

    // ListView messages (used to hit-test desktop icons)
    public const uint LVM_FIRST = 0x1000;
    public const uint LVM_HITTEST = LVM_FIRST + 18; // 0x1012
    public const uint LVHT_ONITEM = 0x0000000E;   // icon | label | state icon

    // AppendMenu flags
    public const uint MF_STRING = 0x0000;
    public const uint MF_GRAYED = 0x0001;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    /// <summary>Extract the client-x from an LPARAM (low 16 bits, sign-extended).</summary>
    public static int GET_X_LPARAM(IntPtr lParam) => (short)(lParam.ToInt64() & 0xFFFF);

    /// <summary>Extract the client-y from an LPARAM (high 16 bits, sign-extended).</summary>
    public static int GET_Y_LPARAM(IntPtr lParam) => (short)((lParam.ToInt64() >> 16) & 0xFFFF);

    // ---- Shell icon extraction (for fence item display) ----

    public const uint SHGFI_ICON = 0x000000100;
    public const uint SHGFI_SMALLICON = 0x000000001;
    public const uint SHGFI_LARGEICON = 0x000000000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    public struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
        ref SHFILEINFO psfi, uint cbSizeFileInfo, uint uFlags);

    // ---- SHGetStockIconInfo (Vista+) for system icons that SHGetFileInfo can't resolve ----
    public const int SIID_COMPUTER = 15;       // 此电脑 (This PC)
    public const int SIID_RECYCLEBIN = 48;      // 回收站 (Recycle Bin)
    public const int SIID_CONTROLPANEL = 23;     // 控制面板 (Control Panel)

    public const uint SHGSI_ICON         = 0x00000080;  // retrieve the HICON (REQUIRED to get hIcon)
    public const uint SHGSI_ICONLOCATION = 0x00000100; // fill szPath / iSysImageIndex
    public const uint SHGSI_LARGEICON     = 0x00000000;  // large (48px at 96 DPI)
    public const uint SHGSI_SMALLICON     = 0x00000001;  // small (16px)
    public const uint SHGSI_LINKOVERLAY   = 0x00000200;  // add arrow overlay

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SHSTOCKICONINFO
    {
        public int cbSize;
        public IntPtr hIcon;          // HICON — caller must DestroyIcon()
        public int iSysImageIndex;
        public int iIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szPath;
    }

    [DllImport("shell32.dll", SetLastError = true)]
    public static extern int SHGetStockIconInfo(int siid, uint uFlags, out SHSTOCKICONINFO psii);

    /// <summary>Destroy an icon handle returned by SHGetFileInfo / SHGetStockIconInfo. The caller
    /// owns these handles and must release them; we copy the pixels to a managed Bitmap first so the
    /// HICON lifetime is irrelevant after this call.</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>Extract icons from an executable/DLL by resource index. Returns the number of icons extracted.
    /// phiconLarge/phiconSmall receive HICON handles — caller must DestroyIcon each non-zero handle.
    /// This is the most reliable way to get system stock icons when SHGetStockIconInfo fails
    /// (e.g., in layered desktop-child window contexts where shell COM may not be fully initialized).</summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int ExtractIconEx(string szFileName, int nIconIndex,
        out IntPtr phiconLarge, out IntPtr phiconSmall, uint nIcons);
}
