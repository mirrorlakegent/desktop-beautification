using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using DesktopSuite.Wallpaper;

namespace DesktopSuite.Desktop.Organizer;

/// <summary>
/// The transparent, click-through container that hosts the fence boxes.
///
/// ARCHITECTURE (Milestone 1): a PLAIN Win32 child window of the desktop shell
/// (the same WorkerW surface the wallpaper host uses — see <c>WallpaperChildWindow</c>), painted as
/// a <b>layered</b> window (WS_EX_LAYERED) via <c>UpdateLayeredWindow</c> from an off-screen 32bpp
/// ARGB bitmap. This replaces:
///   (1) the WPF <see cref="System.Windows.Interop.HwndSource"/> approach — a WPF HwndSource child of
///       the layered desktop WorkerW is NOT composited by DWM, so the window was created (icons
///       correctly hidden) yet never painted and the desktop went blank;
///   (2) the short-lived Direct2D attempt (hand-rolled <c>D2D1CreateFactory</c> COM interop crashed
///       at factory creation); and
///   (3) a plain GDI+ <c>WM_PAINT</c> child — a NON-layered GDI self-drawn child of a layered parent
///       is also NOT composited (see <c>WallpaperChildWindow</c> lines 10-13).
/// Layered + <c>UpdateLayeredWindow</c> is the mechanism the wallpaper layer itself relies on, so it
/// is the reliable way to show content under the layered desktop WorkerW.
///
/// MILESTONE 1 SCOPE (static render + click-through, NO interaction):
///  - The window is created AS a child of the desktop host from the start (never re-parented),
///    exactly like <c>WallpaperChildWindow</c>, with WS_EX_LAYERED | WS_EX_NOACTIVATE.
///  - Rendering is GDI+ into an off-screen <see cref="Bitmap"/> (Format32bppArgb), pushed to the
///    layered window with <c>UpdateLayeredWindow</c> (see <see cref="UpdateVisual"/>).
///  - Click-through is delivered by <see cref="FenceNative.SetWindowRgn"/> (per-box union + the
///    "＋ 新建分类" tile), repointed at this window's pure-Win32 HWND. The layered window uses
///    constant-alpha (AlphaFormat = 0) so the bitmap is composited at full opacity and the hit region
///    is what punches the transparent, click-through gaps.
///  - Interaction (drag, rename, recategorize, collapse, add) is deferred to Milestone 2 / later.
///
/// MILESTONE 2 (ACTIVE, this build): <see cref="_diagFullWindow"/> is <c>false</c>. <see cref="UpdateVisual"/>
/// draws the REAL fence boxes (dark body RGB 20,22,28 + lighter header RGB 40,44,54, rounded corners,
/// white bold title, white item names) and the dashed "＋ 新建分类" tile from <see cref="_boxRects"/>.
/// Click-through gaps come from the SetWindowRgn hit region (ApplyRegion), not per-pixel alpha — this
/// avoids the GetHbitmap alpha-premultiply pitfall and keeps rendering robust.
///
/// DIAGNOSTIC (off by default): set <see cref="_diagFullWindow"/> to <c>true</c> to re-confirm layered
/// compositing — <see cref="UpdateVisual"/> then fills the ENTIRE bitmap dark and <see cref="ApplyRegion"/>
/// exposes the whole window. A full-screen dark surface proves the layered child is composited.
///
/// Layout model:
///  - The window is sized to the host's client rect and placed at (0,0).
///  - Box rectangles are stored in physical-pixel client coordinates (<see cref="_boxRects"/>), using
///    the SAME virtual-screen origin basis (<see cref="_virtualLeft"/>/<see cref="_virtualTop"/>) the
///    click-through region uses, so rendering and hit-testing always line up.
///
/// Known Phase limitations (unchanged, not regressed): DPI is approximately correct off 96-DPI;
/// multi-monitor clamping is deferred; no thumbnails; no Source heuristic (so 临时 stays empty).
/// </summary>
public sealed class FenceLayer
{
    // Logical (96-DPI) header height used for the click region when a box is collapsed.
    // Stored physical-pixel rects for the hit region + (Milestone 2) the real rendering.
    private readonly List<BoxRect> _boxRects = new();
    private (int Left, int Top, int Right, int Bottom)? _addTileRect;

    private FenceLayout? _layout;
    private double _dpiX = 1.0;
    private double _dpiY = 1.0;
    private double _virtualLeft;
    private double _virtualTop;

    // Pure-Win32 window state (replaces the old WPF HwndSource + Border/Canvas/FenceBox tree).
    private IntPtr _hwnd = IntPtr.Zero;
    private string? _className;
    private NativeMethods.WndProc _wndProc; // held alive: the OS keeps a raw pointer to it

    // Diagnostic full-window overlay: was ON to confirm layered compositing under the desktop WorkerW.
    // Milestone 2 is now ACTIVE — real per-box rendering is on and this is off. Flip to true only to
    // re-run the "full-screen dark" compositing check. Click-through is provided by SetWindowRgn
    // (per-box hit region) since the layered window uses constant-alpha (AlphaFormat = 0).
    private bool _diagFullWindow = false;
    private int _winW, _winH;

    private const double HeaderHeight = 28;
    private const double AddTileWidth = 160;
    private const double AddTileHeight = 48;

    public FenceLayer()
    {
        // Keep the delegate alive: the OS holds a raw pointer to it (see WallpaperChildWindow).
        _wndProc = WndProc;
    }

    /// <summary>Initial build entry (called once). Subsequent edits use the Refresh* methods below.</summary>
    public void Show(DesktopIconItem[] items, FenceLayout layout)
    {
        HostLog.Write($"FenceLayer.Show：called items={items.Length} categories={layout.Categories.Count}");
        _layout = layout;
        IntPtr hwnd = CreateDesktopWindow();
        HostLog.Write($"FenceLayer.Show：返回 hwnd=0x{hwnd.ToInt64():X}");
    }

    /// <summary>
    /// Create the desktop child window (plain Win32, layered + UpdateLayeredWindow rendered) and mount
    /// it under the desktop shell. The window is created AS a child of the desktop host — never
    /// re-parented — so DWM composites it the same way the wallpaper host is composited. Falls back
    /// gracefully if no host can be found (this build then does nothing visible, which is safe).
    /// </summary>
    private IntPtr CreateDesktopWindow()
    {
        // Re-entrancy guard: the desktop child window is created exactly once. The startup retry path
        // (EnableFences/ApplyFencesWithRetryIfEnabled) and a manual toggle race can otherwise both
        // reach here; without this, the second create overwrites _hwnd and the first HWND is leaked.
        if (_hwnd != IntPtr.Zero)
        {
            HostLog.Write($"FenceLayer.CreateDesktopWindow：跳过（_hwnd 已存在，防重入；hwnd=0x{_hwnd.ToInt64():X}）。");
            return _hwnd;
        }

        HostLog.Write("FenceLayer.CreateDesktopWindow：开始创建纯 Win32 桌面子窗口（layered）…");
        try
        {
            IntPtr host = ResolveDesktopHost();
            HostLog.Write($"FenceLayer.CreateDesktopWindow：ResolveDesktopHost => 0x{host.ToInt64():X}");
            if (host == IntPtr.Zero)
            {
                HostLog.Write("FenceLayer.CreateDesktopWindow：未找到桌面宿主，放弃挂载（仅顶层窗口回退不适用围栏）。");
                return IntPtr.Zero;
            }

            // Size to the host's client rect (physical px). The host is the full-screen desktop
            // container, so (0,0)+this size places the window exactly on the desktop.
            int w, h;
            if (host != IntPtr.Zero &&
                NativeMethods.GetClientRect(host, out var hcr) && hcr.Width > 0 && hcr.Height > 0)
            {
                w = hcr.Width;
                h = hcr.Height;
            }
            else
            {
                w = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
                h = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);
            }
            if (w <= 0) w = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
            if (h <= 0) h = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);
            _winW = w;
            _winH = h;
            HostLog.Write($"FenceLayer.CreateDesktopWindow：缓存窗口尺寸 _winW={_winW} _winH={_winH}");

            // ---- Plain-Win32 window creation, copied from WallpaperChildWindow (the PROVEN path) ----
            _className = $"FenceHost_{Guid.NewGuid():N}";
            _wndProc = WndProc; // keep the delegate alive for the OS-held raw pointer
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
                throw new Win32Exception(Marshal.GetLastWin32Error(), "RegisterClassEx failed.");

            // ---- PRIMARY PATH: WorkerW child WITHOUT WS_EX_LAYERED at create ----
            // Passing WS_EX_LAYERED in dwExStyle together with WS_CHILD is rejected at
            // CreateWindowEx time on Win11 in many configurations (root cause of the round-6
            // "CreateWindowEx failed" log). Apply the layered bit AFTER the window exists via
            // SetWindowLongPtrW + SetWindowPos(SWP_FRAMECHANGED) — the well-known layered+child
            // timing requirement.
            _hwnd = NativeMethods.CreateWindowEx(
                NativeMethods.WS_EX_NOACTIVATE,
                _className,
                "DesktopSuite Fence Host",
                NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE | NativeMethods.WS_CLIPSIBLINGS | NativeMethods.WS_CLIPCHILDREN,
                0, 0, w, h,
                host,
                IntPtr.Zero,
                hInstance,
                IntPtr.Zero);

            if (_hwnd != IntPtr.Zero && !ApplyLayeredStyle(_hwnd))
            {
                // Window created fine but the layered bit couldn't be applied → destroy and fall back.
                HostLog.Write("FenceLayer.CreateDesktopWindow：child 创建成功但补加 WS_EX_LAYERED 失败，销毁并回落");
                NativeMethods.DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
            }
            if (_hwnd == IntPtr.Zero)
            {
                // ---- FALLBACK PATH: top-level WS_POPUP layered (Rainmeter-style) ----
                // Does NOT depend on WorkerW parenting; positioned at (0,0,w,h) on the desktop plane.
                HostLog.Write("FenceLayer.CreateDesktopWindow：child 不可行，改顶层 WS_POPUP layered");
                _hwnd = NativeMethods.CreateWindowEx(
                    NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW,
                    _className,
                    "DesktopSuite Fence TopLevel",
                    FenceNative.WS_POPUP | NativeMethods.WS_VISIBLE,
                    0, 0, w, h,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    hInstance,
                    IntPtr.Zero);
                if (_hwnd == IntPtr.Zero)
                {
                    int err = Marshal.GetLastWin32Error();
                    string msg = err != 0 ? new Win32Exception(err).Message : "(no error code)";
                    HostLog.Write($"FenceLayer.CreateDesktopWindow：顶层 WS_POPUP layered 也失败 err={err} msg={msg}");
                    return IntPtr.Zero;
                }
                // Slide the top-level to the bottom of the desktop plane so icon-windows stay above.
                NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_BOTTOM, 0, 0, w, h,
                    NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
                HostLog.Write($"FenceLayer.CreateDesktopWindow：ok(顶层 WS_POPUP layered) hwnd=0x{_hwnd.ToInt64():X} size={w}x{h}");
            }
            else
            {
                // Pin the fence to the TOP of its desktop host so it sits above the wallpaper surface.
                NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOP, 0, 0, w, h,
                    NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
                HostLog.Write($"FenceLayer.CreateDesktopWindow：ok(WorkerW 子窗口 layered) host=0x{host.ToInt64():X} hwnd=0x{_hwnd.ToInt64():X} size={w}x{h}");
            }

            // DPI + virtual-screen origin (physical-px basis for box-region math).
            _dpiX = GetDpiScale();
            _dpiY = _dpiX;
            _virtualLeft = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
            _virtualTop = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);

            // Build layout (box rects) then clip + show, then push the first layered paint.
            BuildBoxes();
            ApplyRegion();
            UpdateVisual();
        }
        catch (Exception ex)
        {
            HostLog.Write("FenceLayer.CreateDesktopWindow 失败", ex);
        }
        return _hwnd;
    }

    /// <summary>Add WS_EX_LAYERED to an existing window via SetWindowLongPtrW + SWP_FRAMECHANGED
    /// (the well-known Win32 layered+child timing requirement: layered bit must be applied AFTER
    /// the window exists when combining with WS_CHILD, otherwise CreateWindowEx itself rejects the
    /// call). Returns true when the window's ex-style verifies as layered after the change.</summary>
    private static bool ApplyLayeredStyle(IntPtr h)
    {
        try
        {
            IntPtr prev = FenceNative.GetWindowLongPtrW(h, FenceNative.GWL_EXSTYLE);
            uint prevEx = (uint)prev.ToInt64();
            uint newEx = prevEx | NativeMethods.WS_EX_LAYERED;
            _ = FenceNative.SetWindowLongPtrW(h, FenceNative.GWL_EXSTYLE, (IntPtr)(uint)newEx);
            // SetWindowLongPtrW returns the previous value on success; cannot disambiguate from 0
            // alone when the previous value happened to be 0, so verify by reading back the ex-style.
            IntPtr after = FenceNative.GetWindowLongPtrW(h, FenceNative.GWL_EXSTYLE);
            uint afterEx = (uint)after.ToInt64();
            if ((afterEx & NativeMethods.WS_EX_LAYERED) == 0)
            {
                int err = Marshal.GetLastWin32Error();
                HostLog.Write($"FenceLayer.ApplyLayeredStyle：失败 SetWindowLongPtrW 后回读 ex=0x{afterEx:X} 不含 WS_EX_LAYERED err={err}");
                return false;
            }
            NativeMethods.SetWindowPos(h, IntPtr.Zero, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED);
            HostLog.Write($"FenceLayer.ApplyLayeredStyle：ok ex 0x{prevEx:X} -> 0x{afterEx:X}（含 WS_EX_LAYERED）");
            return true;
        }
        catch (Exception ex)
        {
            HostLog.Write("FenceLayer.ApplyLayeredStyle 失败", ex);
            return false;
        }
    }

    /// <summary>Compute per-box physical-pixel rects (and the add-tile rect) from the layout, then
    /// request a repaint + re-apply the click region. Replaces the old WPF Canvas tree build.</summary>
    private void BuildBoxes()
    {
        _boxRects.Clear();
        _addTileRect = null;
        if (_layout == null) return;

        // ---- Auto-layout: always compute positions (M2 has no drag-to-position yet) ----
        // Without this, FenceCategory X/Y/Width/Height default to 0 → ALL boxes stack at
        // (0,0). AutoLayoutGrid writes computed coordinates back into each category.
        // TODO: when drag-to-reposition is implemented, skip this if persisted coords exist.
        AutoLayoutGrid();

        double maxRight = 0;
        foreach (var cat in _layout.Categories)
        {
            int left = (int)Math.Round(cat.X - _virtualLeft);
            int top = (int)Math.Round(cat.Y - _virtualTop);
            double hLogical = cat.Collapsed ? HeaderHeight : cat.Height;
            int right = left + (int)Math.Round(cat.Width * _dpiX);
            int bottom = top + (int)Math.Round(hLogical * _dpiY);
            var names = new List<string>();
            foreach (var p in cat.MemberPaths)
            {
                var n = Path.GetFileName(p);
                if (!string.IsNullOrWhiteSpace(n)) names.Add(n);
            }
            _boxRects.Add(new BoxRect(left, top, right, bottom, cat.DisplayName, cat.Collapsed,
                cat.IconRef, names));
            maxRight = Math.Max(maxRight, cat.X + cat.Width);
        }

        // Add-tile sits to the right of the rightmost box (same placement the old WPF tile used).
        double y = _layout.Categories.Count > 0 ? _layout.Categories[0].Y : _virtualTop;
        int tl = (int)Math.Round((maxRight + 30) - _virtualLeft);
        int tt = (int)Math.Round(y - _virtualTop);
        int tr = tl + (int)Math.Round(AddTileWidth * _dpiX);
        int tb = tt + (int)Math.Round(AddTileHeight * _dpiY);
        _addTileRect = (tl, tt, tr, tb);
    }

    /// <summary>
    /// Auto-layout: spread categories into a responsive grid across the desktop work area.
    /// Called when persisted X/Y/Width/Height are missing or zero (first run / no saved layout).
    /// Writes computed physical-pixel coordinates back into each <see cref="FenceCategory"/> so
    /// <see cref="BuildBoxes"/> and persistence both use the same values.
    /// </summary>
    private void AutoLayoutGrid()
    {
        if (_layout == null || _layout.Categories.Count == 0) return;

        int cols = _layout.Categories.Count <= 2 ? 1
            : _layout.Categories.Count <= 4 ? 2
            : 3;
        int rows = (int)Math.Ceiling((double)_layout.Categories.Count / cols);

        // Physical-pixel margins and gaps (scaled by DPI).
        double marginX = 40 * _dpiX;
        double marginY = 30 * _dpiY;
        double gapX = 20 * _dpiX;
        double gapY = 16 * _dpiY;

        // Available area for the grid (leave margins on all sides).
        double availW = Math.Max(_winW - marginX * 2, 200 * _dpiX);
        double availH = Math.Max(_winH - marginY * 2, 200 * _dpiY);

        double boxW = (availW - gapX * (cols - 1)) / cols;
        // Clamp box width to a reasonable range.
        boxW = Math.Clamp(boxW, 180 * _dpiX, 360 * _dpiX);

        const double rowHLogical = 220; // logical px per row at 96 DPI
        double rowH = rowHLogical * _dpiY;
        // Don't exceed available height per row.
        rowH = Math.Min(rowH, (availH - gapY * (rows - 1)) / Math.Max(rows, 1));

        double baseX = _virtualLeft + marginX;
        double baseY = _virtualTop + marginY;

        for (int i = 0; i < _layout.Categories.Count; i++)
        {
            var cat = _layout.Categories[i];
            int col = i % cols;
            int row = i / cols;

            cat.X = baseX + col * (boxW + gapX);
            cat.Y = baseY + row * (rowH + gapY);
            cat.Width = boxW / _dpiX; // store as logical (will be multiplied by dpiX in BuildBoxes)

            // Height: header + items (taller for more items).
            int itemCount = cat.MemberPaths?.Count ?? 0;
            double itemAreaH = Math.Min(itemCount, 8) * (20 * _dpiY); // max 8 visible items
            double bodyH = (8 + itemAreaH + 24) * _dpiY; // padding top/bottom
            cat.Height = Math.Max(bodyH / _dpiY, rowH / _dpiY); // store as logical
        }

        HostLog.Write($"FenceLayer.AutoLayoutGrid：{_layout.Categories.Count} 个分类 → {cols}列×{rows}行 网格布局 boxW={boxW:F0}px rowH={rowH:F0}px");
    }

    // ---- Win32 window procedure ----

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        const uint WM_PAINT = 0x000F;
        const uint WM_SIZE = 0x0005;
        const uint WM_DESTROY = 0x0002;
        const uint WM_DISPLAYCHANGE = 0x007E;
        const uint WM_DPICHANGED = 0x02E0;

        switch (msg)
        {
            case WM_PAINT:
            {
                // Layered windows are painted exclusively via UpdateLayeredWindow — there is nothing
                // to draw here. Just validate the paint region so the OS stops sending WM_PAINT.
                NativeMethods.BeginPaint(hWnd, out var ps);
                NativeMethods.EndPaint(hWnd, ref ps);
                return IntPtr.Zero;
            }
            case WM_SIZE:
            {
                int nw = (int)(lParam & 0xFFFF);
                int nh = (int)((lParam >> 16) & 0xFFFF);
                if (nw > 0 && nh > 0)
                {
                    _winW = nw;
                    _winH = nh;
                    UpdateVisual();
                }
                return IntPtr.Zero;
            }
            case WM_DISPLAYCHANGE:
            case WM_DPICHANGED:
                OnDisplayOrDpiChange();
                return IntPtr.Zero;
            case WM_DESTROY:
                // Teardown is driven by Close(); do not unregister the class here.
                return IntPtr.Zero;
            default:
                return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
        }
    }

    /// <summary>Render the fence surface into an off-screen 32bppARGB bitmap and push it to the
    /// layered window via <c>UpdateLayeredWindow</c>. This is what actually makes content visible —
    /// a layered window is NOT painted through WM_PAINT/BeginPaint; it is composited from the bitmap
    /// we supply. Milestone 2 draws the real per-box rectangles (see <see cref="DrawBoxes"/>) on a
    /// transparent-cleared bitmap; the click-through gaps are then delivered by the SetWindowRgn hit
    /// region (see <see cref="ApplyRegion"/>). The diagnostic mode (<see cref="_diagFullWindow"/>) fills
    /// the whole bitmap dark instead.</summary>
    private void UpdateVisual()
    {
        if (_hwnd == IntPtr.Zero || _winW <= 0 || _winH <= 0) return;

        IntPtr hdcScreen = IntPtr.Zero;
        IntPtr hdcMem = IntPtr.Zero;
        IntPtr hBmp = IntPtr.Zero;
        IntPtr hBmpOld = IntPtr.Zero;
        try
        {
            using var bmp = new Bitmap(_winW, _winH, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                // Transparent base (alpha 0). The layered window uses constant-alpha (AlphaFormat = 0,
                // SourceConstantAlpha = 255) for Milestone 2, so the bitmap's own alpha is ignored and the
                // whole window would be opaque black — UNLESS the click region (SetWindowRgn, per-box union
                // in ApplyRegion) clips the window to the boxes. Click-through = region gaps; visible =
                // drawn boxes. Drawing only boxes on a transparent-cleared bitmap keeps it simple/robust.
                g.Clear(Color.FromArgb(0, 0, 0, 0));
                if (_diagFullWindow)
                {
                    // Opaque dark fill of the ENTIRE bitmap (RGB 20,22,28). Used only to re-confirm the
                    // layered child is composited under the desktop WorkerW.
                    using var brush = new SolidBrush(Color.FromArgb(255, 20, 22, 28));
                    g.FillRectangle(brush, 0, 0, _winW, _winH);
                }
                else
                {
                    // Milestone 2: draw the real fence boxes (body + header + titles + items) and the
                    // "＋ 新建分类" tile from _boxRects.
                    DrawBoxes(g);
                }
            }

            hdcScreen = NativeMethods.GetDC(IntPtr.Zero);
            hdcMem = NativeMethods.CreateCompatibleDC(hdcScreen);
            if (hdcMem == IntPtr.Zero) return;

            hBmp = bmp.GetHbitmap();
            if (hBmp == IntPtr.Zero) return;
            hBmpOld = NativeMethods.SelectObject(hdcMem, hBmp);

            // Screen position of this window (a child of the desktop host). Use the real window rect
            // so any parent offset is honoured exactly.
            NativeMethods.GetWindowRect(_hwnd, out var wr);
            var pDst = new NativeMethods.POINT { X = wr.Left, Y = wr.Top };
            var pSrc = new NativeMethods.POINT { X = 0, Y = 0 };
            var size = new NativeMethods.SIZE { cx = _winW, cy = _winH };
            var blend = new NativeMethods.BLENDFUNCTION
            {
                BlendOp = NativeMethods.AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                // Constant-alpha (AlphaFormat = 0): the whole window is composited at full opacity, and
                // click-through is delivered by the SetWindowRgn hit region (per-box union in ApplyRegion)
                // which clips the window to the boxes. This avoids the GetHbitmap alpha-premultiply pitfall
                // of per-pixel alpha, keeping Milestone 2 rendering robust.
                AlphaFormat = 0
            };

            bool ok = NativeMethods.UpdateLayeredWindow(
                _hwnd, IntPtr.Zero, ref pDst, ref size, hdcMem, ref pSrc, 0, ref blend, NativeMethods.ULW_ALPHA);
            HostLog.Write(ok
                ? $"FenceLayer.UpdateVisual：ok size={_winW}x{_winH} diag={_diagFullWindow}"
                : $"FenceLayer.UpdateVisual：UpdateLayeredWindow 失败 err={Marshal.GetLastWin32Error()}");
        }
        catch (Exception ex)
        {
            HostLog.Write("FenceLayer.UpdateVisual 失败", ex);
        }
        finally
        {
            if (hdcScreen != IntPtr.Zero) NativeMethods.ReleaseDC(IntPtr.Zero, hdcScreen);
            if (hBmpOld != IntPtr.Zero && hdcMem != IntPtr.Zero) NativeMethods.SelectObject(hdcMem, hBmpOld);
            if (hBmp != IntPtr.Zero) NativeMethods.DeleteObject(hBmp);
            if (hdcMem != IntPtr.Zero) NativeMethods.DeleteDC(hdcMem);
        }
    }

    /// <summary>Milestone 2: paint the real fence boxes + the "＋ 新建分类" tile into the off-screen
    /// bitmap. Box geometry comes from <see cref="_boxRects"/> (physical px, client coords — the same
    /// basis <see cref="ApplyRegion"/> uses for the click-through region), so rendering and hit-testing
    /// always line up. Boxes: dark body (RGB 20,22,28) + lighter header (RGB 40,44,54), rounded corners,
    /// white bold title, white item names. Click-through is NOT done here (the bitmap is cleared
    /// transparent) — it is delivered by the SetWindowRgn hit region.</summary>
    private void DrawBoxes(Graphics g)
    {
        int r = (int)Math.Round(10 * _dpiX);
        int pad = (int)Math.Round(12 * _dpiX);
        int hh = (int)Math.Round(HeaderHeight * _dpiY);

        using var bodyBrush = new SolidBrush(Color.FromArgb(255, 20, 22, 28));
        using var headerBrush = new SolidBrush(Color.FromArgb(255, 40, 44, 54));
        using var borderPen = new Pen(Color.FromArgb(255, 64, 70, 86), 1);
        using var titleBrush = new SolidBrush(Color.FromArgb(255, 255, 255, 255));
        using var itemBrush = new SolidBrush(Color.FromArgb(235, 210, 214, 222));
        using var titleFont = new Font("Segoe UI", (float)(13 * _dpiY), FontStyle.Bold);
        using var itemFont = new Font("Segoe UI", (float)(12 * _dpiY), FontStyle.Regular);
        using var sf = new StringFormat(StringFormatFlags.NoWrap)
        {
            Alignment = StringAlignment.Near,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter
        };

        foreach (var b in _boxRects)
        {
            int w = b.Right - b.Left;
            int h = b.Bottom - b.Top;
            if (w <= 0 || h <= 0) continue;

            // Body (all corners rounded) then header (top corners rounded only).
            FillRoundedRect(g, bodyBrush, b.Left, b.Top, w, h, r);
            int headerH = Math.Min(hh, h);
            FillHeaderPath(g, headerBrush, b.Left, b.Top, w, headerH, r);

            // Title (with optional emoji glyph).
            string title = string.IsNullOrEmpty(b.Name) ? "未命名" : b.Name;
            if (!string.IsNullOrEmpty(b.IconRef)) title = b.IconRef + " " + title;
            g.DrawString(title, titleFont, titleBrush,
                new RectangleF(b.Left + pad, b.Top, w - pad * 2, headerH), sf);

            // Item names (skipped when the box is collapsed).
            if (!b.Collapsed)
            {
                float lineH = (float)(20 * _dpiY);
                float y = b.Top + headerH + (float)(8 * _dpiY);
                int maxItems = (int)((h - headerH - 16 * _dpiY) / lineH);
                int shown = 0;
                foreach (var it in b.Items)
                {
                    if (shown >= maxItems) break;
                    g.DrawString(it, itemFont, itemBrush,
                        new RectangleF(b.Left + pad, y, w - pad * 2, lineH), sf);
                    y += lineH;
                    shown++;
                }
                if (b.Items.Count > maxItems && maxItems > 0)
                {
                    g.DrawString($"… 还有 {b.Items.Count - maxItems} 项", itemFont, itemBrush,
                        new RectangleF(b.Left + pad, y, w - pad * 2, lineH), sf);
                }
            }

            // 1px outline for definition.
            using var borderPath = RoundedRectPath(b.Left, b.Top, w, h, r);
            g.DrawPath(borderPen, borderPath);
        }

        // "＋ 新建分类" tile to the right of the rightmost box.
        if (_addTileRect.HasValue)
        {
            var t = _addTileRect.Value;
            int tw = t.Right - t.Left, th = t.Bottom - t.Top;
            using var tilePen = new Pen(Color.FromArgb(160, 90, 96, 112), 1.5f) { DashStyle = DashStyle.Dash };
            using var tilePath = RoundedRectPath(t.Left, t.Top, tw, th, r);
            using var tileBrush = new SolidBrush(Color.FromArgb(220, 150, 156, 172));
            using var tileFont = new Font("Segoe UI", (float)(12 * _dpiY), FontStyle.Regular);
            g.DrawPath(tilePen, tilePath);
            g.DrawString("＋ 新建分类", tileFont, tileBrush,
                new RectangleF(t.Left, t.Top, tw, th),
                new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
        }
    }

    /// <summary>Rounded-rect <see cref="GraphicsPath"/> (all four corners).</summary>
    private static GraphicsPath RoundedRectPath(int x, int y, int w, int h, int r)
    {
        r = Math.Min(r, Math.Min(w, h) / 2);
        var path = new GraphicsPath();
        path.AddArc(x, y, r, r, 180, 90);
        path.AddArc(x + w - r, y, r, r, 270, 90);
        path.AddArc(x + w - r, y + h - r, r, r, 0, 90);
        path.AddArc(x, y + h - r, r, r, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static void FillRoundedRect(Graphics g, Brush brush, int x, int y, int w, int h, int r)
    {
        using var path = RoundedRectPath(x, y, w, h, r);
        g.FillPath(brush, path);
    }

    /// <summary>Header fill: top corners rounded, bottom edge square (sits flush on the body).</summary>
    private static void FillHeaderPath(Graphics g, Brush brush, int x, int y, int w, int h, int r)
    {
        r = Math.Min(r, w / 2);
        using var path = new GraphicsPath();
        path.AddArc(x, y, r, r, 180, 90);
        path.AddArc(x + w - r, y, r, r, 270, 90);
        path.AddLine(x + w, y + r, x + w, y + h);
        path.AddLine(x + w, y + h, x, y + h);
        path.AddLine(x, y + h, x, y + r);
        path.CloseFigure();
        g.FillPath(brush, path);
    }

    /// <summary>Resize + rebuild + re-region on display / DPI changes so boxes stay aligned and the
    /// click-through stays correct.</summary>
    private void OnDisplayOrDpiChange()
    {
        try
        {
            IntPtr host = ResolveDesktopHost();
            int w, h;
            if (host != IntPtr.Zero &&
                NativeMethods.GetClientRect(host, out var hcr) && hcr.Width > 0 && hcr.Height > 0)
            {
                w = hcr.Width;
                h = hcr.Height;
            }
            else
            {
                w = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
                h = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);
            }
            _winW = w;
            _winH = h;
            NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOP, 0, 0, w, h,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);

            _dpiX = GetDpiScale();
            _dpiY = _dpiX;
            _virtualLeft = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
            _virtualTop = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);

            BuildBoxes();
            ApplyRegion();
            UpdateVisual();
        }
        catch (Exception ex)
        {
            HostLog.Write("FenceLayer 显示/DPI 变化处理失败", ex);
        }
    }

    /// <summary>Destroy the desktop child window. Safe to call multiple times.</summary>
    public void Close()
    {
        try
        {
            if (_hwnd != IntPtr.Zero)
            {
                NativeMethods.DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
            }
            if (_className != null)
            {
                try
                {
                    NativeMethods.UnregisterClass(_className, NativeMethods.GetModuleHandle(null));
                }
                catch { /* class may already be gone; ignore */ }
                _className = null;
            }
        }
        catch (Exception ex)
        {
            HostLog.Write("FenceLayer.Close 失败", ex);
        }
    }

    /// <summary>Per-monitor DPI scale (DPI/96) for this window, used by the box-region math. Falls back
    /// to 1.0 (96 DPI) when the API is unavailable or the process is not DPI-aware.</summary>
    private double GetDpiScale()
    {
        try
        {
            if (_hwnd != IntPtr.Zero)
            {
                uint dpi = FenceNative.GetDpiForWindow(_hwnd);
                if (dpi > 0) return dpi / 96.0;
            }
        }
        catch { }
        return 1.0;
    }

    /// <summary>Locate the desktop host window. Mount under the SAME desktop surface WorkerW the
    /// wallpaper uses — in the WorkerW shell shape a window parented directly to Progman is NOT
    /// composited by DWM (only DefView and the wallpaper WorkerW render), so parenting to the
    /// wallpaper WorkerW is what makes the FenceLayer actually show. Progman is the fallback.</summary>
    private static IntPtr ResolveDesktopHost()
    {
        try
        {
            if (WorkerWHost.TryFindWallpaperSurface(out IntPtr workerW) && workerW != IntPtr.Zero)
                return workerW;
            IntPtr progman = NativeMethods.FindWindow("Progman", null);
            if (progman != IntPtr.Zero) return progman;
        }
        catch (Exception ex)
        {
            HostLog.Write("FenceLayer.ResolveDesktopHost 失败", ex);
        }
        return IntPtr.Zero;
    }

    /// <summary>Union of all box rectangles (header-only when collapsed) as the hit region → click-through
    /// everywhere else. Coordinates are physical pixels relative to the window's client origin, using the
    /// SAME virtual-origin basis (<see cref="_virtualLeft"/>/<see cref="_virtualTop"/>) the rendering uses,
    /// so the region always lines up with the boxes regardless of monitor layout.</summary>
    private void ApplyRegion()
    {
        try
        {
            // ---- DIAGNOSTIC OVERLAY ----
            // When on, skip per-box clipping entirely and expose the WHOLE window so a real-machine run
            // can tell "rendered but region-clipped" (full-screen dark surface) from "never painted"
            // (still nothing). Normal click-through region logic runs only when off.
            if (_diagFullWindow)
            {
                if (_hwnd != IntPtr.Zero && _winW > 0 && _winH > 0)
                {
                    IntPtr rgn = FenceNative.CreateRectRgn(0, 0, _winW, _winH);
                    if (rgn != IntPtr.Zero)
                    {
                        FenceNative.SetWindowRgn(_hwnd, rgn, true);
                        HostLog.Write("FenceLayer.ApplyRegion DIAG: full-window visible region set");
                        return;
                    }
                }
                HostLog.Write($"FenceLayer.ApplyRegion DIAG: 跳过（_hwnd=0x{_hwnd.ToInt64():X} _winW={_winW} _winH={_winH}），回落到正常 region。");
            }

            if (_hwnd == IntPtr.Zero) return;

            IntPtr? combined = null;
            foreach (var b in _boxRects)
            {
                IntPtr rgn = FenceNative.CreateRectRgn(b.Left, b.Top, b.Right, b.Bottom);
                if (rgn == IntPtr.Zero) continue;
                if (combined == null)
                    combined = rgn;
                else
                {
                    FenceNative.CombineRgn(combined.Value, combined.Value, rgn, FenceNative.RGN_OR);
                    FenceNative.DeleteObject(rgn);
                }
            }

            // Add the "＋ 新建分类" tile to the hit region so it is both visible AND clickable.
            if (_addTileRect.HasValue)
            {
                var t = _addTileRect.Value;
                IntPtr trgn = FenceNative.CreateRectRgn(t.Left, t.Top, t.Right, t.Bottom);
                if (trgn != IntPtr.Zero)
                {
                    if (combined == null) combined = trgn;
                    else
                    {
                        FenceNative.CombineRgn(combined.Value, combined.Value, trgn, FenceNative.RGN_OR);
                        FenceNative.DeleteObject(trgn);
                    }
                }
            }

            if (combined != null)
            {
                FenceNative.SetWindowRgn(_hwnd, combined.Value, true);
                // SetWindowRgn takes ownership of the region; do not DeleteObject(combined).
                HostLog.Write($"FenceLayer.ApplyRegion：boxes={_boxRects.Count} addTile={_addTileRect.HasValue} → 命中区已应用");
            }
            else
            {
                // No boxes: make the window fully transparent + click-through (empty region) instead of
                // leaving a full-screen opaque surface. An empty region clips the whole window away.
                IntPtr empty = FenceNative.CreateRectRgn(0, 0, 0, 0);
                if (empty != IntPtr.Zero) FenceNative.SetWindowRgn(_hwnd, empty, true);
                HostLog.Write($"FenceLayer.ApplyRegion：无命中区（combined==null），回退全透明点击穿透 winW={_winW} winH={_winH}");
            }
        }
        catch (Exception ex)
        {
            HostLog.Write("FenceLayer.ApplyRegion 失败", ex);
        }
    }

    /// <summary>Physical-pixel box rectangle (client coords) used for both the click region and
    /// (Milestone 2) the layered-bitmap rendering via <see cref="UpdateVisual"/>.</summary>
    private readonly struct BoxRect
    {
        public int Left { get; }
        public int Top { get; }
        public int Right { get; }
        public int Bottom { get; }
        public string Name { get; }
        public bool Collapsed { get; }
        public string? IconRef { get; }
        public List<string> Items { get; }

        public BoxRect(int left, int top, int right, int bottom, string name, bool collapsed,
            string? iconRef, List<string> items)
        {
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
            Name = name;
            Collapsed = collapsed;
            IconRef = iconRef;
            Items = items;
        }
    }
}
