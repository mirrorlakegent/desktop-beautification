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
/// The transparent, click-through window that hosts the fence boxes.
///
/// Layout model (chosen for Phase 2/3 simplicity, per the design brief):
///  - The window covers the ENTIRE virtual screen (Left/Top/Width/Height from
///    <see cref="SystemParameters"/>, which are DPI-correct logical units).
///  - A single <see cref="Canvas"/> fills the window; each box is placed at
///    (VirtualToLogical(Category.X), VirtualToLogical(Category.Y)).
///  - Click-through is achieved with <see cref="FenceNative.SetWindowRgn"/>: the union of every box's
///    rectangle (minus any collapsed box's body) is applied to the window, so clicks outside any box
///    fall through to the native desktop; clicks on a box hit the window (double-click opens files,
///    header drag moves the box).
///
/// Persistence: every user edit mutates <see cref="_layout"/> (the same instance shown by
/// MainWindow.EnableFences) and calls <see cref="FenceStore.Current"/>.Save — never recreating this
/// Window. The boxes are rebuilt (cheap Canvas children) on recategorize / add, but the desktop-child
/// Window itself is created exactly once via <see cref="Show"/>.
///
/// Known Phase limitations (unchanged, not regressed): DPI is approximately correct off 96-DPI;
/// multi-monitor clamping is deferred; no thumbnails; no Source heuristic (so 临时 stays empty).
/// </summary>
public sealed class FenceLayer : Window
{
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

    // display / DPI change hook
    private HwndSource? _hwndSourceLayer;
    private const uint WM_DISPLAYCHANGE = 0x007E;
    private const uint WM_DPICHANGED = 0x02E0;

    /// <summary>Header height (logical) used for the click region when a box is collapsed.</summary>
    private const double HeaderHeight = 28;

    public FenceLayer()
    {
        WindowStyle = WindowStyle.None;
        // Fences is mounted as a CHILD of the desktop shell (see MountToDesktop -> SetParent).
        // A WS_EX_LAYERED window that is reparented to become a child is NOT painted by DWM, which
        // is exactly why the boxes never appeared while the native icons were correctly hidden.
        // The proven pattern in this codebase (WorkerWHost) is a normal non-layered borderless window,
        // so we must NOT use AllowsTransparency here.
        AllowsTransparency = false;
        // Opaque dark background. Because ApplyRegion() clips the window (via SetWindowRgn) to the
        // union of box rectangles, this brush only ever shows inside a box — and the box paints over
        // it — so it is effectively invisible. A non-layered window has no alpha channel, so
        // Brushes.Transparent would render as solid BLACK behind the boxes (the original bug).
        Background = new SolidColorBrush(Color.FromRgb(20, 22, 28));
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = false;

        _virtualLeft = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
        _virtualTop = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        Content = _canvas;
        _addTile = BuildAddTile();
        SourceInitialized += OnSourceInitialized;
    }

    /// <summary>Initial build entry (called once). Subsequent edits use the Refresh* methods below.</summary>
    public void Show(DesktopIconItem[] items, FenceLayout layout)
    {
        _layout = layout;
        _items = items;
        BuildBoxes(items);
        base.Show();
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

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        try
        {
            var source = PresentationSource.FromVisual(this) as HwndSource;
            if (source?.CompositionTarget != null)
            {
                _dpiX = source.CompositionTarget.TransformToDevice.M11;
                _dpiY = source.CompositionTarget.TransformToDevice.M22;
            }
            // Install a hook for display / DPI changes so boxes stay aligned and click-through stays
            // correct when the virtual screen or scaling changes. Removed in OnClosed.
            _hwndSourceLayer = source;
            _hwndSourceLayer?.AddHook(WndProcLayer);
        }
        catch
        {
            _dpiX = _dpiY = 1.0;
        }

        // Rebuild once we know the DPI so box offsets are correct.
        if (_layout != null)
            BuildBoxes(_items);

        MountToDesktop();
        ApplyRegion();
    }

    /// <summary>React to display / DPI changes: re-read scaling, resize to the (possibly new) virtual
    /// screen, and rebuild box positions + click region so nothing drifts out of alignment.</summary>
    private IntPtr WndProcLayer(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == (int)WM_DISPLAYCHANGE || msg == (int)WM_DPICHANGED)
        {
            try
            {
                if (_hwndSourceLayer?.CompositionTarget != null)
                {
                    _dpiX = _hwndSourceLayer.CompositionTarget.TransformToDevice.M11;
                    _dpiY = _hwndSourceLayer.CompositionTarget.TransformToDevice.M22;
                }
                // Re-cover the (possibly changed) virtual screen, both DPI-correct placement and the
                // physical origin used by the click region.
                _virtualLeft = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
                _virtualTop = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
                Left = SystemParameters.VirtualScreenLeft;
                Top = SystemParameters.VirtualScreenTop;
                Width = SystemParameters.VirtualScreenWidth;
                Height = SystemParameters.VirtualScreenHeight;

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

    protected override void OnClosed(EventArgs e)
    {
        // Remove the display/DPI hook so it does not dangle on a destroyed HWND.
        if (_hwndSourceLayer != null)
        {
            try { _hwndSourceLayer.RemoveHook(WndProcLayer); } catch { }
            _hwndSourceLayer = null;
        }
        base.OnClosed(e);
    }

    /// <summary>Reparent under the desktop host for correct z-order; degrade silently on any failure.</summary>
    private void MountToDesktop()
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            IntPtr defView = DesktopShell.FindDefView();
            IntPtr host = defView != IntPtr.Zero ? FenceNative.GetParent(defView) : IntPtr.Zero;

            if (host != IntPtr.Zero)
            {
                NativeMethods.SetParent(hwnd, host);
                NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOP, 0, 0, 0, 0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE |
                    NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
            }
            else
            {
                HostLog.Write("FenceLayer.MountToDesktop：未找到桌面宿主窗口，降级为顶层透明窗口。");
            }

            // Tool window (no taskbar/alt-tab) + non-activating — applied UNCONDITIONALLY so even the
            // fallback top-level window can never steal focus. Keep the graceful degrade logging above.
            try
            {
                int ex = (int)FenceNative.GetWindowLongPtrW(hwnd, FenceNative.GWL_EXSTYLE).ToInt64();
                ex |= (int)(FenceNative.WS_EX_TOOLWINDOW | FenceNative.WS_EX_NOACTIVATE);
                FenceNative.SetWindowLongPtrW(hwnd, FenceNative.GWL_EXSTYLE, new IntPtr(ex));
            }
            catch (Exception ex)
            {
                HostLog.Write("FenceLayer.MountToDesktop 设置扩展样式失败", ex);
            }
        }
        catch (Exception ex)
        {
            HostLog.Write("FenceLayer.MountToDesktop 失败，降级为顶层窗口", ex);
        }
    }

    /// <summary>Union of all box rectangles (header-only when collapsed) as the hit region → click-through
    /// everywhere else. Coordinates are physical pixels relative to the window's client origin, DPI-scaled
    /// so they align with the rendered (DPI-scaled) boxes.</summary>
    private void ApplyRegion()
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

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
            // to the virtual origin, and the tile's Canvas position is already in logical units.
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
                FenceNative.SetWindowRgn(hwnd, combined.Value, true);
                // SetWindowRgn takes ownership of the region; do not DeleteObject(combined).
            }
            else
            {
                // No boxes (defensive): clear any stale region so the empty window is fully
                // click-through instead of swallowing clicks across its whole rectangle.
                FenceNative.SetWindowRgn(hwnd, IntPtr.Zero, true);
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
