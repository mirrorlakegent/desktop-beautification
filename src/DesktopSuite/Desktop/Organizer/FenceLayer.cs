using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using DesktopSuite.Wallpaper;

namespace DesktopSuite.Desktop.Organizer;

/// <summary>
/// The transparent, click-through container that hosts the fence boxes.
///
/// IMPORTANT (root-cause fix, 2025): this used to be a WPF <see cref="Window"/> that was created
/// top-level and then re-parented onto the desktop shell via <c>SetParent</c> (MountToDesktop). That
/// pattern is unreliable: a WPF <see cref="Window"/>'s <c>HwndTarget</c> is initialised for a TOP-LEVEL
/// window, and once it is turned into a child of the shell, DWM frequently refuses to composite its
/// content — the window is created (icons are correctly hidden) but it never paints, so the desktop
/// goes blank with no fence boxes. The previous "AllowsTransparency=false" change only mattered for
/// layered windows and did NOT fix this, which is exactly why the symptom persisted.
///
/// The fix mirrors the ALREADY-PROVEN wallpaper path (<c>WallpaperChildWindow</c>): instead of
/// re-parenting a WPF Window, we host the WPF visual tree in a RAW child window of the desktop shell
/// via <see cref="HwndSource"/>. The window is created AS a child of the desktop host from the start
/// (never re-parented), so DWM composites it normally and the boxes appear above the (hidden) icons.
///
/// Layout model:
///  - The child window is sized to the desktop host's client rect and placed at (0,0).
///  - A single <see cref="Canvas"/> fills the window; each box is placed at
///    (VirtualToLogical(Category.X), VirtualToLogical(Category.Y)).
///  - Click-through is achieved with <see cref="FenceNative.SetWindowRgn"/>: the union of every box's
///    rectangle (minus any collapsed box's body) plus the "＋ 新建分类" tile is applied to the window,
///    so clicks outside any box fall through to the native desktop; clicks on a box hit the window
///    (double-click opens files, header drag moves the box).
///
/// Persistence: every user edit mutates <see cref="_layout"/> (the same instance shown by
/// MainWindow.EnableFences) and calls <see cref="FenceStore.Current"/>.Save — never recreating this
/// container. The boxes are rebuilt (cheap Canvas children) on recategorize / add, but the desktop
/// child window itself is created exactly once via <see cref="Show"/>.
///
/// Known Phase limitations (unchanged, not regressed): DPI is approximately correct off 96-DPI;
/// multi-monitor clamping is deferred; no thumbnails; no Source heuristic (so 临时 stays empty).
/// </summary>
public sealed class FenceLayer
{
    // Root visual: a dark border that fills the child window; the canvas (with the boxes) sits inside.
    // The window is non-layered, but ApplyRegion() clips it to the box rectangles, so only the boxes
    // are ever painted — everything outside the region is not drawn and clicks fall through.
    private readonly Border _root = new()
    {
        Background = new SolidColorBrush(Color.FromRgb(20, 22, 28)),
        BorderThickness = new Thickness(0)
    };
    private readonly Canvas _canvas = new() { Background = Brushes.Transparent, ClipToBounds = false };
    private readonly Border _addTile;

    private readonly List<(FenceBox Box, FenceCategory Category)> _boxes = new();

    private FenceLayout? _layout;
    private DesktopIconItem[] _items = System.Array.Empty<DesktopIconItem>();
    private double _dpiX = 1.0;
    private double _dpiY = 1.0;
    private double _virtualLeft;
    private double _virtualTop;

    // transient box-drag state
    private FenceBox? _dragBox;
    private bool _dragArmed;
    private Point _dragOffset;

    // desktop child window (raw HWND hosted by HwndSource)
    private HwndSource? _source;
    private IntPtr _hwnd = IntPtr.Zero;

    private const uint WM_DISPLAYCHANGE = 0x007E;
    private const uint WM_DPICHANGED = 0x02E0;

    /// <summary>Header height (logical) used for the click region when a box is collapsed.</summary>
    private const double HeaderHeight = 28;

    public FenceLayer()
    {
        _root.Child = _canvas;
        _addTile = BuildAddTile();
    }

    /// <summary>Initial build entry (called once). Subsequent edits use the Refresh* methods below.</summary>
    public void Show(DesktopIconItem[] items, FenceLayout layout)
    {
        _layout = layout;
        _items = items;
        CreateDesktopWindow();
    }

    /// <summary>
    /// Create the desktop child window (hosted WPF tree) and mount it under the desktop shell.
    /// The window is created AS a child of the desktop host — never re-parented — so DWM composites
    /// it. Falls back gracefully if no host can be found (a plain top-level window still works).
    /// </summary>
    private void CreateDesktopWindow()
    {
        // Re-entrancy guard: the desktop child window is created exactly once. The startup
        // retry path (EnableFences/ApplyFencesWithRetryIfEnabled) and a manual toggle race can
        // otherwise both reach here within a few seconds; without this, the second HwndSource
        // overwrites _source and the first HWND is leaked (never Disposed).
        if (_source != null) return;

        try
        {
            IntPtr host = ResolveDesktopHost();

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

            var ps = new HwndSourceParameters
            {
                // Non-layered child window; click-through is done with SetWindowRgn (see ApplyRegion).
                WindowStyle = unchecked((int)(NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE |
                                              NativeMethods.WS_CLIPSIBLINGS | NativeMethods.WS_CLIPCHILDREN)),
                ExtendedWindowStyle = unchecked((int)(NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE)),
                ParentWindow = host,
                UsesPerPixelOpacity = false,
            };
            ps.SetPosition(0, 0);
            ps.SetSize(w, h);

            _source = new HwndSource(ps);
            _source.RootVisual = _root;
            _hwnd = _source.Handle;

            // Belt-and-suspenders: guarantee a non-layered, tool, non-activating child.
            try
            {
                int ex = (int)FenceNative.GetWindowLongPtrW(_hwnd, FenceNative.GWL_EXSTYLE).ToInt64();
                ex |= (int)(FenceNative.WS_EX_TOOLWINDOW | FenceNative.WS_EX_NOACTIVATE);
                ex &= ~(int)NativeMethods.WS_EX_LAYERED;
                FenceNative.SetWindowLongPtrW(_hwnd, FenceNative.GWL_EXSTYLE, new IntPtr(ex));
            }
            catch (Exception ex)
            {
                HostLog.Write("FenceLayer：设置扩展样式失败", ex);
            }

            // Read DPI + virtual origin now that we have a real HWND.
            if (_source.CompositionTarget != null)
            {
                _dpiX = _source.CompositionTarget.TransformToDevice.M11;
                _dpiY = _source.CompositionTarget.TransformToDevice.M22;
            }
            _virtualLeft = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
            _virtualTop = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);

            // Hook display / DPI changes so boxes stay aligned and click-through stays correct.
            _source.AddHook(WndProcLayer);

            // Build once DPI/origin are known, then clip + show.
            BuildBoxes(_items);
            ApplyRegion();

            // (A) Z-order fix: pin the fence to the TOP of its desktop host so it sits above the
            // native icon layer and is never hidden behind the desktop shell. The wallpaper surface
            // is pinned to HWND_BOTTOM in WorkerWHost, so this also guarantees the fence renders
            // ABOVE the wallpaper. HWND_TOP here only reorders among the host's own children, so
            // normal top-level app windows stay above the fence. This removes the prior reliance on
            // "new child window lands on top" — the ordering is now explicit and stable.
            try
            {
                NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOP, 0, 0, w, h,
                    NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
            }
            catch (Exception ex)
            {
                HostLog.Write("FenceLayer：置顶失败", ex);
            }
        }
        catch (Exception ex)
        {
            HostLog.Write("FenceLayer.CreateDesktopWindow 失败", ex);
        }
    }

    /// <summary>Locate the desktop host window. GetParent(SHELLDLL_DefView) is correct in both shell
    /// shapes (the icon WorkerW in shape A, Progman in shape B — both sit above the wallpaper
    /// WorkerW). Progman is the final fallback. Returns Zero only if the shell is unavailable.</summary>
    private static IntPtr ResolveDesktopHost()
    {
        try
        {
            IntPtr defView = DesktopShell.FindDefView();
            if (defView != IntPtr.Zero)
            {
                IntPtr p = FenceNative.GetParent(defView);
                if (p != IntPtr.Zero) return p;
            }
            IntPtr progman = NativeMethods.FindWindow("Progman", null);
            if (progman != IntPtr.Zero) return progman;
        }
        catch (Exception ex)
        {
            HostLog.Write("FenceLayer.ResolveDesktopHost 失败", ex);
        }
        return IntPtr.Zero;
    }

    private Border BuildAddTile()
    {
        var tile = new Border
        {
            Width = 160,
            Height = 48,
            // Opaque now: under the non-layered window (AllowsTransparency=false) there is no alpha
            // channel, so the old alpha=60 semi-transparent brush would render as a faint/black tile.
            // Drop the alpha; the opaque dark tone matches FenceBox panels.
            Background = new SolidColorBrush(Color.FromRgb(40, 44, 54)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(140, 120, 130, 150)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Cursor = Cursors.Hand,
            ToolTip = "新建一个分类盒"
        };
        var txt = new TextBlock
        {
            Text = "＋ 新建分类",
            Foreground = Brushes.White,
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        tile.Child = txt;
        tile.MouseLeftButtonDown += (_, _) => AddCategory();
        return tile;
    }

    private void BuildBoxes(DesktopIconItem[] items)
    {
        _canvas.Children.Clear();
        _boxes.Clear();
        if (_layout == null) return;

        var byPath = new Dictionary<string, DesktopIconItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in items)
            byPath[it.Path] = it;

        foreach (var cat in _layout.Categories)
        {
            var members = new List<DesktopIconItem>(cat.MemberPaths.Count);
            foreach (var p in cat.MemberPaths)
                if (byPath.TryGetValue(p, out var it))
                    members.Add(it);

            var box = new FenceBox(this, cat, members);
            Canvas.SetLeft(box, VirtualToLogicalX(cat.X));
            Canvas.SetTop(box, VirtualToLogicalY(cat.Y));
            _canvas.Children.Add(box);
            _boxes.Add((box, cat));
        }

        // Place the add tile to the right of the rightmost box.
        double maxRight = 0;
        foreach (var (_, c) in _boxes) maxRight = Math.Max(maxRight, c.X + c.Width);
        double y = _boxes.Count > 0 ? _boxes[0].Category.Y : _virtualTop;
        Canvas.SetLeft(_addTile, VirtualToLogicalX(maxRight + 30));
        Canvas.SetTop(_addTile, VirtualToLogicalY(y));
        _canvas.Children.Add(_addTile);
    }

    private double VirtualToLogicalX(double vx) => (vx - _virtualLeft) / _dpiX;
    private double VirtualToLogicalY(double vy) => (vy - _virtualTop) / _dpiY;
    private double LogicalToVirtualX(double lx) => lx * _dpiX + _virtualLeft;
    private double LogicalToVirtualY(double ly) => ly * _dpiY + _virtualTop;

    private IntPtr WndProcLayer(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == (int)WM_DISPLAYCHANGE || msg == (int)WM_DPICHANGED)
        {
            try
            {
                if (_source?.CompositionTarget != null)
                {
                    _dpiX = _source.CompositionTarget.TransformToDevice.M11;
                    _dpiY = _source.CompositionTarget.TransformToDevice.M22;
                }
                // Re-cover the (possibly changed) virtual screen, both DPI-correct placement and the
                // physical origin used by the click region.
                _virtualLeft = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
                _virtualTop = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);

                // Resize the desktop child window to the new host client rect.
                ResizeToHost();

                if (_layout != null)
                    BuildBoxes(_items);
                ApplyRegion();
            }
            catch (Exception ex)
            {
                HostLog.Write("FenceLayer 显示/DPI 变化处理失败", ex);
            }
            handled = true;
        }
        return IntPtr.Zero;
    }

    /// <summary>Resize the desktop child window to the host's current client rect.</summary>
    private void ResizeToHost()
    {
        if (_hwnd == IntPtr.Zero) return;
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
            NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOP, 0, 0, w, h,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
            NativeMethods.InvalidateRect(_hwnd, IntPtr.Zero, true);
        }
        catch (Exception ex)
        {
            HostLog.Write("FenceLayer.ResizeToHost 失败", ex);
        }
    }

    /// <summary>Destroy the desktop child window. Safe to call multiple times.</summary>
    public void Close()
    {
        try
        {
            if (_source != null)
            {
                _source.RemoveHook(WndProcLayer);
                _source.Dispose();   // destroys the underlying HWND
            }
        }
        catch (Exception ex)
        {
            HostLog.Write("FenceLayer.Close 失败", ex);
        }
        finally
        {
            _source = null;
            _hwnd = IntPtr.Zero;
        }
    }

    /// <summary>Union of all box rectangles (header-only when collapsed) as the hit region → click-through
    /// everywhere else. Coordinates are physical pixels relative to the window's client origin, using the
    /// SAME virtual-origin basis (<see cref="_virtualLeft"/>/<see cref="_virtualTop"/>) that the WPF boxes
    /// are rendered with, so the region always lines up with the boxes regardless of monitor layout.</summary>
    private void ApplyRegion()
    {
        try
        {
            if (_hwnd == IntPtr.Zero) return;

            IntPtr? combined = null;
            foreach (var (_, cat) in _boxes)
            {
                int left = (int)Math.Round(cat.X - _virtualLeft);
                int top = (int)Math.Round(cat.Y - _virtualTop);
                double hLogical = cat.Collapsed ? HeaderHeight : cat.Height;
                int right = left + (int)Math.Round(cat.Width * _dpiX);
                int bottom = top + (int)Math.Round(hLogical * _dpiY);

                IntPtr rgn = FenceNative.CreateRectRgn(left, top, right, bottom);
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
            // The tile sits outside the box union (placed at maxRight+30), so without this it was
            // clipped out by SetWindowRgn and every click on it fell through to the desktop.
            // Same physical-coordinate basis as the boxes: region coords are physical pixels relative
            // to the window origin, and the tile's Canvas position is already in logical units.
            double tlx = Canvas.GetLeft(_addTile);
            double tly = Canvas.GetTop(_addTile);
            if (!double.IsNaN(tlx) && !double.IsNaN(tly))
            {
                int tl = (int)Math.Round(tlx * _dpiX);
                int tt = (int)Math.Round(tly * _dpiY);
                int tr = tl + (int)Math.Round(_addTile.Width * _dpiX);
                int tb = tt + (int)Math.Round(_addTile.Height * _dpiY);
                IntPtr trgn = FenceNative.CreateRectRgn(tl, tt, tr, tb);
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
            }
            else
            {
                // No boxes (defensive): clear any stale region so the empty window is fully
                // click-through instead of swallowing clicks across its whole rectangle.
                FenceNative.SetWindowRgn(_hwnd, IntPtr.Zero, true);
            }
        }
        catch (Exception ex)
        {
            HostLog.Write("FenceLayer.ApplyRegion 失败", ex);
        }
    }

    // ---- Interactive operations (all run on the UI thread; every edit persists) ----

    /// <summary>Drag an item onto a box: record the override, re-classify, rebuild, re-region, persist.</summary>
    public void Recategorize(string path, string targetCatId)
    {
        if (_layout == null) return;
        try
        {
            _layout.Overrides[path] = targetCatId;
            var list = new List<DesktopIconItem>(_items);
            FenceClassifier.Apply(list, _layout.Categories, _layout.Overrides);
            BuildBoxes(_items);
            ApplyRegion();
            FenceStore.Current.Save(_layout);
        }
        catch (Exception ex)
        {
            HostLog.Write("FenceLayer.Recategorize 失败", ex);
        }
    }

    /// <summary>Commit an inline rename of a box's display name and persist.</summary>
    public void RenameCategory(FenceCategory cat, string name)
    {
        if (_layout == null) return;
        try
        {
            cat.DisplayName = name;
            FenceStore.Current.Save(_layout);
        }
        catch (Exception ex)
        {
            HostLog.Write("FenceLayer.RenameCategory 失败", ex);
        }
    }

    /// <summary>Collapse/expand toggled: re-apply the click region (header-only when collapsed) and persist.</summary>
    public void OnCollapsedChanged(FenceBox box)
    {
        try
        {
            ApplyRegion();
            SaveLayout();
        }
        catch (Exception ex)
        {
            HostLog.Write("FenceLayer.OnCollapsedChanged 失败", ex);
        }
    }

    /// <summary>Add a new (empty, non-auto) custom category box and persist.</summary>
    public void AddCategory()
    {
        if (_layout == null) return;
        try
        {
            var cat = new FenceCategory
            {
                Id = Guid.NewGuid().ToString(),
                DisplayName = "新建分类",
                IconRef = "📁",
                Width = 240,
                Height = 280,
                AutoClassify = false
            };
            double maxRight = 0;
            foreach (var c in _layout.Categories) maxRight = Math.Max(maxRight, c.X + c.Width);
            double y = _layout.Categories.Count > 0 ? _layout.Categories[0].Y : _virtualTop;
            cat.X = maxRight == 0 ? _virtualLeft + 24 : maxRight + 30;
            cat.Y = y;
            _layout.Categories.Add(cat);
            BuildBoxes(_items);
            ApplyRegion();
            FenceStore.Current.Save(_layout);
        }
        catch (Exception ex)
        {
            HostLog.Write("FenceLayer.AddCategory 失败", ex);
        }
    }

    public void SaveLayout()
    {
        var layout = _layout;
        if (layout != null)
            FenceStore.Current.Save(layout);
    }

    // ---- Box dragging (header) ----

    /// <summary>Called by a box header's PreviewMouseLeftButtonDown. Arms a drag; actual capture/move
    /// begins on the first mouse move so a double-click (rename) is not hijacked.</summary>
    public void BeginBoxDrag(FenceBox box, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is Button) return; // ignore the collapse button
        _dragBox = box;
        _dragArmed = true;
        var start = e.GetPosition(_canvas);
        _dragOffset = new Point(start.X - Canvas.GetLeft(box), start.Y - Canvas.GetTop(box));
        box.PreviewMouseMove += BoxDragMove;
        box.PreviewMouseUp += BoxDragEnd;
        e.Handled = true;
    }

    private void BoxDragMove(object? sender, MouseEventArgs e)
    {
        if (!_dragArmed || _dragBox == null) return;
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(_canvas);
        double nl = p.X - _dragOffset.X;
        double nt = p.Y - _dragOffset.Y;
        Canvas.SetLeft(_dragBox, nl);
        Canvas.SetTop(_dragBox, nt);
        _dragBox.Category.X = LogicalToVirtualX(nl);
        _dragBox.Category.Y = LogicalToVirtualY(nt);
        ApplyRegion(); // keep click-through aligned while dragging
    }

    private void BoxDragEnd(object? sender, MouseButtonEventArgs e)
    {
        if (_dragBox != null)
        {
            _dragBox.PreviewMouseMove -= BoxDragMove;
            _dragBox.PreviewMouseUp -= BoxDragEnd;
        }
        _dragArmed = false;
        _dragBox = null;
        ApplyRegion();
        SaveLayout();
    }
}
