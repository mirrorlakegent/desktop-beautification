using System.Drawing;
using System.Windows.Forms;
using DesktopSuite.Desktop;
using DesktopSuite.Wallpaper;

namespace DesktopSuite;

/// <summary>
/// System-tray presence for DesktopSuite. The wallpaper renderer is intentionally detached from the
/// GUI, so the tray (owned by the GUI process) is the always-available control surface: toggle
/// sound, open the volume popup, show/hide the main window, stop the wallpaper, or quit.
/// Closing the main window minimises to the tray instead of exiting, so the icon stays put.
/// </summary>
public sealed class TrayManager : IDisposable
{
    private readonly NotifyIcon _notify;
    private readonly ToolStripMenuItem _soundItem;
    private readonly ToolStripMenuItem _launchItem;
    private readonly ToolStripMenuItem _iconItem;
    private readonly ToolStripMenuItem _undoItem;
    private readonly MainWindow _owner;
    private readonly WallpaperEngine _wallpaper;
    private readonly AppSettings _settings;
    private readonly DesktopSceneManager _scenes;

    public TrayManager(MainWindow owner, WallpaperEngine wallpaper, AppSettings settings,
                       IconHider iconHider, DesktopSceneManager scenes)
    {
        _owner = owner;
        _wallpaper = wallpaper;
        _settings = settings;
        _scenes = scenes;

        _notify = new NotifyIcon
        {
            Icon = MakeTrayIcon(),
            Visible = true,
            Text = "DesktopSuite — 动态壁纸"
        };

        var menu = new ContextMenuStrip();

        _soundItem = new ToolStripMenuItem("🔇 声音：关");
        _soundItem.Click += (_, _) =>
        {
            _settings.AudioEnabled = !_settings.AudioEnabled;
            _settings.Save();
            if (_wallpaper.IsDynamicRunning)
                _wallpaper.SetAudioRuntime(_settings.AudioEnabled, _settings.Volume);
            RefreshSoundLabel();
            _owner.SyncAudioUI();
        };
        menu.Items.Add(_soundItem);

        var volItem = new ToolStripMenuItem("🔈 音量…");
        volItem.Click += (_, _) => ShowVolumeForm();
        menu.Items.Add(volItem);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("显示主窗口", null, (_, _) => _owner.ShowMainWindow()));
        menu.Items.Add(new ToolStripMenuItem("🖼️ 立即轮换壁纸", null, (_, _) => _owner.RotateNow()));

        // M3.31: undo for accidentally deleted fence categories. Disabled until a deletion happens.
        _undoItem = new ToolStripMenuItem("↩ 撤销删除分类")
        {
            Enabled = false
        };
        _undoItem.Click += (_, _) => _owner.UndoFenceCategoryDelete();
        menu.Items.Add(_undoItem);

        _iconItem = new ToolStripMenuItem("🗂️ 隐藏桌面图标：关");
        _iconItem.Click += (_, _) => _owner.ToggleHideIcons();
        menu.Items.Add(_iconItem);

        _launchItem = new ToolStripMenuItem("🚀 开机自启：关");
        _launchItem.Click += (_, _) => _owner.ToggleLaunchOnBoot();
        menu.Items.Add(_launchItem);

        menu.Items.Add(new ToolStripMenuItem("停止壁纸", null, (_, _) => _owner.StopWallpaperFromTray()));

        // Phase 3: scene presets (icon visibility + wallpaper + rotation + sound in one click).
        var sceneMenu = new ToolStripMenuItem("🎬 场景");
        foreach (var s in _scenes.Scenes)
            sceneMenu.DropDownItems.Add(new ToolStripMenuItem(s.Name, null, (_, _) => _owner.ApplyScene(s.Name)));
        menu.Items.Add(sceneMenu);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("退出（保留壁纸）", null, (_, _) => _owner.ExitKeepWallpaper()));
        menu.Items.Add(new ToolStripMenuItem("退出并停止壁纸", null, (_, _) => _owner.ExitStopWallpaper()));

        _notify.ContextMenuStrip = menu;
        // M3.30: double-click the tray icon to toggle (show/hide) the fences overlay; falls back to
        // showing the main window when fences are not enabled.
        _notify.DoubleClick += (_, _) => _owner.ToggleFenceOverlay();

        RefreshSoundLabel();
        RefreshLaunchOnBootLabel();
        RefreshIconLabel();
    }

    public void RefreshSoundLabel()
    {
        bool on = _settings.AudioEnabled;
        _soundItem.Text = on ? "🔊 声音：开" : "🔇 声音：关";
        _soundItem.Checked = on;
    }

    public void RefreshLaunchOnBootLabel()
    {
        bool on = _settings.LaunchOnStartup;
        _launchItem.Text = on ? "🚀 开机自启：开" : "🚀 开机自启：关";
        _launchItem.Checked = on;
    }

    /// <summary>
    /// Boundary case (P2): the shell can be temporarily unreadable — Explorer restarting, an RDP
    /// session switch, a locked workstation. The old code collapsed that Unknown into `false` and
    /// showed "关", i.e. it actively claimed the icons were visible when we simply could not tell.
    /// Report 未知 instead so the tray never contradicts the main window's status line.
    /// </summary>
    public void RefreshIconLabel()
    {
        switch (_owner.IconState)
        {
            case IconVisibility.Hidden:
                _iconItem.Text = "🗂️ 隐藏桌面图标：开";
                _iconItem.Checked = true;
                break;
            case IconVisibility.Visible:
                _iconItem.Text = "🗂️ 隐藏桌面图标：关";
                _iconItem.Checked = false;
                break;
            default:
                _iconItem.Text = "🗂️ 隐藏桌面图标：未知";
                _iconItem.Checked = false;
                break;
        }
    }

    /// <summary>M3.31: reflect the fence-delete undo availability on the tray menu. The item is
    /// greyed out until a category is deleted, then shows the name of the most recent one.</summary>
    public void RefreshUndoItem()
    {
        if (_undoItem == null) return;
        bool can = _owner.CanUndoFenceCategoryDelete;
        _undoItem.Enabled = can;
        _undoItem.Text = can ? $"↩ 撤销删除分类「{_owner.PendingUndoCategoryName}」" : "↩ 撤销删除分类";
    }

    private void ShowVolumeForm()
    {
        using var form = new VolumeForm(
            muted: !_settings.AudioEnabled,
            volume: _settings.Volume,
            onChange: (muted, vol) =>
            {
                _settings.AudioEnabled = !muted;
                _settings.Volume = vol;
                _settings.Save();
                if (_wallpaper.IsDynamicRunning)
                    _wallpaper.SetAudioRuntime(_settings.AudioEnabled, _settings.Volume);
                RefreshSoundLabel();
                _owner.SyncAudioUI();
            });
        form.ShowDialog();
    }

    private static Icon MakeTrayIcon()
    {
        const int size = 32;
        using var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        using var brush = new SolidBrush(Color.FromArgb(255, 190, 40, 170));
        g.FillEllipse(brush, 3, 3, size - 6, size - 6);
        using var white = new SolidBrush(Color.White);
        var tri = new PointF[]
        {
            new(size * 0.38f, size * 0.30f),
            new(size * 0.38f, size * 0.70f),
            new(size * 0.70f, size * 0.50f),
        };
        g.FillPolygon(white, tri);
        return Icon.FromHandle(bmp.GetHicon());
    }

    public void Dispose()
    {
        _notify.Visible = false;
        _notify.Dispose();
    }

    /// <summary>
    /// Tiny modeless-feel popup with a mute checkbox and a volume slider. Positioned at the cursor;
    /// closes when it loses focus. Changes are pushed back through the supplied callback.
    /// </summary>
    private sealed class VolumeForm : Form
    {
        private readonly TrackBar _track;
        private readonly Label _label;
        private readonly CheckBox _mute;
        private readonly Action<bool, int> _onChange;
        private bool _ready;

        public VolumeForm(bool muted, int volume, Action<bool, int> onChange)
        {
            _onChange = onChange;

            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Width = 240;
            Height = 116;

            var pt = Cursor.Position;
            Location = new Point(
                Math.Max(0, pt.X - Width / 2),
                Math.Max(0, pt.Y - Height));

            _mute = new CheckBox { Text = "静音", Left = 12, Top = 12, Width = 200, Checked = muted };
            _track = new TrackBar { Left = 12, Top = 40, Width = 216, Minimum = 0, Maximum = 100, Value = volume, TickStyle = TickStyle.None };
            _label = new Label { Left = 12, Top = 84, Width = 216, Text = $"{volume}%" };

            _mute.CheckedChanged += (_, _) => Fire();
            _track.ValueChanged += (_, _) => { _label.Text = $"{_track.Value}%"; Fire(); };

            Controls.Add(_mute);
            Controls.Add(_track);
            Controls.Add(_label);

            Deactivate += (_, _) => Close();
            _ready = true;
        }

        private void Fire()
        {
            if (_ready) _onChange(_mute.Checked, _track.Value);
        }
    }
}
