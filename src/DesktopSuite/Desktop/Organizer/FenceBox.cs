using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using DesktopSuite.Wallpaper;

namespace DesktopSuite.Desktop.Organizer;

/// <summary>
/// One fence box: a header (glyph + display name + collapse button) over a scrollable list of item
/// names. Pure code (no XAML) to keep the build risk-free.
///
/// Interaction (Phase 3, all virtualized — never moves real files):
///  - Drag an item onto another box → <see cref="FenceLayer.Recategorize"/> (writes Override, rebuilds).
///  - Drag the header → <see cref="FenceLayer.BeginBoxDrag"/> (repositions the category, persists).
///  - Double-click the header title → inline rename → <see cref="FenceLayer.RenameCategory"/>.
///  - Collapse button → toggles <see cref="FenceCategory.Collapsed"/>, persists via
///    <see cref="FenceLayer.OnCollapsedChanged"/> (which re-applies the click region so only the
///    header stays clickable).
///  - Double-click an item → launch the REAL file via Process.Start (Fences only records metadata).
///
/// Layout: a <see cref="FenceBox"/> is placed by <see cref="FenceLayer"/> on a Canvas at
/// (VirtualToLogical(Category.X), VirtualToLogical(Category.Y)); its pixel size equals
/// (Category.Width, Category.Height).
/// </summary>
public sealed class FenceBox : UserControl
{
    private readonly FenceLayer _owner;
    private readonly FenceCategory _category;
    private Border _body = null!;
    private DockPanel _headerPanel = null!;
    private TextBlock _title = null!;

    // item drag transient state
    private string? _dragItemPath;
    private Point _dragStartPoint;
    private bool _dragCandidate;

    public FenceCategory Category => _category;

    public FenceBox(FenceLayer owner, FenceCategory category, IReadOnlyList<DesktopIconItem> items)
    {
        _owner = owner;
        _category = category;

        var root = new Grid
        {
            Background = new SolidColorBrush(Color.FromArgb(150, 20, 22, 28))
        };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) }); // header
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // body

        // ---- Header (drag handle + rename target + collapse button) ----
        var header = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(200, 40, 44, 54)),
            Padding = new Thickness(6, 2, 6, 2)
        };
        header.PreviewMouseLeftButtonDown += (s, e) => _owner.BeginBoxDrag(this, e);

        _headerPanel = new DockPanel();
        _title = new TextBlock
        {
            Text = HeaderText(),
            Foreground = Brushes.White,
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center
        };
        _title.MouseLeftButtonDown += (s, e) =>
        {
            if (e.ClickCount == 2) { StartRename(); e.Handled = true; }
        };
        DockPanel.SetDock(_title, Dock.Left);

        var collapseBtn = new Button
        {
            Content = category.Collapsed ? "▸" : "▾",
            Width = 22,
            Height = 22,
            Padding = new Thickness(0),
            Foreground = Brushes.White,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0)
        };
        collapseBtn.Click += (_, _) =>
        {
            category.Collapsed = !category.Collapsed;
            collapseBtn.Content = category.Collapsed ? "▸" : "▾";
            _body.Visibility = category.Collapsed ? Visibility.Collapsed : Visibility.Visible;
            _owner.OnCollapsedChanged(this); // persist + re-apply click region
        };
        DockPanel.SetDock(collapseBtn, Dock.Right);
        _headerPanel.Children.Add(_title);
        _headerPanel.Children.Add(collapseBtn);
        header.Child = _headerPanel;
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        // ---- Body: list of items ----
        _body = new Border { Padding = new Thickness(6) };
        var list = new ItemsControl
        {
            ItemsSource = items,
            ItemTemplate = BuildItemTemplate()
        };
        var scroll = new ScrollViewer
        {
            Content = list,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = Brushes.Transparent,
            Foreground = Brushes.White
        };
        _body.Child = scroll;
        Grid.SetRow(_body, 1);
        root.Children.Add(_body);
        if (category.Collapsed) _body.Visibility = Visibility.Collapsed;

        Content = root;
        Width = Math.Max(80, category.Width);
        Height = Math.Max(60, category.Height);
        Background = Brushes.Transparent;

        // Drop target for item recategorization.
        AllowDrop = true;
        DragOver += OnDragOver;
        Drop += OnDrop;
    }

    private string HeaderText() =>
        $"{(string.IsNullOrEmpty(_category.IconRef) ? "" : _category.IconRef + " ")}{_category.DisplayName}";

    // ---- Inline rename (double-click header title) ----
    private void StartRename()
    {
        var tb = new TextBox
        {
            Text = _category.DisplayName,
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)),
            BorderThickness = new Thickness(0),
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Width = Math.Max(80, _title.ActualWidth)
        };
        DockPanel.SetDock(tb, Dock.Left);
        _headerPanel.Children.Remove(_title);
        _headerPanel.Children.Insert(0, tb);
        tb.Focus();
        tb.SelectAll();
        tb.LostFocus += (_, _) => CommitRename(tb);
        tb.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { CommitRename(tb); e.Handled = true; }
            else if (e.Key == Key.Escape) { RestoreTitle(tb); e.Handled = true; }
        };
    }

    private void CommitRename(TextBox tb)
    {
        string name = tb.Text.Trim();
        RestoreTitle(tb);
        if (!string.IsNullOrEmpty(name) && name != _category.DisplayName)
        {
            _category.DisplayName = name;
            _owner.RenameCategory(_category, name);
        }
    }

    private void RestoreTitle(TextBox tb)
    {
        if (tb.Parent is DockPanel dp) dp.Children.Remove(tb);
        if (!_headerPanel.Children.Contains(_title)) _headerPanel.Children.Insert(0, _title);
        _title.Text = HeaderText();
    }

    // ---- Item drag-to-recategorize ----
    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent("fence/item"))
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent("fence/item"))
        {
            var path = (string)e.Data.GetData("fence/item");
            _owner.Recategorize(path, _category.Id);
            e.Handled = true;
        }
    }

    private DataTemplate BuildItemTemplate()
    {
        var factory = new FrameworkElementFactory(typeof(TextBlock));
        factory.SetBinding(TextBlock.TextProperty, new Binding("Name"));
        factory.SetValue(TextBlock.ForegroundProperty, Brushes.White);
        factory.SetValue(TextBlock.FontSizeProperty, 12.0);
        factory.SetValue(TextBlock.PaddingProperty, new Thickness(2, 1, 2, 1));
        factory.SetValue(TextBlock.CursorProperty, Cursors.Hand);
        factory.SetValue(TextBlock.TagProperty, new Binding("Path"));
        factory.AddHandler(TextBlock.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(OnItemPreviewDown));
        factory.AddHandler(TextBlock.PreviewMouseMoveEvent, new MouseEventHandler(OnItemPreviewMove));
        factory.AddHandler(TextBlock.PreviewMouseUpEvent, new MouseButtonEventHandler(OnItemPreviewUp));
        factory.AddHandler(TextBlock.MouseLeftButtonDownEvent, new MouseButtonEventHandler(OnItemMouseDown));
        return new DataTemplate { VisualTree = factory };
    }

    private void OnItemPreviewDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is TextBlock tb && tb.Tag is string path)
        {
            _dragCandidate = true;
            _dragItemPath = path;
            _dragStartPoint = e.GetPosition(tb);
        }
    }

    private void OnItemPreviewMove(object sender, MouseEventArgs e)
    {
        if (!_dragCandidate || _dragItemPath == null || sender is not TextBlock tb) return;
        if (e.LeftButton != MouseButtonState.Pressed) { _dragCandidate = false; return; }
        var p = e.GetPosition(tb);
        if (Math.Abs(p.X - _dragStartPoint.X) < 4 && Math.Abs(p.Y - _dragStartPoint.Y) < 4) return;
        _dragCandidate = false;
        try
        {
            DragDrop.DoDragDrop(tb, new DataObject("fence/item", _dragItemPath), DragDropEffects.Move);
        }
        catch (Exception ex)
        {
            HostLog.Write("FenceBox 项拖拽失败", ex);
        }
    }

    private void OnItemPreviewUp(object sender, MouseButtonEventArgs e) => _dragCandidate = false;

    private static void OnItemMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return; // only react to double-click (open the real file)
        if (sender is not TextBlock tb || tb.Tag is not string path) return;
        e.Handled = true;
        OpenOriginalFile(path);
    }

    /// <summary>Launch the original file in its default application. Fences does not move anything.</summary>
    private static void OpenOriginalFile(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            HostLog.Write($"FenceBox 双击打开失败：{path}", ex);
        }
    }
}
