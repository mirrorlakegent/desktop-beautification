using System;
using System.Text;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;
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

    // ---- M3.30: hide/show overlay + idle auto-hide ----
    // The overlay can be hidden (collapsed to nothing) on demand — double-click empty desktop,
    // idle timeout, or tray icon — without tearing down the window. ShowWindow(SW_HIDE) makes the
    // layered child fully invisible and click-through; SW_SHOW restores it.
    private bool _hidden;
    private DateTime _lastActivityUtc = DateTime.UtcNow;

    // Diagnostic full-window overlay: was ON to confirm layered compositing under the desktop WorkerW.
    // Milestone 2 is now ACTIVE — real per-box rendering is on and this is off. Flip to true only to
    // re-run the "full-screen dark" compositing check. Click-through is provided by SetWindowRgn
    // (per-box hit region) since the layered window uses constant-alpha (AlphaFormat = 0).
    private bool _diagFullWindow = false;
    private int _winW, _winH;

    // ---- M4-B: user-tunable box appearance ----
    private FenceAppearance _appearance = new();
    private Bitmap? _frostBmp;          // cached frosted backdrop (screen capture, blurred) — reused per session
    private Bitmap? _alphaFillTmp;      // reusable offscreen for the ColorMatrix alpha-fill workaround

    // ---- M3 interaction state ----
    private FenceCategory? _dragCat;
    private int _dragOffsetX, _dragOffsetY;
    private int _dragBoxIndex = -1;       // box index of the box currently being moved (for reorder swap)
    private double _dragOrigX, _dragOrigY; // saved cat.X/Y BEFORE drag started (for clean reorder swap)
    private DateTime _lastDragPaint = DateTime.MinValue;

    // ---- Undo support for accidental category deletion ----
    // Each deletion snapshots the removed category (geometry + members) plus the paths re-homed into
    // 未分类, so a mistaken delete can be fully restored. A small stack keeps the last few deletions.
    private readonly List<UndoEntry> _undoStack = new();
    private const int MaxUndoEntries = 10;
    public event Action? UndoStateChanged;

    private sealed record UndoEntry(FenceCategory Category, int OriginalIndex, List<string> MovedPaths);

    // Custom box-resize state (layered+noactivate windows can't use WS_THICKFRAME reliably).
    // _resizeDir: 0=none, 3=box bottom-right corner drag.
    private int _resizeDir;
    private FenceCategory? _resizeCat;
    private int _resizeStartX, _resizeStartY;
    private double _resizeStartW, _resizeStartH;

    // Item drag between categories (Fences-style reorganize).
    // _pendingItem is armed on item press but NOT yet a drag (no capture) so a plain click /
    // double-click still works. Once the pointer moves past DragThreshold it promotes to _dragItemCat.
    private (int BoxIndex, int ItemIndex)? _pendingItem;
    private int _pendingItemStartX, _pendingItemStartY;
    private FenceCategory? _dragItemCat;
    private int _dragItemIndex = -1;
    private int _dragItemX, _dragItemY;      // cursor position during item drag (for ghost rendering)
    private int _itemDropTarget = -1;     // box index currently highlighted as the drop target
    private int _itemDropSlot = -1;       // grid insertion slot (0-based) inside the drop target box
    private const int DragThreshold = 4;  // px before a press becomes a drag

    /// <summary>Shell icon cache: full path → GDI+ Icon (small, 16×16 at 96 DPI). Avoids repeated
    /// SHGetFileInfo calls per paint cycle. Cleared on DPI/layout changes.</summary>
    private readonly System.Collections.Generic.Dictionary<string, System.Drawing.Bitmap> _iconCache = new();

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

        // M3: desktop-icon → box import.
        // Implemented via the right-click "Import desktop icons" menu (OnContextMenu) — imports both the
        // per-user and public Desktop directories plus the 3 system virtual icons (此电脑/回收站/控制面板).
        // An earlier full-screen TOPMOST proxy-window approach (OLE drop) was abandoned: it flashed
        // opaque-white under the Explorer icon layer and never reliably received the drop.
    }

    /// <summary>Currently displayed layout (in-memory source of truth). Null before <see cref="Show"/>.</summary>
    public FenceLayout? CurrentLayout => _layout;

    /// <summary>Replace the live layout wholesale (used by layout import, M4-A). Rebuilds boxes, the
    /// hit-region and repaints. The desktop window itself is reused (create-once invariant preserved).</summary>
    public void ApplyLayout(FenceLayout layout)
    {
        if (layout == null) return;
        _layout = layout;
        BuildBoxes();
        ApplyRegion();
        UpdateVisual();
        HostLog.Write($"FenceLayer.ApplyLayout：已套用导入布局，分类数={layout.Categories.Count}");
    }

    /// <summary>M4-B: apply user-tunable box appearance (corner radius, body/header opacity, title
    /// font size, title alignment, glyph toggle, frosted). Repaints and re-regions so the click
    /// hit-area matches the (rounded) visual. Safe to call any time after <see cref="Show"/>.</summary>
    public void SetAppearance(FenceAppearance appearance)
    {
        if (appearance == null) return;
        // Defense-in-depth: clamp to valid ranges (0-255). 0 is allowed — it means "fully
        // transparent" and the renderer skips the fill when opacity <= 3. This lets users achieve
        // true transparency instead of being forced to a minimum that triggers GDI+ low-alpha bugs.
        var safe = appearance.Clone();
        safe.CornerRadius =   Math.Clamp(safe.CornerRadius,   0, 40);
        safe.BodyOpacity =    Math.Clamp(safe.BodyOpacity,    0, 255);
        safe.HeaderOpacity =  Math.Clamp(safe.HeaderOpacity,  0, 255);
        safe.TitleFontSize =  Math.Clamp(safe.TitleFontSize,  8f, 28f);
        safe.TitleAlign =     Math.Clamp(safe.TitleAlign,     0, 1);
        safe.FrostOpacity =   Math.Clamp(safe.FrostOpacity,   0, 200);

        bool frostedWasOn = _appearance.Frosted;
        _appearance = safe;
        // Turning frosted OFF (or changing the radius) invalidates the cached backdrop capture.
        if (!_appearance.Frosted || _appearance.CornerRadius != appearance.CornerRadius)
            InvalidateFrost();
        ApplyRegion();
        UpdateVisual();
        HostLog.Write($"FenceLayer.SetAppearance：圆角={_appearance.CornerRadius} 体透明度={_appearance.BodyOpacity} 头透明度={_appearance.HeaderOpacity} 标题字号={_appearance.TitleFontSize:F0} 对齐={_appearance.TitleAlign} 字形={_appearance.ShowGlyph} 毛玻璃={_appearance.Frosted}（之前毛玻璃={frostedWasOn}）");
    }

    /// <summary>Drop the cached frosted backdrop so it is re-captured on the next paint.</summary>
    private void InvalidateFrost()
    {
        if (_frostBmp != null) { _frostBmp.Dispose(); _frostBmp = null; }
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
                // CS_DBLCLKS is required so WM_LBUTTONDBLCLK fires (double-click to open items).
                style = FenceNative.CS_DBLCLKS
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

        // ---- Auto-layout gate (content-based, NOT an instance flag) ----
        // Run the grid ONLY when NO category has a valid size yet (fresh layout / first run). Once
        // positions exist — assigned by AutoLayoutGrid, loaded from fences.json, or from DefaultLayout
        // — we never overwrite them. This keeps user-dragged coordinates across application restarts
        // (an instance flag would reset every launch and re-run the grid, eating saved positions).
        bool needsInitialGrid = _layout.Categories.Count > 0 &&
            _layout.Categories.All(c => c.Width <= 0 || c.Height <= 0);
        if (needsInitialGrid)
        {
            AutoLayoutGrid();
            HostLog.Write("FenceLayer.BuildBoxes：首次自动网格布局完成（后续保留用户/持久化坐标）");
        }

        double maxRight = 0;
        for (int ci = 0; ci < _layout.Categories.Count; ci++)
        {
            var cat = _layout.Categories[ci];
            int left = (int)Math.Round(cat.X - _virtualLeft);
            int top = (int)Math.Round(cat.Y - _virtualTop);
            double hLogical = cat.Collapsed ? HeaderHeight : cat.Height;
            int right = left + (int)Math.Round(cat.Width * _dpiX);
            int bottom = top + (int)Math.Round(hLogical * _dpiY);
            var names = new List<string>();
            var paths = new List<string>(cat.MemberPaths);
            foreach (var p in cat.MemberPaths)
            {
                var n = GetDisplayName(p);
                if (!string.IsNullOrWhiteSpace(n)) names.Add(n);
            }
            _boxRects.Add(new BoxRect(left, top, right, bottom, cat.DisplayName, cat.Collapsed,
                cat.IconRef, names, paths, ci));
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
                    BuildBoxes();
                    ApplyRegion();
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
            case FenceNative.WM_NCHITTEST:
                // Layered + no-activate windows do not get reliable system resize from WS_THICKFRAME,
                // so we route ALL hits to HTCLIENT and implement resize ourselves in OnLButtonDown /
                // OnMouseMove. This also keeps drag-move working.
                return FenceNative.HTCLIENT;

            case FenceNative.WM_SETCURSOR:
            {
                // lParam low word = hit-test result from our WM_NCHITTEST (always HTCLIENT = 1).
                int ht = (int)(lParam.ToInt64() & 0xFFFF);
                if (ht == FenceNative.HTCLIENT)
                {
                    FenceNative.POINT p;
                    if (FenceNative.GetCursorPos(out p) && FenceNative.ScreenToClient(_hwnd, ref p))
                        FenceNative.SetCursor(ChooseCursor(p.X, p.Y));
                    else
                        FenceNative.SetCursor(FenceNative.LoadCursor(IntPtr.Zero, FenceNative.IDC_ARROW));
                    return new IntPtr(1); // we set the cursor ourselves; suppress DefWindowProc
                }
                return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
            }

            // ---- M3 interaction ----
            case FenceNative.WM_LBUTTONDOWN:
                OnLButtonDown(FenceNative.GET_X_LPARAM(lParam), FenceNative.GET_Y_LPARAM(lParam));
                return IntPtr.Zero;
            case FenceNative.WM_MOUSEMOVE:
                OnMouseMove(FenceNative.GET_X_LPARAM(lParam), FenceNative.GET_Y_LPARAM(lParam),
                    (wParam.ToInt64() & FenceNative.MK_LBUTTON) != 0);
                return IntPtr.Zero;
            case FenceNative.WM_LBUTTONUP:
                OnLButtonUp(FenceNative.GET_X_LPARAM(lParam), FenceNative.GET_Y_LPARAM(lParam));
                return IntPtr.Zero;
            case FenceNative.WM_LBUTTONDBLCLK:
                OnLButtonDblClk(FenceNative.GET_X_LPARAM(lParam), FenceNative.GET_Y_LPARAM(lParam));
                return IntPtr.Zero;
            case FenceNative.WM_CAPTURECHANGED:
                // Capture stolen (Alt-Tab etc.) — finalize any in-progress drag at its last position.
                EndDrag();
                return IntPtr.Zero;
            case FenceNative.WM_CONTEXTMENU:
            {
                // lParam is screen coords (0x8000 if from keyboard). Convert to client for hit-test.
                int sx = FenceNative.GET_X_LPARAM(lParam);
                int sy = FenceNative.GET_Y_LPARAM(lParam);
                if (sx == -1 && sy == -1)
                {
                    return IntPtr.Zero;
                }
                var pt = new FenceNative.POINT { X = sx, Y = sy };
                FenceNative.ScreenToClient(hWnd, ref pt);
                OnContextMenu(pt.X, pt.Y);
                return IntPtr.Zero;
            }
            case FenceNative.WM_COMMAND:
            {
                // Context-menu command: low word of wParam is the menu item id.
                int id = (int)(wParam.ToInt64() & 0xFFFF);
                OnContextCommand(id);
                return IntPtr.Zero;
            }
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
    // ---- M3 interaction helpers ----

    private enum HitZone { None, AddTile, CollapseBtn, Title, Item }

    private struct HitResult
    {
        public HitZone Zone;
        public int BoxIndex;
        public int ItemIndex;
    }

    // Layout metrics shared by DrawBoxes and hit-testing (must stay in sync!).
    private int CornerRadius => (int)Math.Round(10 * _dpiX);
    private int TitlePad => (int)Math.Round(12 * _dpiX);
    private int HeaderH(int boxH) => Math.Min((int)Math.Round(HeaderHeight * _dpiY), boxH);
    private int ItemLineH => (int)Math.Round(20 * _dpiY);
    private int ItemTopPad => (int)Math.Round(8 * _dpiY);
    private int CollapseBtnW => (int)Math.Round(28 * _dpiX);
    private int CollapseBtnInner => (int)Math.Round(8 * _dpiX);

    /// <summary>Map a client-pixel point to a fence element. Client coords (from WM_* lParam) and
    /// <see cref="_boxRects"/> are the SAME basis (window-client physical pixels), so no transform.</summary>
    private HitResult HitTest(int x, int y)
    {
        // 1) Add-tile (top priority so it isn't shadowed by a box beneath it).
        if (_addTileRect.HasValue)
        {
            var t = _addTileRect.Value;
            if (x >= t.Left && x <= t.Right && y >= t.Top && y <= t.Bottom)
                return new HitResult { Zone = HitZone.AddTile };
        }
        // 2) Boxes (reverse order so topmost wins on overlap).
        for (int i = _boxRects.Count - 1; i >= 0; i--)
        {
            var b = _boxRects[i];
            int boxH = b.Bottom - b.Top;
            int hh = HeaderH(boxH);
            if (x < b.Left || x > b.Right || y < b.Top || y > b.Bottom) continue;

            // Collapse button (header right).
            int cbLeft = b.Right - CollapseBtnW;
            int cbRight = b.Right - CollapseBtnInner;
            if (y >= b.Top && y <= b.Top + hh && x >= cbLeft && x <= cbRight)
                return new HitResult { Zone = HitZone.CollapseBtn, BoxIndex = i };

            // Title band (drag handle) — anywhere in header except the collapse button.
            if (y >= b.Top && y <= b.Top + hh)
                return new HitResult { Zone = HitZone.Title, BoxIndex = i };

            // Item grid cells (only when expanded) — must match DrawBoxes grid layout exactly.
            if (!b.Collapsed && b.Items.Count > 0)
            {
                int iconSz = (int)Math.Round(48 * _dpiX);
                int labelH = (int)Math.Round(20 * _dpiY);
                int cellPad = (int)Math.Round(4 * _dpiX);
                int gap = (int)Math.Round(2 * _dpiY);
                int cellW = iconSz + cellPad * 2;
                int cellH = iconSz + gap + labelH + cellPad * 2;
                int availW = b.Right - b.Left - TitlePad * 2;
                int cols = Math.Max(1, availW / cellW);

                float y0 = b.Top + hh + (float)(6 * _dpiY);
                int totalCells = cols * ((int)((boxH - hh - 12 * _dpiY)) / cellH);
                if (totalCells <= 0) totalCells = b.Items.Count;

                for (int k = 0; k < b.Items.Count && k < totalCells; k++)
                {
                    int col = k % cols;
                    int row = k / cols;
                    float cxLeft = b.Left + TitlePad + col * cellW;
                    float cyTop = y0 + row * cellH;
                    // Hit box = entire cell area
                    if (x >= cxLeft && x < cxLeft + cellW &&
                        y >= cyTop && y < cyTop + cellH)
                        return new HitResult { Zone = HitZone.Item, BoxIndex = i, ItemIndex = k };
                }
            }
        }
        return new HitResult { Zone = HitZone.None };
    }

    /// <summary>Strip common shortcut suffixes (.lnk) from display names for a cleaner look.</summary>
    private static string StripLnkSuffix(string name)
    {
        if (name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            return name[..^4];
        return name;
    }

    /// <summary>Resolve a file path or Shell Namespace CLSID path to a user-friendly display name.
    /// Regular files: Path.GetFileName + strip .lnk suffix.
    /// System icons (::CLSID): return the well-known Chinese display name.</summary>
    private static string GetDisplayName(string path)
    {
        // Well-known system desktop icons (Shell Namespace items, not real files)
        if (path.Equals("::{20D04FE0-3AEA-1069-A2D8-08002B30309D}", StringComparison.OrdinalIgnoreCase))
            return "此电脑";
        if (path.Equals("::{645FF040-5081-101B-9F08-00AA002F954E}", StringComparison.OrdinalIgnoreCase))
            return "回收站";
        if (path.Equals("::{21EC2020-3AEA-1069-A2DD-08002B30309D}", StringComparison.OrdinalIgnoreCase))
            return "控制面板";
        // Regular filesystem path
        return StripLnkSuffix(Path.GetFileName(path));
    }

    /// <summary>Given a client-pixel point inside box <paramref name="bi"/>, return the grid
    /// insertion slot (0-based among ALL items) the cursor is over. Mirrors the item-grid layout
    /// used by <see cref="DrawBoxes"/> so the computed slot matches what the user sees. The result
    /// is clamped to [0, itemCount] so dropping past the last row appends.</summary>
    private int ComputeInsertSlot(int bi, int x, int y)
    {
        if (bi < 0 || bi >= _boxRects.Count) return -1;
        var b = _boxRects[bi];
        int w = b.Right - b.Left;
        int h = b.Bottom - b.Top;
        if (w <= 0 || h <= 0) return -1;
        int pad = (int)Math.Round(12 * _dpiX);
        int hh = Math.Min((int)Math.Round(HeaderHeight * _dpiY), h);
        int iconSz = (int)Math.Round(48 * _dpiX);
        int labelH = (int)Math.Round(20 * _dpiY);
        int cellPad = (int)Math.Round(4 * _dpiX);
        int gap = (int)Math.Round(2 * _dpiY);
        int cellW = iconSz + cellPad * 2;
        int cellH = iconSz + gap + labelH + cellPad * 2;
        int availW = w - pad * 2;
        int cols = Math.Max(1, availW / cellW);
        float y0 = b.Top + hh + (float)(6 * _dpiY);

        int col = (int)((x - (b.Left + pad)) / cellW);
        col = Math.Clamp(col, 0, cols - 1);
        int row = (int)((y - y0) / cellH);
        if (row < 0) row = 0;
        int slot = row * cols + col;

        int count = _layout != null && b.CategoryIndex >= 0 && b.CategoryIndex < _layout.Categories.Count
            ? (_layout.Categories[b.CategoryIndex].MemberPaths?.Count ?? 0)
            : 0;
        if (slot > count) slot = count;
        return slot;
    }

    /// <summary>Check if two box rectangles overlap by at least the given fraction (0-1) of the smaller box's area.</summary>
    private static bool BoxesOverlap(BoxRect a, BoxRect b, float minOverlapFraction)
    {
        int ixLeft = Math.Max(a.Left, b.Left);
        int ixRight = Math.Min(a.Right, b.Right);
        int ixTop = Math.Max(a.Top, b.Top);
        int ixBottom = Math.Min(b.Bottom, a.Bottom);
        if (ixRight <= ixLeft || ixBottom <= ixTop) return false; // no intersection
        long intersectArea = (long)(ixRight - ixLeft) * (ixBottom - ixTop);
        long areaA = (long)(a.Right - a.Left) * (a.Bottom - a.Top);
        long areaB = (long)(b.Right - b.Left) * (b.Bottom - b.Top);
        long minArea = Math.Min(areaA, areaB);
        return minArea > 0 && intersectArea >= (long)(minArea * minOverlapFraction);
    }

    /// <summary>After a position swap, resolve ALL overlapping boxes using iterative
    /// minimum-translation-vector (MTV) separation. Guarantees no two boxes overlap on exit.</summary>
    private void SeparateOverlappingBoxes()
    {
        if (_layout == null || _boxRects.Count < 2) return;
        HostLog.Write($"FenceLayer 开始分离重叠盒子：{_boxRects.Count} 个盒子");
        int minGap = (int)Math.Round(30 * _dpiX); // generous gap between boxes (physical px)
        int totalIters = 0;
        // Multi-pass: each pass separates every overlapping pair. Cascading overlaps
        // (where fixing pair A-B pushes B into C) resolve over subsequent passes.
        for (int iter = 0; iter < 20; iter++)
        {
            bool anyOverlap = false;
            for (int i = 0; i < _boxRects.Count; i++)
            {
                for (int j = i + 1; j < _boxRects.Count; j++)
                {
                    var a = _boxRects[i];
                    var b = _boxRects[j];
                    // Check for ANY intersection (even 1px touch)
                    int ixL = Math.Max(a.Left, b.Left);
                    int ixR = Math.Min(a.Right, b.Right);
                    int ixT = Math.Max(a.Top, b.Top);
                    int ixB = Math.Min(a.Bottom, b.Bottom);
                    if (ixR <= ixL || ixB <= ixT) continue; // separated
                    anyOverlap = true;
                    int overlapX = ixR - ixL;
                    int overlapY = ixB - ixT;
                    var catA = _layout.Categories[a.CategoryIndex];
                    var catB = _layout.Categories[b.CategoryIndex];
                    // MTV: separate along axis of MINIMUM overlap (least displacement)
                    if (overlapX > 0 && overlapY > 0)
                    {
                        if (overlapX <= overlapY)
                        {
                            // Push horizontally — full overlap + gap to guarantee separation
                            int push = overlapX + minGap;
                            if (a.Left < b.Left) { catA.X -= push / _dpiX; catB.X += push / _dpiX; }
                            else { catB.X -= push / _dpiX; catA.X += push / _dpiX; }
                        }
                        else
                        {
                            // Push vertically — full overlap + gap
                            int push = overlapY + minGap;
                            if (a.Top < b.Top) { catA.Y -= push / _dpiY; catB.Y += push / _dpiY; }
                            else { catB.Y -= push / _dpiY; catA.Y += push / _dpiY; }
                        }
                    }
                    else if (overlapX > 0) // edge-only horizontal touch
                    {
                        int push = overlapX + minGap;
                        if (a.Left < b.Left) { catA.X -= push / _dpiX; catB.X += push / _dpiX; }
                        else { catB.X -= push / _dpiX; catA.X += push / _dpiX; }
                    }
                    else // edge-only vertical touch (overlapY > 0)
                    {
                        int push = overlapY + minGap;
                        if (a.Top < b.Top) { catA.Y -= push / _dpiY; catB.Y += push / _dpiY; }
                        else { catB.Y -= push / _dpiY; catA.Y += push / _dpiY; }
                    }
                }
            }
            // Rebuild rects after adjusting all pairs in this pass
            BuildBoxes();
            if (!anyOverlap) break; // clean exit — fully resolved
            totalIters = iter + 1;
        }
        HostLog.Write($"FenceLayer 分离完成：{totalIters} 轮迭代");
    }

    /// <summary>Returns the index of the box whose bottom-right resize zone contains (x,y),
    /// or -1. Shared by OnLButtonDown (start resize) and cursor feedback.</summary>
    private int HitResizeZone(int x, int y)
    {
        const int edge = 8; // px resize-sensitive square around the bottom-right corner
        for (int i = 0; i < _boxRects.Count; i++)
        {
            var b = _boxRects[i];
            if (x >= b.Right - edge && x <= b.Right + edge &&
                y >= b.Bottom - edge && y <= b.Bottom + edge)
                return i;
        }
        return -1;
    }

    /// <summary>Pick the mouse cursor for a client-space point: resize zone → diagonal arrow,
    /// title → 4-way move arrow, clickable items/buttons → hand, elsewhere → default arrow.</summary>
    private IntPtr ChooseCursor(int x, int y)
    {
        if (HitResizeZone(x, y) >= 0)
            return FenceNative.LoadCursor(IntPtr.Zero, FenceNative.IDC_SIZENWSE);
        var hit = HitTest(x, y);
        switch (hit.Zone)
        {
            case HitZone.Title:
                return FenceNative.LoadCursor(IntPtr.Zero, FenceNative.IDC_SIZEALL);
            case HitZone.Item:
            case HitZone.AddTile:
            case HitZone.CollapseBtn:
                return FenceNative.LoadCursor(IntPtr.Zero, FenceNative.IDC_HAND);
            default:
                return FenceNative.LoadCursor(IntPtr.Zero, FenceNative.IDC_ARROW);
        }
    }

    private void OnLButtonDown(int x, int y)
    {
        TouchActivity();
        // Custom box-resize: if the press is within the bottom-right resize zone of a box,
        // start a resize of that specific box (layered+noactivate windows can't use WS_THICKFRAME).
        int rz = HitResizeZone(x, y);
        if (rz >= 0)
        {
            var b = _boxRects[rz];
            _resizeDir = 3;
            _resizeCat = _layout!.Categories[b.CategoryIndex];
            _resizeStartX = x;
            _resizeStartY = y;
            _resizeStartW = _resizeCat.Width;
            _resizeStartH = _resizeCat.Height;
            FenceNative.SetCapture(_hwnd);
            HostLog.Write($"FenceLayer 盒子缩放开始：cat={_resizeCat.DisplayName}");
            return;
        }

        var hit = HitTest(x, y);
        switch (hit.Zone)
        {
            case HitZone.AddTile:
                NewCategory();
                break;
            case HitZone.CollapseBtn:
                ToggleCollapse(hit.BoxIndex);
                break;
            case HitZone.Title:
            {
                var b = _boxRects[hit.BoxIndex];
                _dragCat = _layout!.Categories[b.CategoryIndex];
                _dragBoxIndex = hit.BoxIndex;
                _dragOffsetX = x - b.Left;
                _dragOffsetY = y - b.Top;
                _dragOrigX = _dragCat.X;  // save original position BEFORE drag overwrites it
                _dragOrigY = _dragCat.Y;
                FenceNative.SetCapture(_hwnd);
                HostLog.Write($"FenceLayer 拖拽开始：cat={_dragCat.DisplayName}");
                break;
            }
            case HitZone.Item:
                // Arm a potential item-drag. We do NOT capture yet, so a plain click or double-click
                // (open) still works; promotion to a real drag happens in OnMouseMove past DragThreshold.
                _pendingItem = (hit.BoxIndex, hit.ItemIndex);
                _pendingItemStartX = x;
                _pendingItemStartY = y;
                break;
            // HitZone.None / AddTile(handled above) / CollapseBtn(handled above): nothing extra.
        }

        // Fallback: if the click landed inside a box rect but HitTest didn't return Title
        // (e.g., title bar is off-screen, or clicked on empty body area), still allow
        // dragging the box so users can recover boxes whose title is outside the visible area.
        if (_dragCat == null && !_pendingItem.HasValue && hit.Zone == HitZone.None)
        {
            for (int i = 0; i < _boxRects.Count; i++)
            {
                var b = _boxRects[i];
                if (x >= b.Left && x <= b.Right && y >= b.Top && y <= b.Bottom)
                {
                    _dragCat = _layout!.Categories[b.CategoryIndex];
                    _dragBoxIndex = i;
                    _dragOffsetX = x - b.Left;
                    _dragOffsetY = y - b.Top;
                    _dragOrigX = _dragCat.X;
                    _dragOrigY = _dragCat.Y;
                    FenceNative.SetCapture(_hwnd);
                    HostLog.Write($"FenceLayer 拖拽开始（body fallback）：cat={_dragCat.DisplayName}");
                    break;
                }
            }
        }
    }

    private void OnMouseMove(int x, int y, bool lButton)
    {
        TouchActivity();
        // ---- Item drag between categories ----
        // Promote a pending item press into a real drag once the pointer passes the threshold.
        if (_pendingItem.HasValue && _dragItemCat == null)
        {
            if (!lButton) { _pendingItem = null; return; }
            int ddx = x - _pendingItemStartX, ddy = y - _pendingItemStartY;
            if (Math.Abs(ddx) > DragThreshold || Math.Abs(ddy) > DragThreshold)
            {
                var b = _boxRects[_pendingItem.Value.BoxIndex];
                _dragItemCat = _layout!.Categories[b.CategoryIndex];
                _dragItemIndex = _pendingItem.Value.ItemIndex;
                _pendingItem = null;
                FenceNative.SetCapture(_hwnd);
                HostLog.Write($"FenceLayer 条目拖拽开始：cat={_dragItemCat.DisplayName} item={_dragItemIndex}");
            }
            else
            {
                return; // below threshold — stay pending, don't start box-move
            }
        }
        // Active item drag: track cursor position for ghost rendering, and highlight drop-target box.
        if (_dragItemCat != null)
        {
            if (!lButton) { EndDrag(); return; }
            _dragItemX = x;
            _dragItemY = y;
            var h = HitTest(x, y);
            int tgt = -1;
            // Primary: use HitResult (works for title/item/collapse zones) — allow SAME box too
            // (so the user can reorder items within a box, not just move across boxes).
            if (h.Zone != HitZone.None && h.BoxIndex >= 0 && h.BoxIndex < _boxRects.Count)
            {
                tgt = h.BoxIndex;
            }
            // Fallback: direct box-rect containment (catches drops on empty areas inside a box
            // that HitTest might classify as None due to grid cell gaps).
            if (tgt < 0)
            {
                for (int bi = 0; bi < _boxRects.Count; bi++)
                {
                    var b = _boxRects[bi];
                    if (x >= b.Left && x <= b.Right && y >= b.Top && y <= b.Bottom)
                    { tgt = bi; break; }
                }
            }
            int slot = tgt >= 0 ? ComputeInsertSlot(tgt, x, y) : -1;
            if (tgt != _itemDropTarget || slot != _itemDropSlot)
            {
                _itemDropTarget = tgt;
                _itemDropSlot = slot;
            }
            // Always redraw: ghost icon must follow cursor in real-time
            UpdateVisual();
            return;
        }

        // Custom box-resize: grow/shrink the targeted box from its bottom-right corner.
        if (_resizeDir != 0 && _resizeCat != null)
        {
            if (!lButton) { EndDrag(); return; }
            // Convert screen-px delta to logical px (cat.Width/Height are stored in logical units).
            double dx = (x - _resizeStartX) / _dpiX;
            double dy = (y - _resizeStartY) / _dpiY;
            _resizeCat.Width = Math.Max(140, _resizeStartW + dx);
            _resizeCat.Height = Math.Max(70, _resizeStartH + dy);
            BuildBoxes();
            ApplyRegion();
            UpdateVisual();
            return;
        }

        if (_dragCat == null) return;
        // 左键一松开就终止拖拽。不要单纯依赖 WM_LBUTTONUP：WS_EX_NOACTIVATE 窗口的鼠标捕获是
        // "弱"的，且拖拽期间命中区滞留在旧位置，松手点常在命中区外，WM_LBUTTONUP 经常收不到，
        // 导致盒子黏在光标上。这里把"左键抬起(lButton==false)"作为确定性终止信号。
        if (!lButton)
        {
            EndDrag();
            return;
        }

        int boxW = (int)Math.Round(_dragCat.Width * _dpiX);
        int boxH = (int)Math.Round((_dragCat.Collapsed ? HeaderHeight : _dragCat.Height) * _dpiY);
        int newLeft = Math.Clamp(x - _dragOffsetX, 0, Math.Max(_winW - boxW, 0));
        int newTop = Math.Clamp(y - _dragOffsetY, 0, Math.Max(_winH - boxH, 0));

        _dragCat.X = newLeft + _virtualLeft;
        _dragCat.Y = newTop + _virtualTop;

        // Throttle redraw to ~16ms; update region too so the old box position doesn't show
        // as an opaque-black ghost (AlphaFormat=0 makes transparent pixels render as opaque black
        // within the stale region).
        var now = DateTime.Now;
        if ((now - _lastDragPaint).TotalMilliseconds >= 16)
        {
            _lastDragPaint = now;
            BuildBoxes();
            UpdateVisual();
            ApplyRegion();
        }
    }

    private void OnLButtonUp(int x, int y)
    {
        TouchActivity();
        // A press that never became a drag (plain click) just clears the pending state.
        if (_pendingItem.HasValue && _dragItemCat == null)
        {
            _pendingItem = null;
            return;
        }
        EndDrag();
    }

    private void EndDrag()
    {
        // ---- Item drag (reorder within a box OR move across boxes) ----
        if (_dragItemCat != null)
        {
            var src = _dragItemCat;
            int idx = _dragItemIndex;
            int target = _itemDropTarget;
            int slot = _itemDropSlot;
            _dragItemCat = null;
            _dragItemIndex = -1;
            _itemDropTarget = -1;
            _itemDropSlot = -1;
            if (FenceNative.GetCapture() == _hwnd) FenceNative.ReleaseCapture();

            if (target >= 0 && target < _boxRects.Count && idx >= 0 && idx < src.MemberPaths.Count && slot >= 0)
            {
                var tb = _boxRects[target];
                var tCat = _layout!.Categories[tb.CategoryIndex];
                string path = src.MemberPaths[idx];

                if (tCat != src)
                {
                    // Cross-category move: insert at the computed grid slot in the target box.
                    src.MemberPaths.RemoveAt(idx);
                    int insertAt = Math.Clamp(slot, 0, tCat.MemberPaths.Count);
                    tCat.MemberPaths.Insert(insertAt, path);
                    HostLog.Write($"FenceLayer 条目移动：'{System.IO.Path.GetFileName(path)}' {src.DisplayName}→{tCat.DisplayName} @ {insertAt}");
                }
                else
                {
                    // Reorder within the SAME box. The dragged item is skipped in the rendered grid,
                    // so the visual slot must be mapped back to an insertion index in the real array:
                    //   visual slot S  (0-based among visible items)  →  original index:
                    //     S <= idx  →  S
                    //     S >  idx  →  S + 1   (the gap left by the dragged item shifts later items)
                    // After removing the dragged item (was at idx), clamp the reduced insertion index.
                    int count = src.MemberPaths.Count;
                    int desiredOrig = (slot <= idx) ? slot : (slot >= count ? count : slot + 1);
                    src.MemberPaths.RemoveAt(idx);
                    int insertAt = (desiredOrig > idx) ? desiredOrig - 1 : desiredOrig;
                    insertAt = Math.Clamp(insertAt, 0, src.MemberPaths.Count);
                    src.MemberPaths.Insert(insertAt, path);
                    HostLog.Write($"FenceLayer 条目重排：'{System.IO.Path.GetFileName(path)}' {src.DisplayName} idx {idx}→{insertAt}");
                }
                BuildBoxes();
                ApplyRegion();
                UpdateVisual();
                try { FenceStore.Current.Save(_layout!); } catch (Exception ex) { HostLog.Write("FenceLayer 条目移动落盘失败", ex); }
                return;
            }
            // No valid move (dropped outside any box) — just refresh (clears highlight).
            BuildBoxes();
            ApplyRegion();
            UpdateVisual();
            return;
        }

        // ---- Resize ----
        if (_resizeDir != 0 && _resizeCat != null)
        {
            _resizeDir = 0; // clear resize FIRST
            var rCat = _resizeCat;
            _resizeCat = null;
            if (FenceNative.GetCapture() == _hwnd) FenceNative.ReleaseCapture();
            BuildBoxes();
            ApplyRegion();
            UpdateVisual();
            try { FenceStore.Current.Save(_layout!); } catch (Exception ex) { HostLog.Write("FenceLayer 盒子缩放落盘失败", ex); }
            HostLog.Write($"FenceLayer 盒子缩放结束：cat={rCat.DisplayName} 已保存 (W={rCat.Width:F0} H={rCat.Height:F0})");
            return;
        }

        // ---- Box move (+ reorder) ----
        if (_dragCat == null) return;
        var cat = _dragCat;
        int movedBox = _dragBoxIndex;
        _dragCat = null;       // null FIRST so WM_CAPTURECHANGED re-entry is a no-op
        _dragBoxIndex = -1;
        if (FenceNative.GetCapture() == _hwnd) FenceNative.ReleaseCapture();

        // Reorder: if the CURSOR (not box center) lies inside another box, swap positions.
        // Using cursor position means dropping anywhere on the target box (title OR body) triggers reorder.
        // After swapping, auto-separate any overlapping boxes.
        int dropTarget = -1;
        if (movedBox >= 0 && movedBox < _boxRects.Count)
        {
            // Use the LAST known cursor position during drag to determine drop target.
            // _boxRects reflect the final BuildBoxes() call in OnMouseMove.
            for (int j = 0; j < _boxRects.Count; j++)
            {
                if (j == movedBox) continue;
                var o = _boxRects[j];
                // Check if the dragged box overlaps the target box significantly (>30% area overlap)
                // OR if the last drag position was inside the target
                if (BoxesOverlap(_boxRects[movedBox], o, 0.3f))
                {
                    var other = _layout!.Categories[o.CategoryIndex];
                    if (other != cat)
                    {
                        dropTarget = j;
                        // Swap positions using saved original coordinates
                        double origX = _dragOrigX, origY = _dragOrigY;
                        cat.X = other.X;
                        cat.Y = other.Y;
                        other.X = origX;
                        other.Y = origY;
                        HostLog.Write($"FenceLayer 分类重排：{cat.DisplayName} ↔ {other.DisplayName}");
                    }
                    break;
                }
            }
        }

        BuildBoxes();
        // ALWAYS separate overlapping boxes — not just after swaps.
        // A simple drag-and-drop without swap can still leave boxes overlapping.
        SeparateOverlappingBoxes();
        ApplyRegion();
        UpdateVisual();
        try { FenceStore.Current.Save(_layout!); } catch (Exception ex) { HostLog.Write("FenceLayer 拖拽落盘失败", ex); }
        HostLog.Write($"FenceLayer 拖拽结束：cat={cat.DisplayName} 已保存");
    }

    private void OnLButtonDblClk(int x, int y)
    {
        TouchActivity();
        // A double-click on the TITLE also fires the DOWN/DOWN that started a drag; the second
        // WM_LBUTTONUP is replaced by WM_LBUTTONDBLCLK, so _dragCat/capture would otherwise leak and
        // later mouse moves would keep dragging the box. Finalize any in-progress drag first.
        _pendingItem = null; // a double-click is never a drag
        EndDrag();
        var hit = HitTest(x, y);
        if (hit.Zone == HitZone.Item && hit.BoxIndex >= 0 && hit.ItemIndex >= 0)
        {
            var b = _boxRects[hit.BoxIndex];
            if (hit.ItemIndex < b.Paths.Count)
                OpenItem(b.Paths[hit.ItemIndex]);
        }
    }

    private void OpenItem(string path)
    {
        try
        {
            var psi = new ProcessStartInfo(path) { UseShellExecute = true };
            Process.Start(psi);
            HostLog.Write($"FenceLayer 打开项：{path}");
        }
        catch (Exception ex)
        {
            HostLog.Write($"FenceLayer 打开项失败 path={path}", ex);
        }
    }

    private void ToggleCollapse(int boxIndex)
    {
        if (_layout == null || boxIndex < 0 || boxIndex >= _boxRects.Count) return;
        var b = _boxRects[boxIndex];
        var cat = _layout.Categories[b.CategoryIndex];
        cat.Collapsed = !cat.Collapsed;
        BuildBoxes();
        ApplyRegion();
        UpdateVisual();
        try { FenceStore.Current.Save(_layout); } catch (Exception ex) { HostLog.Write("FenceLayer 折叠落盘失败", ex); }
        HostLog.Write($"FenceLayer 折叠切换：cat={cat.DisplayName} collapsed={cat.Collapsed}");
    }

    private void NewCategory()
    {
        if (_layout == null) return;
        // Place the new box below all existing boxes (virtual-screen coords).
        double maxBottom = _virtualTop;
        foreach (var b in _boxRects) maxBottom = Math.Max(maxBottom, b.Bottom + _virtualTop);
        double newX = _virtualLeft + 40 * _dpiX;
        double newY = maxBottom + 20 * _dpiY;

        var cat = new FenceCategory
        {
            Id = Guid.NewGuid().ToString(),
            DisplayName = "新建分类",
            IconRef = "\uD83D\uDCC1", // 📁
            X = newX,
            Y = newY,
            Width = 220,
            Height = 160,
            Collapsed = false,
            MemberPaths = new List<string>()
        };
        _layout.Categories.Add(cat);
        BuildBoxes();
        ApplyRegion();
        UpdateVisual();
        try { FenceStore.Current.Save(_layout); } catch (Exception ex) { HostLog.Write("FenceLayer 新建落盘失败", ex); }
        HostLog.Write("FenceLayer 新建分类完成");
    }

    private void OnContextMenu(int x, int y)
    {
        TouchActivity();
        var hit = HitTest(x, y);
        if (hit.Zone == HitZone.None || hit.BoxIndex < 0) return;
        var b = _boxRects[hit.BoxIndex];
        var cat = _layout!.Categories[b.CategoryIndex];
        bool canDelete = cat.Id != FenceConstants.UncategorizedId;

        IntPtr hMenu = FenceNative.CreatePopupMenu();
        if (hMenu == IntPtr.Zero) return;

        // --- Item-level remove: right-clicking an icon gives a "remove this icon" entry ---
        if (hit.Zone == HitZone.Item && hit.ItemIndex >= 0 && hit.ItemIndex < b.Paths.Count)
        {
            string name = GetDisplayName(b.Paths[hit.ItemIndex]);
            // Encode box index in thousands, item index in units: id = 4000 + boxIndex*1000 + itemIndex.
            int mid = 4000 + hit.BoxIndex * 1000 + hit.ItemIndex;
            FenceNative.AppendMenu(hMenu, FenceNative.MF_STRING, (UIntPtr)mid, $"从此盒子移除「{name}」");
            FenceNative.AppendMenu(hMenu, FenceNative.MF_SEPARATOR, UIntPtr.Zero, null);
        }

        // Encode the category index into the menu id (1000 + index) so WM_COMMAND knows the target.
        FenceNative.AppendMenu(hMenu, canDelete ? FenceNative.MF_STRING : FenceNative.MF_GRAYED,
            (UIntPtr)(1000 + b.CategoryIndex),
            canDelete ? $"删除分类「{cat.DisplayName}」" : "无法删除内置「未分类」");
        // Import desktop icons into this box (replace fragile OLE drag-drop overlay).
        FenceNative.AppendMenu(hMenu, FenceNative.MF_STRING, (UIntPtr)(2000 + b.CategoryIndex),
            "导入桌面图标…");
        FenceNative.AppendMenu(hMenu, FenceNative.MF_STRING, (UIntPtr)(3000 + b.CategoryIndex),
            "导入桌面全部图标");

        var pt = new FenceNative.POINT { X = x, Y = y };
        FenceNative.ClientToScreen(_hwnd, ref pt);
        FenceNative.TrackPopupMenuEx(hMenu, FenceNative.TPM_RIGHTBUTTON, pt.X, pt.Y, _hwnd, IntPtr.Zero);
        FenceNative.DestroyMenu(hMenu);
    }

    private void OnContextCommand(int id)
    {
        if (_layout == null) return;
        // Remove-icon command ids are always >= 4000 (1000+ and 2000+/3000+ ranges stay below 4000),
        // so this branch must cover EVERY box index, not just box 0. Removing the upper bound fixes
        // the bug where icons could only be removed from the uncategorized box.
        if (id >= 4000)
        {
            int boxIndex = (id - 4000) / 1000;
            int itemIndex = (id - 4000) % 1000;
            RemoveItemFromBox(boxIndex, itemIndex);
        }
        else if (id >= 1000 && id < 2000)
        {
            DeleteCategory(id - 1000);
        }
        else if (id >= 2000 && id < 3000)
        {
            ImportDesktopFiles(id - 2000);
        }
        else if (id >= 3000)
        {
            ImportEntireDesktop(id - 3000);
        }
    }

    /// <summary>Remove a single icon from its box (does NOT delete the file; just un-links it from
    /// the fence). Persisted immediately so the change survives restart.</summary>
    private void RemoveItemFromBox(int boxIndex, int itemIndex)
    {
        if (_layout == null || boxIndex < 0 || boxIndex >= _boxRects.Count) return;
        var b = _boxRects[boxIndex];
        if (b.CategoryIndex < 0 || b.CategoryIndex >= _layout.Categories.Count) return;
        var cat = _layout.Categories[b.CategoryIndex];
        if (itemIndex < 0 || itemIndex >= cat.MemberPaths.Count) return;

        string path = cat.MemberPaths[itemIndex];
        cat.MemberPaths.RemoveAt(itemIndex);
        HostLog.Write($"FenceLayer 移除图标：{path} from {cat.DisplayName}（剩余 {cat.MemberPaths.Count}）");
        BuildBoxes();
        ApplyRegion();
        UpdateVisual();
        try { FenceStore.Current.Save(_layout); } catch (Exception ex) { HostLog.Write("FenceLayer 移除图标落盘失败", ex); }
    }

    private void DeleteCategory(int index)
    {
        if (_layout == null || index < 0 || index >= _layout.Categories.Count) return;
        var cat = _layout.Categories[index];
        if (cat.Id == FenceConstants.UncategorizedId) return; // built-in, immutable

        // Confirm first — deletion is destructive, so make the consequence explicit (icon count).
        // This is the first line of defence against accidental deletes.
        var dlg = MessageBox.Show(
            $"确定要删除分类「{cat.DisplayName}」吗？\n\n其 {cat.MemberPaths.Count} 个图标将移回「未分类」。\n（删除后仍可在系统托盘菜单中撤销）",
            "删除分类",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);
        if (dlg != DialogResult.Yes) return;

        // Snapshot for undo BEFORE mutating anything.
        _undoStack.Add(new UndoEntry(cat, index, cat.MemberPaths.ToList()));
        if (_undoStack.Count > MaxUndoEntries) _undoStack.RemoveAt(0);

        // Re-home members into the uncategorized box so they are not lost.
        var unc = _layout.Categories.FirstOrDefault(c => c.Id == FenceConstants.UncategorizedId);
        if (unc != null && cat.MemberPaths != null)
            unc.MemberPaths.AddRange(cat.MemberPaths);

        _layout.Categories.RemoveAt(index);
        BuildBoxes();
        ApplyRegion();
        UpdateVisual();
        try { FenceStore.Current.Save(_layout); } catch (Exception ex) { HostLog.Write("FenceLayer 删除落盘失败", ex); }
        HostLog.Write($"FenceLayer 删除分类：{cat.DisplayName}（可撤销，撤销栈 {_undoStack.Count}）");
        UndoStateChanged?.Invoke();
    }

    /// <summary>Restore the most recently deleted category — its box (with original geometry) and all
    /// its member icons. No-op if nothing is pending. Safe to call repeatedly to walk back several
    /// deletions.</summary>
    public void UndoLastCategoryDelete()
    {
        if (_layout == null || _undoStack.Count == 0) return;
        var entry = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);

        var cat = entry.Category;
        var unc = _layout.Categories.FirstOrDefault(c => c.Id == FenceConstants.UncategorizedId);
        if (unc != null && entry.MovedPaths != null)
        {
            // Pull the re-homed members back out of 未分类 (only the exact paths we moved).
            foreach (var p in entry.MovedPaths)
            {
                int i = unc.MemberPaths.IndexOf(p);
                if (i >= 0) unc.MemberPaths.RemoveAt(i);
            }
        }

        // Re-insert at the (clamped) original slot. Geometry lives on the category object, so the box
        // returns to its old position regardless of the current list order.
        int insertAt = Math.Min(entry.OriginalIndex, _layout.Categories.Count);
        _layout.Categories.Insert(insertAt, cat);

        BuildBoxes();
        ApplyRegion();
        UpdateVisual();
        try { FenceStore.Current.Save(_layout); } catch (Exception ex) { HostLog.Write("FenceLayer 撤销删除落盘失败", ex); }
        HostLog.Write($"FenceLayer 撤销删除分类：{cat.DisplayName}（剩余撤销栈 {_undoStack.Count}）");
        UndoStateChanged?.Invoke();
    }

    public bool CanUndoCategoryDelete => _undoStack.Count > 0;
    public string? PendingUndoCategoryName => _undoStack.Count > 0 ? _undoStack[^1].Category.DisplayName : null;

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

                // Transparent base (alpha 0). The layered window uses constant-alpha (AlphaFormat = 0,
                // SourceConstantAlpha = 255) for Milestone 2, so the bitmap's own alpha is ignored and the
                // whole window would be opaque black — UNLESS the click region (SetWindowRgn, per-box union
                // in ApplyRegion) clips the window to the boxes. Click-through = region gaps; visible =
                // drawn boxes. Drawing only boxes on a transparent-cleared bitmap keeps it simple/robust.
                //
                // v13: Body background fill is done HERE via LockBits (before GDI+ touches the bitmap).
                // This completely bypasses GDI+'s unreliable low-alpha compositing: we write exact ARGB
                // pixel values directly into memory. GDI+ then draws headers/borders/icons/text ON TOP
                // of this pre-filled background — GDI+ compositing works correctly when the destination
                // pixels are already opaque or semi-transparent (the bug only affects GDI+ Brush alpha).
                FillBodyPixels(bmp);

                using (var g = Graphics.FromImage(bmp))
                {
                    if (_diagFullWindow)
                {
                    // Opaque dark fill of the ENTIRE bitmap (RGB 20,22,28). Used only to re-confirm the
                    // layered child is composited under the desktop WorkerW.
                    using var brush = new SolidBrush(Color.FromArgb(255, 20, 22, 28));
                    g.FillRectangle(brush, 0, 0, _winW, _winH);
                }
                else
                {
                    // Milestone 2: draw the real fence boxes (headers, borders, titles, items) and the
                    // "＋ 新建分类" tile from _boxRects. Body background is already filled by
                    // FillBodyPixels (LockBits, before this Graphics block) — do NOT fill body here.
                    EnsureFrostCapture();
                    DrawBoxes(g);
                }
            }

            // NOTE: No post-GDI+ body cleanup needed. FillBodyPixels (before Graphics) writes the
            // correct body alpha directly via LockBits. GDI+ operations (text, borders, icons)
            // draw ON TOP of that background — their pixels are in the header/icon/label areas,
            // not the body fill area. The v14 "ClearBodyPixels" approach was WRONG: it unconditionally
            // overwrote ALL body-region pixels to (0,0,0,1), destroying the correct alpha value
            // that FillBodyPixels had written — which is why v14 showed white at EVERY opacity level.

            hdcScreen = NativeMethods.GetDC(IntPtr.Zero);
            hdcMem = NativeMethods.CreateCompatibleDC(hdcScreen);
            if (hdcMem == IntPtr.Zero) return;

            // UpdateLayeredWindow with AC_SRC_ALPHA expects the source bitmap in PREMULTIPLIED
            // alpha (PARGB). GDI+ stores straight alpha in Format32bppArgb, so we premultiply the
            // pixel bytes before handing the bitmap to GetHbitmap — otherwise DWM double-premultiplies
            // and icon edges (fine alpha) render dark/invisible against the box background.
            PremultiplyAlpha(bmp);

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
                // Per-pixel alpha (AlphaFormat = 1): the bitmap's own alpha channel is respected,
                // so semi-transparent brushes in DrawBoxes composite correctly — wallpaper shows through.
                AlphaFormat = 1 // AC_SRC_ALPHA
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
        int r = (int)Math.Round(_appearance.CornerRadius * _dpiX);
        int pad = (int)Math.Round(12 * _dpiX);
        int hh = (int)Math.Round(HeaderHeight * _dpiY);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

        // M4-B: body/header alpha + title font size are user-tunable. When Frosted is on, the body
        // is the blurred desktop backdrop (drawn per-box, clipped) instead of the flat dark fill.
        var titleAlign = _appearance.TitleAlign == 1 ? StringAlignment.Center : StringAlignment.Near;
        // Border alpha scales with body opacity so a near-transparent box doesn't keep a hard frame.
        int borderA = Math.Min(160, (int)(_appearance.BodyOpacity * 160 / 180.0));
        using var borderPen = new Pen(Color.FromArgb(borderA, 64, 70, 86), 1);     // scales 0→160 with body
        using var itemBrush = new SolidBrush(Color.FromArgb(235, 210, 214, 222));  // mostly opaque text
        using var titleFont = new Font("Segoe UI", (float)(_appearance.TitleFontSize * _dpiY), FontStyle.Bold);
        using var itemFont = new Font("Segoe UI", (float)(9 * _dpiY), FontStyle.Regular);
        using var sf = new StringFormat(StringFormatFlags.NoWrap)
        {
            Alignment = titleAlign,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter
        };

        for (int bi = 0; bi < _boxRects.Count; bi++)
        {
            var b = _boxRects[bi];
            int w = b.Right - b.Left;
            int h = b.Bottom - b.Top;
            if (w <= 0 || h <= 0) continue;

            // Body + header.
            // v13: Body background is filled by FillBodyPixels (LockBits, before Graphics.FromImage)
            // with exact ARGB values — completely bypassing GDI+'s low-alpha brush bug.
            // We do NOT fill the body here; GDI+ only draws headers, borders, titles, icons, and
            // labels ON TOP of the pre-filled body background.
            //
            int headerH = Math.Min(hh, h);
            if (_appearance.Frosted && _frostBmp != null)
            {
                // Frosted mode: draw blurred desktop backdrop clipped to box, then tint.
                using var boxPath = RoundedRectPath(b.Left, b.Top, w, h, r);
                using (var clip = new Region(boxPath))
                {
                    g.SetClip(clip, System.Drawing.Drawing2D.CombineMode.Replace);
                    g.DrawImage(_frostBmp, 0, 0);
                    g.ResetClip();
                }
                if (_appearance.FrostOpacity > 3)
                {
                    FillRoundedRectWithAlpha(g, b.Left, b.Top, w, h, r,
                        _appearance.FrostOpacity, 16, 18, 24);
                }
            }
            // NOTE: No else-branch for body fill here — body is handled by FillBodyPixels.
            if (_appearance.HeaderOpacity > 3)
            {
                FillHeaderWithAlpha(g, b.Left, b.Top, w, headerH, r,
                    _appearance.HeaderOpacity, 40, 44, 54);
            }

            // Title: drawn with GDI TextRenderer (NOT GDI+ DrawString). TextRenderer performs
            // automatic font fallback to Segoe UI Emoji for colored emoji and lays out the
            // emoji+label run with correct, natural spacing — eliminating both the "□□" tofu
            // boxes and the oversized gap caused by GDI+'s emoji advance-width measurement.
            string label = string.IsNullOrEmpty(b.Name) ? "未命名" : b.Name;
            string titleText = (_appearance.ShowGlyph && !string.IsNullOrEmpty(b.IconRef))
                ? b.IconRef + " " + label
                : label;
            var titleFlags = TextFormatFlags.VerticalCenter
                           | TextFormatFlags.SingleLine
                           | TextFormatFlags.EndEllipsis;
            titleFlags |= (titleAlign == StringAlignment.Center)
                ? TextFormatFlags.HorizontalCenter
                : TextFormatFlags.Left;
            int titleW = (int)(w - CollapseBtnW);
            TextRenderer.DrawText(
                g,
                titleText,
                titleFont,
                new Rectangle((int)b.Left, (int)b.Top, titleW, headerH),
                Color.FromArgb(255, 255, 255, 255),
                titleFlags);

            // Collapse/expand chevron in the header's right side (hit-tested as CollapseBtn).
            DrawCollapseChevron(g, b, headerH);

            // Items rendered as desktop-style icon grid (large icon centered, label below).
            // Skipped when the box is collapsed.
            if (!b.Collapsed && b.Items.Count > 0)
            {
                int iconSz = (int)Math.Round(48 * _dpiX);       // large icon — matches Windows desktop size
                int labelH = (int)Math.Round(20 * _dpiY);        // single-line — room for descenders (g/p/q/y)
                int cellPad = (int)Math.Round(4 * _dpiX);        // natural padding
                int gap = (int)Math.Round(2 * _dpiY);            // gap between icon and label
                int cellW = iconSz + cellPad * 2;                // cell ≈ icon width (text centered across it)
                int cellH = iconSz + gap + labelH + cellPad * 2; // cell height
                int availW = w - pad * 2;
                int cols = Math.Max(1, availW / cellW);          // auto-fit columns

                float y = b.Top + headerH + (float)(6 * _dpiY);
                int shown = 0;
                int totalCells = cols * ((int)((h - headerH - 12 * _dpiY)) / cellH);
                if (totalCells <= 0) totalCells = b.Items.Count; // fallback: show at least some

                for (int ii = 0; ii < b.Items.Count && shown < totalCells; ii++)
                {
                    // Skip the item being dragged (it's drawn as a ghost at cursor position instead)
                    if (_dragItemCat != null && b.CategoryIndex >= 0 &&
                        _layout!.Categories[b.CategoryIndex] == _dragItemCat && ii == _dragItemIndex)
                    { shown++; continue; }

                    int col = shown % cols;
                    int row = shown / cols;
                    float cx = b.Left + pad + col * cellW + cellW / 2f; // center of cell
                    float cy = y + row * cellH;

                    // Draw large icon centered in the upper portion of the cell.
                    if (ii < b.Paths.Count)
                    {
                        var icon = GetFileIcon(b.Paths[ii]);
                        if (icon != null)
                        {
                            g.DrawImage(icon, (int)(cx - iconSz / 2f), (int)(cy + cellPad), iconSz, iconSz);
                        }
                    }

                    // Draw label below icon, centered, single-line with ellipsis — Windows desktop style.
                    float labelTop = cy + cellPad + iconSz + gap;
                    using var itemSf = new StringFormat
                    {
                        Alignment = StringAlignment.Center,
                        LineAlignment = StringAlignment.Near,
                        Trimming = StringTrimming.EllipsisCharacter,
                        FormatFlags = StringFormatFlags.NoWrap   // single line, like desktop icons
                    };
                    string displayName = StripLnkSuffix(b.Items[ii]);
                    g.DrawString(displayName, itemFont, itemBrush,
                        new RectangleF(cx - cellW / 2f, labelTop, cellW, labelH),
                        itemSf);

                    shown++;
                }
                if (b.Items.Count > totalCells && totalCells > 0)
                {
                    using var moreSf = new StringFormat { Alignment = StringAlignment.Center };
                    g.DrawString($"… +{b.Items.Count - totalCells}", itemFont, itemBrush,
                        new RectangleF(b.Left + pad, y + ((b.Items.Count - 1) / cols + 1) * cellH,
                            w - pad * 2, labelH), moreSf);
                }
            }

            // 1px outline for definition; highlight the box currently targeted as an item drop.
            using var borderPath = RoundedRectPath(b.Left, b.Top, w, h, r);
            if (bi == _itemDropTarget)
            {
                using var dropPen = new Pen(Color.FromArgb(255, 88, 160, 255), 2); // accent blue, 2px
                g.DrawPath(dropPen, borderPath);

                // Insertion-slot indicator: outline the grid cell where the dragged item will land.
                if (_itemDropSlot >= 0)
                {
                    int padI = (int)Math.Round(12 * _dpiX);
                    int hhI = Math.Min((int)Math.Round(HeaderHeight * _dpiY), h);
                    int iconSzI = (int)Math.Round(48 * _dpiX);
                    int labelHI = (int)Math.Round(20 * _dpiY);
                    int cellPadI = (int)Math.Round(4 * _dpiX);
                    int gapI = (int)Math.Round(2 * _dpiY);
                    int cellWI = iconSzI + cellPadI * 2;
                    int cellHI = iconSzI + gapI + labelHI + cellPadI * 2;
                    int availWI = w - padI * 2;
                    int colsI = Math.Max(1, availWI / cellWI);
                    float y0I = b.Top + hhI + (float)(6 * _dpiY);
                    int colI = _itemDropSlot % colsI;
                    int rowI = _itemDropSlot / colsI;
                    float cellLeft = b.Left + padI + colI * cellWI;
                    float cellTop = y0I + rowI * cellHI;
                    using var slotPen = new Pen(Color.FromArgb(220, 88, 160, 255), 2.5f);
                    using var slotBrush = new SolidBrush(Color.FromArgb(48, 88, 160, 255)); // faint fill
                    using var slotPath = RoundedRectPath(
                        (int)Math.Round(cellLeft), (int)Math.Round(cellTop),
                        cellWI, cellHI, (int)Math.Round(6 * _dpiX));
                    g.FillPath(slotBrush, slotPath);
                    g.DrawPath(slotPen, slotPath);
                }
            }
            else
            {
                g.DrawPath(borderPen, borderPath);
            }
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

        // ---- Ghost icon during item drag ----
        if (_dragItemCat != null && _dragItemIndex >= 0 && _dragItemIndex < _dragItemCat.MemberPaths.Count)
        {
            string path = _dragItemCat.MemberPaths[_dragItemIndex];
            string displayName = GetDisplayName(path);
            var icon = GetFileIcon(path);
            int ghostIconSz = (int)Math.Round(48 * _dpiX);  // slightly larger for visibility
            int gx = _dragItemX - ghostIconSz / 2;
            int gy = _dragItemY - ghostIconSz / 2;

            // Semi-transparent overlay
            using var ghostBg = new SolidBrush(Color.FromArgb(120, 40, 44, 52));
            g.FillRectangle(ghostBg, gx - 4, gy - 4, ghostIconSz + 8, ghostIconSz + 36);

            // Icon
            if (icon != null)
                g.DrawImage(icon, gx, gy, ghostIconSz, ghostIconSz);

            // Label
            using var ghostFont = new Font("Segoe UI", (float)(9 * _dpiY), FontStyle.Regular);
            using var ghostBrush = new SolidBrush(Color.FromArgb(220, 255, 255, 255));
            using var ghostSf = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Near,
                Trimming = StringTrimming.EllipsisCharacter,
                FormatFlags = StringFormatFlags.NoWrap
            };
            g.DrawString(displayName, ghostFont, ghostBrush,
                new RectangleF(gx, gy + ghostIconSz + 2, ghostIconSz, 20), ghostSf);
        }
    }

    /// <summary>Draw the collapse/expand chevron (▾ expanded / ▸ collapsed) at the header's right,
    /// inside the region hit-tested as <see cref="HitZone.CollapseBtn"/>.</summary>
    private void DrawCollapseChevron(Graphics g, BoxRect b, int headerH)
    {
        int cx = b.Right - (CollapseBtnW + CollapseBtnInner) / 2;
        string chevron = b.Collapsed ? "▸" : "▾";
        using var chevFont = new Font("Segoe UI", (float)(12 * _dpiY), FontStyle.Regular);
        using var chevBrush = new SolidBrush(Color.FromArgb(200, 180, 186, 200));
        g.DrawString(chevron, chevFont, chevBrush,
            new RectangleF(cx - 12, b.Top, 24, headerH),
            new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
    }

    // ---- .lnk shortcut arrow removal -----------------------------------------------
    // SHGetFileInfo on a .lnk always paints the little "shortcut arrow" overlay. To render a clean
    // icon (no arrow), we resolve the shortcut to its REAL target via IShellLink and then ask the
    // shell for the *target's* icon. CLSID_ShellLink = 00021401-0000-0000-C000-000000000046.

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport, Guid("0000010B-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string? pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }

    /// <summary>Resolve a .lnk shortcut to its target file path (no arrow overlay needed afterwards).
    /// Returns null on any failure so the caller can fall back to the .lnk's own (arrowed) icon.</summary>
    private static string? ResolveLnkTarget(string lnkPath)
    {
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"));
            if (type == null) return null;
            var link = (IShellLinkW)Activator.CreateInstance(type)!;
            try
            {
                var persist = (IPersistFile)link;
                persist.Load(lnkPath, 0); // STGM_READ
                link.Resolve(IntPtr.Zero, 0x1 /* SLR_NO_UI */ | 0x100 /* SLR_NOSEARCH */);
                var sb = new StringBuilder(260);
                link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
                string target = sb.ToString();
                return string.IsNullOrWhiteSpace(target) ? null : target;
            }
            finally
            {
                Marshal.ReleaseComObject(link);
            }
        }
        catch { return null; }
    }

    /// <summary>Get (or retrieve from cache) the shell **large** icon for a file/directory path,
    /// returned as a managed <see cref="Bitmap"/> (deep pixel copy) so there is no HICON lifetime
    /// hazard. Uses SHGFI_LARGEICON so items render as desktop-style icon+label grids.
    /// For well-known system icons (此电脑/回收站/控制面板), uses SHGetStockIconInfo (with the
    /// SHGSI_ICON flag) which reliably resolves them even when SHGetFileInfo fails on ::CLSID paths.
    /// Returns null if extraction fails (caller should fall back to text-only).</summary>
    private System.Drawing.Bitmap? GetFileIcon(string fullPath)
    {
        if (string.IsNullOrEmpty(fullPath)) return null;
        if (_iconCache.TryGetValue(fullPath, out var cached)) return cached;
        try
        {
            // ---- System icons (::CLSID paths) ----
            // PRIMARY (version-correct & authoritative): read the icon source straight from the
            // CLSID's registered DefaultIcon — this is exactly how Explorer resolves these icons,
            // so it is correct for every Windows version. The registry value is "dll,index" where
            // index is NEGATIVE for a resource ID; ExtractIconEx accepts the negative value directly.
            // SHGetStockIconInfo is kept only as a last-resort fallback (it is unreliable in some
            // process contexts — e.g. it returns E_INVALIDARG here).
            bool isSystemIcon = false;
            int siid = 0;
            if (fullPath.Equals("::{20D04FE0-3AEA-1069-A2D8-08002B30309D}", StringComparison.OrdinalIgnoreCase))
                { siid = FenceNative.SIID_COMPUTER; isSystemIcon = true; }    // 此电脑
            else if (fullPath.Equals("::{645FF040-5081-101B-9F08-00AA002F954E}", StringComparison.OrdinalIgnoreCase))
                { siid = FenceNative.SIID_RECYCLEBIN; isSystemIcon = true; }  // 回收站
            else if (fullPath.Equals("::{21EC2020-3AEA-1069-A2DD-08002B30309D}", StringComparison.OrdinalIgnoreCase))
                { siid = FenceNative.SIID_CONTROLPANEL; isSystemIcon = true; } // 控制面板

            if (isSystemIcon)
            {
                // Primary: registry DefaultIcon -> ExtractIconEx (signed index)
                if (TryGetStockIconFromRegistry(fullPath, out var regDll, out var regIdx) && File.Exists(regDll))
                {
                    IntPtr hLarge = IntPtr.Zero, hSmall = IntPtr.Zero;
                    int n = FenceNative.ExtractIconEx(regDll, regIdx, out hLarge, out hSmall, 1);
                    if (n > 0 && hLarge != IntPtr.Zero)
                    {
                        var bmp = IconToBitmap(hLarge);
                        FenceNative.DestroyIcon(hLarge);
                        if (hSmall != IntPtr.Zero) FenceNative.DestroyIcon(hSmall);
                        if (bmp != null) { _iconCache[fullPath] = bmp; return bmp; }
                    }
                    HostLog.Write($"FenceLayer 注册表图标 ExtractIconEx 失败: dll={regDll} idx={regIdx} n={n}");
                }
                else
                {
                    HostLog.Write($"FenceLayer 未从注册表取到系统图标: path={fullPath}");
                }

                // Last-resort fallback: SHGetStockIconInfo (official API)
                var sii = new FenceNative.SHSTOCKICONINFO { cbSize = Marshal.SizeOf<FenceNative.SHSTOCKICONINFO>() };
                int hr = FenceNative.SHGetStockIconInfo(siid, FenceNative.SHGSI_ICON | FenceNative.SHGSI_LARGEICON, out sii);
                if (hr == 0 && sii.hIcon != IntPtr.Zero)
                {
                    var bmp = IconToBitmap(sii.hIcon);
                    FenceNative.DestroyIcon(sii.hIcon);
                    if (bmp != null) { _iconCache[fullPath] = bmp; return bmp; }
                }
                else
                {
                    HostLog.Write($"FenceLayer SHGetStockIconInfo 也失败(hr=0x{hr:X8} siid={siid})");
                }
            }

            // Regular file: SHGetFileInfo (works for .lnk, .exe, folders, etc.)
            var sfi = new FenceNative.SHFILEINFO();
            uint flags = FenceNative.SHGFI_ICON | FenceNative.SHGFI_LARGEICON;
            IntPtr ret = FenceNative.SHGetFileInfo(fullPath, 0, ref sfi, (uint)Marshal.SizeOf<FenceNative.SHFILEINFO>(), flags);
            if (sfi.hIcon != IntPtr.Zero)
            {
                var bmp = IconToBitmap(sfi.hIcon);
                FenceNative.DestroyIcon(sfi.hIcon);
                if (bmp != null) { _iconCache[fullPath] = bmp; return bmp; }
            }

            bool exists = File.Exists(fullPath) || Directory.Exists(fullPath);
            HostLog.Write($"FenceLayer GetFileIcon 无图标: path={fullPath} exists={exists} SHGFI_ret=0x{ret.ToInt64():X8}");
        }
        catch (Exception ex)
        {
            HostLog.Write($"FenceLayer GetFileIcon 异常: path={fullPath}", ex);
        }
        return null;
    }

    /// <summary>Convert a shell HICON to a managed Bitmap (deep copy of pixels) so the shell's
    /// HICON can be safely DestroyIcon'd immediately and the bitmap has no handle lifetime hazards.
    /// This is the Fences-style robust pattern — caching the raw Icon/Clone shares the HICON and
    /// breaks once the original is disposed.</summary>
    private static System.Drawing.Bitmap? IconToBitmap(IntPtr hIcon)
    {
        try
        {
            using var icon = System.Drawing.Icon.FromHandle(hIcon);
            return icon.ToBitmap();
        }
        catch { return null; }
    }

    /// <summary>Resolve a well-known system CLSID path (e.g. "::{20D04FE0-...}") to its registered
    /// icon source. Reads HKCR\CLSID\{guid}\DefaultIcon whose default value is formatted
    /// "dllpath,index" (index is NEGATIVE for a resource ID on stock Windows icons). This mirrors
    /// exactly how Explorer resolves 此电脑 / 回收站 / 控制面板, so the result is always the
    /// correct icon for the current OS version.</summary>
    private static bool TryGetStockIconFromRegistry(string clsidPath, out string? dllPath, out int iconIndex)
    {
        dllPath = null;
        iconIndex = 0;
        try
        {
            var guid = clsidPath.TrimStart(':', '{').TrimEnd('}');
            if (!Guid.TryParse(guid, out _)) return false;
            using var key = Registry.ClassesRoot.OpenSubKey($@"CLSID\{{{guid}}}\DefaultIcon");
            var raw = key?.GetValue("") as string;
            if (string.IsNullOrWhiteSpace(raw)) return false;

            // "C:\WINDOWS\System32\imageres.dll,-109"  (may be quoted)
            raw = raw.Trim().Trim('"');
            int comma = raw.LastIndexOf(',');
            if (comma < 0) { dllPath = raw; iconIndex = 0; return !string.IsNullOrEmpty(dllPath); }

            dllPath = raw.Substring(0, comma).Trim().Trim('"');
            if (!int.TryParse(raw.Substring(comma + 1).Trim(), out iconIndex)) iconIndex = 0;
            return !string.IsNullOrEmpty(dllPath);
        }
        catch (Exception ex)
        {
            HostLog.Write($"FenceLayer 读取系统图标注册表失败: path={clsidPath}", ex);
            return false;
        }
    }

    private void ClearIconCache()
    {
        foreach (var kv in _iconCache) kv.Value?.Dispose();
        _iconCache.Clear();
    }

    /// <summary>Convert a straight-alpha ARGB bitmap to premultiplied-alpha (PARGB) in place.
    /// Required before <see cref="Bitmap.GetHbitmap"/> + UpdateLayeredWindow(AC_SRC_ALPHA), which
    /// expects premultiplied source pixels. Without this, DWM re-premultiplies and fine-alpha icon
    /// edges come out dark/invisible.</summary>
    private static void PremultiplyAlpha(Bitmap bmp)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                byte* ptr = (byte*)data.Scan0;
                int bytes = data.Stride * bmp.Height;
                for (int i = 0; i < bytes; i += 4)
                {
                byte a = ptr[i + 3];
                if (a == 255) continue; // fully opaque needs no change
                if (a == 0)
                {
                    // Alpha=0 must have RGB=(0,0,0) for correct premultiplied format.
                    // Non-zero RGB with alpha=0 can leak as white/garbage in some DWM paths.
                    ptr[i] = 0;     // B
                    ptr[i + 1] = 0; // G
                    ptr[i + 2] = 0; // R
                    continue;
                }
                    ptr[i]     = (byte)(ptr[i]     * a / 255); // B
                    ptr[i + 1] = (byte)(ptr[i + 1] * a / 255); // G
                    ptr[i + 2] = (byte)(ptr[i + 2] * a / 255); // R
                }
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    /// <summary>
    /// v13: Fill body background pixels directly via LockBits — BEFORE GDI+ touches the bitmap.
    /// This completely bypasses GDI+'s unreliable low-alpha SolidBrush compositing (which renders
    /// dark fills as white/pale gray when alpha &lt; ~60 on Format32bppArgb bitmaps).
    /// We write exact ARGB values into pixel memory: each body-region pixel becomes
    /// (B=22, G=28, R=20, A=BodyOpacity). When BodyOpacity=0, nothing is written (bitmap
    /// stays transparent from initial allocation). GDI+ then draws headers, borders, icons,
    /// and text ON TOP — their opacity is never affected by body transparency.
    /// </summary>
    private void FillBodyPixels(Bitmap bmp)
    {
        int bodyA = _appearance.BodyOpacity;
        // v14: GetHbitmap() does NOT preserve alpha=0 correctly — DWM renders alpha=0 pixels
        // as white/opaque. Fix: use alpha=1 (0.4% opacity, imperceptible but non-zero) instead
        // of true zero. This is the "minimum visible alpha" that survives GetHbitmap → DWM.
        if (bodyA <= 0) bodyA = 1;

        int rPx = (int)Math.Round(_appearance.CornerRadius * _dpiX);
        int headerH = (int)Math.Round(HeaderHeight * _dpiY);

        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                byte* ptr = (byte*)data.Scan0;
                int stride = data.Stride;

                for (int bi = 0; bi < _boxRects.Count; bi++)
                {
                    var b = _boxRects[bi];
                    int bx = b.Left, by = b.Top;
                    int bw = b.Right - b.Left, bh = b.Bottom - b.Top;
                    if (bw <= 0 || bh <= 0) continue;

                    int r = Math.Min(rPx, Math.Min(bw, bh) / 2);

                    // Body region: full box rectangle MINUS the header strip at the top.
                    int bodyTop = by + headerH;
                    int bodyH = bh - headerH;
                    if (bodyH <= 0) continue;

                    int startX = Math.Max(bx, 0);
                    int startY = Math.Max(bodyTop, 0);
                    int endX = Math.Min(bx + bw, bmp.Width);
                    int endY = Math.Min(bodyTop + bodyH, bmp.Height);

                    for (int y = startY; y < endY; y++)
                    {
                        for (int x = startX; x < endX; x++)
                        {
                            bool inside;
                            int relX = x - bx;
                            int relY = y - by;

                            if (relY >= bh - r && r > 0)
                            {
                                if (relX < r)
                                {
                                    double dx = relX - r;
                                    double dy = relY - (bh - r);
                                    inside = (dx * dx + dy * dy) <= (long)r * r;
                                }
                                else if (relX >= bw - r)
                                {
                                    double dx = relX - (bw - r);
                                    double dy = relY - (bh - r);
                                    inside = (dx * dx + dy * dy) <= (long)r * r;
                                }
                                else
                                {
                                    inside = true;
                                }
                            }
                            else
                            {
                                inside = true;
                            }

                            if (inside)
                            {
                                int offset = y * stride + x * 4;
                                ptr[offset]     = 22;        // B
                                ptr[offset + 1] = 28;        // G
                                ptr[offset + 2] = 20;        // R
                                ptr[offset + 3] = (byte)bodyA; // A = BodyOpacity
                            }
                        }
                    }
                }
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    /// <summary>M4-B frosted glass: capture the desktop region directly behind the fence window and
    /// blur it, so a semi-transparent box reveals a frosted backdrop instead of flat dark. The capture
    /// is cached for the whole Frosted session and reused across repaints (and during drag) — only
    /// re-captured on DPI/display change or when Frosted is (re)enabled, so it cannot run away into a
    /// feedback loop. Falls back silently (no backdrop) if the screen grab fails.</summary>
    private void EnsureFrostCapture()
    {
        if (!_appearance.Frosted) { InvalidateFrost(); return; }
        if (_frostBmp != null) return;       // reuse cached backdrop
        if (_hwnd == IntPtr.Zero || _winW <= 0 || _winH <= 0) return;
        try
        {
            NativeMethods.GetWindowRect(_hwnd, out var wr);
            using var raw = new Bitmap(_winW, _winH, PixelFormat.Format32bppArgb);
            using (var gc = Graphics.FromImage(raw))
            {
                // CopyFromScreen grabs the final composited screen (wallpaper + icons) at our window's
                // screen position. This does NOT include our own (transparent) layered frame, so there
                // is no self-capture feedback.
                gc.CopyFromScreen(wr.Left, wr.Top, 0, 0, new Size(_winW, _winH));
            }
            int radius = (int)Math.Round(20 * _dpiX); // ~20 logical px blur kernel
            var blurred = BoxBlur(raw, radius);
            // Triple-pass for Gaussian-like quality (box blur × 3 ≈ Gaussian)
            _frostBmp = BoxBlur(BoxBlur(blurred, radius), radius);
            blurred.Dispose();
            HostLog.Write($"FenceLayer.EnsureFrostCapture：毛玻璃背景已捕获并模糊 size={_winW}x{_winH} radius={radius}");
        }
        catch (Exception ex)
        {
            HostLog.Write("FenceLayer.EnsureFrostCapture 失败（回落普通半透明）", ex);
            InvalidateFrost();
        }
    }

    /// <summary>Separable box blur (two passes) approximating a Gaussian. Alpha is forced to 255 so the
    /// captured backdrop is fully opaque (the per-box body alpha is applied later by the caller).</summary>
    private static Bitmap BoxBlur(Bitmap src, int radius)
    {
        if (radius <= 0) return (Bitmap)src.Clone();
        int w = src.Width, h = src.Height;
        var rect = new Rectangle(0, 0, w, h);
        var sData = src.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var dData = dst.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                int stride = sData.Stride;
                byte* src0 = (byte*)sData.Scan0;
                byte* dst0 = (byte*)dData.Scan0;
                for (int i = 0, n = stride * h; i < n; i++) dst0[i] = src0[i];

                // Horizontal pass (per channel: B,G,R), then vertical pass.
                byte[] lineB = new byte[Math.Max(w, h)];
                byte[] lineG = new byte[Math.Max(w, h)];
                byte[] lineR = new byte[Math.Max(w, h)];
                for (int y = 0; y < h; y++)
                {
                    byte* p = dst0 + y * stride;
                    for (int x = 0; x < w; x++) { lineB[x] = p[x * 4]; lineG[x] = p[x * 4 + 1]; lineR[x] = p[x * 4 + 2]; }
                    BlurLine(lineB, w, radius); BlurLine(lineG, w, radius); BlurLine(lineR, w, radius);
                    for (int x = 0; x < w; x++) { p[x * 4] = lineB[x]; p[x * 4 + 1] = lineG[x]; p[x * 4 + 2] = lineR[x]; p[x * 4 + 3] = 255; }
                }
                for (int x = 0; x < w; x++)
                {
                    byte* p = dst0 + x * 4;
                    for (int y = 0; y < h; y++) { lineB[y] = p[y * stride]; lineG[y] = p[y * stride + 1]; lineR[y] = p[y * stride + 2]; }
                    BlurLine(lineB, h, radius); BlurLine(lineG, h, radius); BlurLine(lineR, h, radius);
                    for (int y = 0; y < h; y++) { p[y * stride] = lineB[y]; p[y * stride + 1] = lineG[y]; p[y * stride + 2] = lineR[y]; }
                }
            }
        }
        finally
        {
            src.UnlockBits(sData);
            dst.UnlockBits(dData);
        }
        return dst;
    }

    /// <summary>In-place box blur of a single channel line using a prefix-sum sliding window (O(n)).</summary>
    private static void BlurLine(byte[] a, int n, int radius)
    {
        if (radius <= 0 || n <= 1) return;
        int[] pre = new int[n + 1];
        for (int i = 0; i < n; i++) pre[i + 1] = pre[i] + a[i];
        byte[] outp = new byte[n];
        for (int i = 0; i < n; i++)
        {
            int l = Math.Max(0, i - radius);
            int r = Math.Min(n - 1, i + radius);
            int cnt = r - l + 1;
            outp[i] = (byte)((pre[r + 1] - pre[l]) / cnt);
        }
        for (int i = 0; i < n; i++) a[i] = outp[i];
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
        using var path = HeaderPath(x, y, w, h, r);
        g.FillPath(brush, path);
    }

    /// <summary>Header <see cref="GraphicsPath"/>: top corners rounded, bottom edge square.</summary>
    private static GraphicsPath HeaderPath(int x, int y, int w, int h, int r)
    {
        r = Math.Min(r, w / 2);
        var path = new GraphicsPath();
        path.AddArc(x, y, r, r, 180, 90);
        path.AddArc(x + w - r, y, r, r, 270, 90);
        path.AddLine(x + w, y + r, x + w, y + h);
        path.AddLine(x + w, y + h, x, y + h);
        path.AddLine(x, y + h, x, y + r);
        path.CloseFigure();
        return path;
    }

    /// <summary>
    /// Reliable low-alpha fill that bypasses GDI+'s buggy low-alpha <see cref="SolidBrush"/> on a
    /// 32bpp-ARGB bitmap (which renders faint dark fills as pale gray/white). The shape is drawn at
    /// FULL opacity onto a cached temp bitmap, then alpha-blended back onto <paramref name="g"/> with a
    /// <see cref="ColorMatrix"/> that scales the whole image's alpha to <paramref name="alpha"/>/255.
    /// ColorMatrix alpha scaling is reliable at every value, so the box fades to transparent correctly
    /// instead of going white when the user lowers the opacity slider.
    /// </summary>
    private void FillRoundedRectWithAlpha(Graphics g, int x, int y, int w, int h, int r,
        int alpha, byte cr, byte cg, byte cb)
    {
        if (alpha <= 3 || w <= 0 || h <= 0) return;
        EnsureAlphaTmp(w, h);
        using (var tg = Graphics.FromImage(_alphaFillTmp!))
        {
            tg.Clear(Color.FromArgb(0, 0, 0, 0));
            tg.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var path = RoundedRectPath(0, 0, w, h, r);
            using var solid = new SolidBrush(Color.FromArgb(255, cr, cg, cb));
            tg.FillPath(solid, path);
        }
        DrawTmpWithAlpha(g, x, y, w, h, alpha);
    }

    /// <summary>Same reliable fill as <see cref="FillRoundedRectWithAlpha"/> but for the header shape
    /// (rounded top, square bottom).</summary>
    private void FillHeaderWithAlpha(Graphics g, int x, int y, int w, int h, int r,
        int alpha, byte cr, byte cg, byte cb)
    {
        if (alpha <= 3 || w <= 0 || h <= 0) return;
        EnsureAlphaTmp(w, h);
        using (var tg = Graphics.FromImage(_alphaFillTmp!))
        {
            tg.Clear(Color.FromArgb(0, 0, 0, 0));
            tg.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var path = HeaderPath(0, 0, w, h, r);
            using var solid = new SolidBrush(Color.FromArgb(255, cr, cg, cb));
            tg.FillPath(solid, path);
        }
        DrawTmpWithAlpha(g, x, y, w, h, alpha);
    }

    /// <summary>Grow (or allocate) the shared temp bitmap so it is at least w×h.</summary>
    private void EnsureAlphaTmp(int w, int h)
    {
        if (_alphaFillTmp != null && _alphaFillTmp.Width >= w && _alphaFillTmp.Height >= h) return;
        int nw = Math.Max(_alphaFillTmp?.Width ?? 0, w);
        int nh = Math.Max(_alphaFillTmp?.Height ?? 0, h);
        _alphaFillTmp?.Dispose();
        _alphaFillTmp = new Bitmap(nw, nh, PixelFormat.Format32bppArgb);
    }

    /// <summary>Alpha-blend the cached temp bitmap (its content) onto <paramref name="g"/> at
    /// (x,y) with overall alpha = <paramref name="alpha"/>/255 via a <see cref="ColorMatrix"/>.</summary>
    private void DrawTmpWithAlpha(Graphics g, int x, int y, int w, int h, int alpha)
    {
        var cm = new ColorMatrix(new float[][]
        {
            new float[] { 1, 0, 0, 0, 0 },
            new float[] { 0, 1, 0, 0, 0 },
            new float[] { 0, 0, 1, 0, 0 },
            new float[] { 0, 0, 0, (float)alpha / 255f, 0 },
            new float[] { 0, 0, 0, 0, 1 }
        });
        using var ia = new ImageAttributes();
        ia.SetColorMatrix(cm, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
        g.DrawImage(_alphaFillTmp!,
            new Rectangle(x, y, w, h),
            0, 0, w, h,
            GraphicsUnit.Pixel,
            ia);
    }

    /// <summary>Resize + rebuild + re-region on display / DPI changes so boxes stay aligned and the
    /// click-through stays correct.</summary>
    private void OnDisplayOrDpiChange()
    {
        try
        {
            // Cancel any in-progress drag before re-layout (DPI/monitor change invalidates offsets).
            if (_dragCat != null)
            {
                _dragCat = null;
                if (FenceNative.GetCapture() == _hwnd) FenceNative.ReleaseCapture();
            }

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

            ClearIconCache(); // DPI changed → cached icon sizes are stale
            InvalidateFrost(); // backdrop capture is DPI/size-specific — drop it
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
            InvalidateFrost(); // release the cached frosted backdrop bitmap
        }
        catch (Exception ex)
        {
            HostLog.Write("FenceLayer.Close 失败", ex);
        }
    }

    // ---- M3.30: hide/show overlay + idle activity tracking ----

    /// <summary>The underlying Win32 window handle. Exposed so the desktop double-click hook can
    /// distinguish a double-click on the overlay boxes from one on the bare desktop.</summary>
    public IntPtr Hwnd => _hwnd;

    /// <summary>True when the overlay is hidden (collapsed away). Hidden overlays are fully
    /// click-through and invisible, so they neither intercept input nor obscure the desktop.</summary>
    public bool Hidden => _hidden;

    /// <summary>UTC timestamp of the last user interaction with the overlay. The idle timer in
    /// MainWindow uses this to auto-hide after a period of no activity.</summary>
    public DateTime LastActivityUtc => _lastActivityUtc;

    /// <summary>Record a user interaction so the idle auto-hide timer does not fire prematurely.</summary>
    public void TouchActivity() => _lastActivityUtc = DateTime.UtcNow;

    /// <summary>Hide the overlay (double-click desktop / idle / tray). Fully invisible + click-through.</summary>
    public void HideFences()
    {
        if (_hwnd != IntPtr.Zero && !_hidden)
        {
            NativeMethods.ShowWindow(_hwnd, FenceNative.SW_HIDE);
            _hidden = true;
            HostLog.Write("FenceLayer.HideFences：已隐藏（SW_HIDE）");
        }
    }

    /// <summary>Show the overlay again (double-click desktop / tray). Refreshes the idle timer.</summary>
    public void ShowFences()
    {
        if (_hwnd != IntPtr.Zero && _hidden)
        {
            NativeMethods.ShowWindow(_hwnd, FenceNative.SW_SHOW);
            _hidden = false;
            _lastActivityUtc = DateTime.UtcNow;
            HostLog.Write("FenceLayer.ShowFences：已显示（SW_SHOW）");
        }
    }

    /// <summary>Toggle between hidden and shown.</summary>
    public void ToggleHidden()
    {
        if (_hidden) ShowFences();
        else HideFences();
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
            int fr = (int)Math.Round(_appearance.CornerRadius * _dpiX);
            foreach (var b in _boxRects)
            {
                IntPtr rgn = FenceNative.CreateRoundRectRgn(b.Left, b.Top, b.Right, b.Bottom, fr, fr);
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
                int ftr = (int)Math.Round(_appearance.CornerRadius * _dpiX);
                IntPtr trgn = FenceNative.CreateRoundRectRgn(t.Left, t.Top, t.Right, t.Bottom, ftr, ftr);
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
    // ------------------------------------------------------------------
    // M3 desktop-icon → box import support (called by the right-click "Import desktop icons" menu)
    // ------------------------------------------------------------------

    /// <summary>Box rectangles in FENCE-CLIENT coordinates (screen minus virtual origin). The drop proxy
    /// is positioned at the virtual origin, so its client coords match these exactly — no conversion
    /// needed when painting drop highlights.</summary>
    internal List<BoxRect> GetDropBoxRects() => _boxRects;

    /// <summary>Resolve a SCREEN-space point to the category index of the box under it, or -1.</summary>
    public int HitTestScreen(int screenX, int screenY)
    {
        int cx = screenX - (int)Math.Round(_virtualLeft);
        int cy = screenY - (int)Math.Round(_virtualTop);
        for (int i = _boxRects.Count - 1; i >= 0; i--)
        {
            var b = _boxRects[i];
            if (cx >= b.Left && cx <= b.Right && cy >= b.Top && cy <= b.Bottom)
                return b.CategoryIndex;
        }
        return -1;
    }

    /// <summary>Add dropped desktop file paths to a category's member list (dedup), refresh + persist.</summary>
    public void AddPathsToBox(int catIndex, IList<string> paths)
    {
        if (_layout == null || catIndex < 0 || catIndex >= _layout.Categories.Count) return;
        var cat = _layout.Categories[catIndex];
        int added = 0;
        foreach (var p in paths)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            // M3.28: Check ALL categories to prevent duplicates across boxes.
            if (cat.MemberPaths.Contains(p)) continue;
            bool existsElsewhere = false;
            foreach (var other in _layout.Categories)
            {
                if (other != cat && other.MemberPaths.Contains(p))
                { existsElsewhere = true; break; }
            }
            if (existsElsewhere) continue;
            cat.MemberPaths.Add(p);
            added++;
        }
        if (added == 0) return;
        BuildBoxes();
        ApplyRegion();
        UpdateVisual();
        try { FenceStore.Current.Save(_layout); } catch (Exception ex) { HostLog.Write("FenceLayer 桌面图标拖入落盘失败", ex); }
        HostLog.Write($"FenceLayer 从桌面拖入 {added} 个图标 → {cat.DisplayName}");
    }

    /// <summary>Collect all file paths from BOTH the per-user and public Desktop directories,
    /// plus the 3 well-known SYSTEM virtual icons (此电脑/回收站/控制面板) that live in the
    /// Shell Namespace (registry HKLM\...\Desktop\NameSpace) rather than any filesystem folder.
    /// Skips hidden/system files like desktop.ini.</summary>
    private static string[] GetAllDesktopFilePaths()
    {
        var result = new List<string>();
        foreach (var folder in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
        })
        {
            if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
                foreach (var f in Directory.GetFiles(folder))
                {
                    // Skip hidden/system files (desktop.ini, thumbs.db, etc.) — they are not user icons.
                    var attr = File.GetAttributes(f);
                    if ((attr & FileAttributes.Hidden) != 0 || (attr & FileAttributes.System) != 0)
                        continue;
                    result.Add(f);
                }
        }
        // System desktop icons — NOT files on disk; they are Shell Namespace items rendered by Explorer.
        // Use ::{CLSID} syntax which Windows resolves via Process.Start(UseShellExecute=true).
        result.Add("::{20D04FE0-3AEA-1069-A2D8-08002B30309D}"); // 此电脑 (This PC)
        result.Add("::{645FF040-5081-101B-9F08-00AA002F954E}");   // 回收站 (Recycle Bin)
        result.Add("::{21EC2020-3AEA-1069-A2DD-08002B30309D}");   // 控制面板 (Control Panel)
        return result.ToArray();
    }

    /// <summary>Import the ENTIRE Desktop (both per-user AND public) into a category in one tap.</summary>
    private void ImportEntireDesktop(int catIndex)
    {
        if (_layout == null || catIndex < 0 || catIndex >= _layout.Categories.Count) return;
        var paths = GetAllDesktopFilePaths();
        HostLog.Write($"FenceLayer 导入桌面全部图标：扫描到 {paths.Length} 个文件（含公共桌面）");
        AddPathsToBox(catIndex, paths);
    }

    /// <summary>Open a multi-select file dialog (seeded at the Desktop) and import the chosen files.</summary>
    private void ImportDesktopFiles(int catIndex)
    {
        if (_layout == null || catIndex < 0 || catIndex >= _layout.Categories.Count) return;
        string desk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择要加入围栏的桌面图标",
            InitialDirectory = desk,
            Multiselect = true,
            CheckFileExists = true
        };
        try
        {
            // No owner window: OpenFileDialog uses the desktop as owner and runs its own modal loop.
            if (dlg.ShowDialog() == true)
                AddPathsToBox(catIndex, dlg.FileNames);
        }
        catch (Exception ex)
        {
            HostLog.Write("FenceLayer.ImportDesktopFiles 失败", ex);
        }
    }

    internal readonly struct BoxRect
    {
        public int Left { get; }
        public int Top { get; }
        public int Right { get; }
        public int Bottom { get; }
        public string Name { get; }
        public bool Collapsed { get; }
        public string? IconRef { get; }
        public List<string> Items { get; }   // basenames, for display
        public List<string> Paths { get; }   // full paths, for opening on double-click
        public int CategoryIndex { get; }    // index into _layout.Categories

        public BoxRect(int left, int top, int right, int bottom, string name, bool collapsed,
            string? iconRef, List<string> items, List<string> paths, int categoryIndex)
        {
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
            Name = name;
            Collapsed = collapsed;
            IconRef = iconRef;
            Items = items;
            Paths = paths;
            CategoryIndex = categoryIndex;
        }
    }
}
