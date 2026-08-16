using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using DesktopSuite.Desktop;
using DesktopSuite.Desktop.Organizer;
using DesktopSuite.Safety;
using DesktopSuite.Themes;
using DesktopSuite.Wallpaper;
using Microsoft.Win32;
using System.Windows.Threading;

namespace DesktopSuite;

public partial class MainWindow : Window
{
    private readonly WallpaperEngine _wallpaper = new();
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly IconHider _iconHider = new();
    private readonly DesktopSceneManager _scenes = new();
    private readonly FenceStore _fenceStore = FenceStore.Current;
    private FenceLayer? _fenceLayer;
    private TrayManager? _tray;
    private WallpaperRotator? _rotator;
    private bool _uiReady;
    private bool _forceClose;
    private bool _suppressIconEvents;
    private bool _suppressRotationEvents;
    private bool _suppressAudioEvents;
    private bool _shuttingDown;
    private ResolvedTheme? _lastTheme;

    /// <summary>0/1 latch so OnClosed and SessionEnding cannot both run the exit icon restore (P1-2).</summary>
    private int _iconsRestored;

    /// <summary>0/1 latch serialising the long-running desktop operations (icon apply / scene apply)
    /// that now run on the thread pool, so overlapping clicks cannot interleave two shell toggles (P1-6).</summary>
    private int _desktopBusy;

    // ---- M3.30: double-click desktop to toggle fences + idle auto-hide ----
    private DesktopDoubleClickHook? _desktopHook;
    private DispatcherTimer? _idleTimer;
    private const int FenceIdleHideSeconds = 30;

    public MainWindow()
    {
        InitializeComponent();
        ThemeService.Current.ThemeChanged += OnThemeChanged;

        // Restore last session's preferences and re-adopt a wallpaper that is still running
        // after the app was closed (the renderer is intentionally detached from this process).
        ChkAudio.IsChecked = _settings.AudioEnabled;
        VolSlider.Value = _settings.Volume;
        VolLabel.Text = $"{(int)VolSlider.Value}%";
        _wallpaper.Adopt(_settings.RendererPid);
        RefreshWallpaperStateUI();

        // Time-based wallpaper library rotator. If rotation was enabled last session, it starts
        // immediately (off the UI thread) and keeps the wallpaper in sync with the current period.
        _rotator = new WallpaperRotator(_wallpaper, _settings);
        _rotator.StatusChanged += OnRotatorStatus;
        ChkRotation.IsChecked = _settings.RotationEnabled;
        ChkLaunchOnBoot.IsChecked = _settings.LaunchOnStartup;
        IntervalSlider.Value = Math.Clamp(_settings.RotationIntervalMinutes, 5, 120);
        IntervalLabel.Text = $"{(int)IntervalSlider.Value} 分钟";
        // Rotation is started by ChkRotation_Changed (fired when we set IsChecked above) — do NOT also
        // call Start() here, or the first tick runs twice and causes a visible double-flash.

        // ---- Desktop organization (Phase 3): icon hiding + scenes ----
        // Reconcile our *intent* to reality first so the checkbox shows the true state on launch,
        // then populate the scene picker. The StateChanged callback keeps UI + tray in sync.
        _iconHider.StateChanged += OnIconStateChanged;
        // NOTE: we deliberately do NOT call ReconcileFromReality here. DesiredIconsHidden is the
        // single source of truth for the user's preference. On a prior exit we may have restored the
        // icons to visible (RestoreIconsOnExit) without changing the intent, so reconciling
        // reality→intent at startup would clobber that intent. We trust the intent and re-apply it below.
        ChkHideIcons.IsChecked = _settings.DesiredIconsHidden;
        ChkRestoreIconsOnExit.IsChecked = _settings.RestoreIconsOnExit;
        foreach (var s in _scenes.Scenes)
            CmbScene.Items.Add(s.Name);
        if (!string.IsNullOrEmpty(_settings.ActiveSceneName))
            CmbScene.SelectedItem = _settings.ActiveSceneName;
        UpdateDesktopStatus();
        // V2A/V11A defense-in-depth: if the previous session was killed unexpectedly (hard crash,
        // power loss, hard VM reset) and never got to restore the icons, the user would come back
        // to an empty desktop. Detect this: intent says hidden + RestoreIconsOnExit says restore,
        // but icons are ALREADY hidden (meaning nobody restored them). Log it and do a courtesy
        // restore before re-applying the hidden state, so the user at least sees their desktop flash.
        if (_settings.DesiredIconsHidden && _settings.RestoreIconsOnExit)
        {
            bool? currentlyVisible = DesktopShell.AreIconsVisible();
            if (currentlyVisible == false)
            {
                HostLog.Write("启动检测：上次会话未恢复桌面图标（疑似异常退出），执行补偿性恢复。");
                try { _iconHider.ApplyDetailed(false); } catch { }
            }
        }
        // If the user wants icons hidden (prior session, or a --background login), make sure it is
        // actually applied — with backoff for the case where Explorer is not ready at login yet.
        if (_settings.DesiredIconsHidden)
            ApplyIconsWithRetry(true);

        // ---- Desktop organization (Phase 1+2): Fences ----
        // If fences were active last session, re-show them (and keep the native icons hidden) on launch.
        if (_fenceStore.Load().FencesEnabled)
            ApplyFencesWithRetryIfEnabled();

        // The tray icon is the always-available control surface; it persists after the window is
        // closed (we minimise-to-tray, so the GUI process stays alive to own the icon).
        _tray = new TrayManager(this, _wallpaper, _settings, _iconHider, _scenes);
        _uiReady = true;

        // M3.30: double-click the empty desktop to toggle the fences overlay, and auto-hide after
        // a period of no interaction. The hook installs on this (Dispatcher) thread, which pumps
        // messages, so its callback runs on the UI thread and is safe to touch Win32 state.
        try
        {
            _desktopHook = new DesktopDoubleClickHook(OnDesktopDoubleClick);
            _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _idleTimer.Tick += OnIdleTick;
            _idleTimer.Start();
        }
        catch (Exception ex)
        {
            HostLog.Write("M3.30 初始化失败（钩子/定时器）", ex);
        }

        // V11A fix: hook WM_QUERYENDSESSION at the HWND level as a backstop beyond WPF's
        // Application.SessionEnding. In --background mode the main window's HWND exists but
        // WPF may not route the message to Application.SessionEnding if the window was never
        // shown via Show(). This hook catches it at the source.
        SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            _hwndSource = HwndSource.FromHwnd(hwnd);
            _hwndSource?.AddHook(WndProc);
        }
        catch (Exception ex)
        {
            HostLog.Write("Failed to install WM_QUERYENDSESSION hook", ex);
        }
    }

    private HwndSource? _hwndSource;

    private const uint WM_QUERYENDSESSION = 0x0011;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_QUERYENDSESSION)
        {
            // V11A fix: Windows is asking "can I shut down?" — restore icons BEFORE answering.
            // We return TRUE (allow shutdown) by leaving handled=false (DefWindowProc returns TRUE).
            // The restore runs synchronously; RestoreIconsOnTeardown is latched and fast (~200ms).
            try
            {
                // ENDSESSION_LOGOFF (0x80000000) in lParam distinguishes logoff from shutdown so the
                // recovery log carries the correct reason (otherwise V12 would mislabel a logoff as 关机).
                bool isLogoff = (lParam.ToInt64() & 0x80000000L) != 0;
                string reason = isLogoff ? "系统注销（WM_QUERYENDSESSION）" : "系统关机（WM_QUERYENDSESSION）";
                HostLog.Write($"WM_QUERYENDSESSION received — restoring desktop icons before {(isLogoff ? "logoff" : "shutdown")}.");
                RestoreIconsOnTeardown(reason);
            }
            catch (Exception ex)
            {
                HostLog.Write("WM_QUERYENDSESSION restore failed", ex);
            }
            // Do NOT set handled=true and do NOT return FALSE — we deliberately allow shutdown.
        }
        return IntPtr.Zero;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Closing the window minimises to the tray instead of exiting, so the wallpaper keeps
        // playing and the sound control stays reachable. Only a real exit (tray menu) bypasses this.
        if (!_forceClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        _settings.Save();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        // Persist any in-memory preference changes, then dispose. Crucially, do NOT stop the
        // renderer: the wallpaper is designed to keep playing after the GUI exits.
        _shuttingDown = true;
        // V11A fix: remove the WndProc hook before the window is destroyed.
        try { _hwndSource?.RemoveHook(WndProc); } catch { }
        // Fences and native icons are mutually exclusive: if fences was active, the native icons are
        // hidden, so we MUST restore them on exit regardless of the hide-icons feature's own
        // RestoreIconsOnExit preference (otherwise fences-active + restore-OFF would leave a blank
        // desktop). This runs before closing the layer; RestoreIconsOnTeardown below still handles
        // the standalone hide-icons feature and is a no-op for icons once we've restored here.
        bool wasFences = _fenceLayer != null;
        if (wasFences)
        {
            try { _iconHider.ApplyDetailed(false); } catch { }
        }
        // Close the fences layer (it owns a desktop child window / region) before we tear down.
        try { _fenceLayer?.Close(); _fenceLayer = null; } catch { }
        RestoreIconsOnTeardown("窗口关闭");
        _rotator?.Dispose();
        _tray?.Dispose();
        _desktopHook?.Dispose();
        _idleTimer?.Stop();
        _settings.Save();
        _wallpaper.Dispose();
        base.OnClosed(e);
    }

    /// <summary>
    /// Restore the desktop icons on an app-teardown path, honouring RestoreIconsOnExit.
    ///
    /// P1-2: this is shared by BOTH teardown routes. Window.OnClosed covers a normal quit, but a
    /// Windows shutdown/logoff tears the process down through Application.SessionEnding WITHOUT ever
    /// raising OnClosed — which used to leave the user with a headless desktop after the reboot.
    /// The Interlocked latch makes it safe for both to fire for the same teardown.
    ///
    /// Restoring is a TEMPORARY action for THIS exit only: it must NOT overwrite the user's persisted
    /// intent (DesiredIconsHidden). We snapshot the intent, suppress icon-event re-entrancy so the
    /// programmatic checkbox reset (Apply → StateChanged → OnIconStateChanged) cannot clobber it,
    /// and write the intent back afterwards.
    /// </summary>
    public void RestoreIconsOnTeardown(string reason)
    {
        if (System.Threading.Interlocked.Exchange(ref _iconsRestored, 1) != 0) return;

        if (!_settings.RestoreIconsOnExit)
        {
            HostLog.Write($"退出（{reason}）：用户已关闭「退出时恢复桌面图标」，保持当前状态。");
            return;
        }

        bool intent = _settings.DesiredIconsHidden;
        _suppressIconEvents = true;
        try
        {
            var result = _iconHider.ApplyDetailed(false);
            HostLog.Write($"退出（{reason}）：恢复桌面图标 → {result.Outcome}（{result.Strategy}）。");
        }
        catch (Exception ex)
        {
            HostLog.Write($"退出（{reason}）：恢复桌面图标异常", ex);
        }
        finally
        {
            _settings.DesiredIconsHidden = intent;   // intent survives the courtesy restore
            _settings.Save();
            _suppressIconEvents = false;
        }
    }

    // ---- Tray-driven actions (called by TrayManager) ----

    /// <summary>M3.30: tray double-click toggles the fences overlay (hide when shown, show when
    /// hidden). If fences were never enabled, fall back to showing the main window.</summary>
    public void ToggleFenceOverlay()
    {
        if (_fenceLayer != null) { _fenceLayer.ToggleHidden(); return; }
        ShowMainWindow();
    }

    /// <summary>Tray "撤销删除分类" — restores the most recently deleted fence box + its icons.</summary>
    public void UndoFenceCategoryDelete()
    {
        if (_fenceLayer == null) return;
        _fenceLayer.UndoLastCategoryDelete();
        _tray?.RefreshUndoItem();
    }

    /// <summary>Whether a deleted category can still be undone (drives the tray menu item state).</summary>
    public bool CanUndoFenceCategoryDelete => _fenceLayer?.CanUndoCategoryDelete ?? false;

    /// <summary>Name of the most recently deleted category, for the tray menu label.</summary>
    public string? PendingUndoCategoryName => _fenceLayer?.PendingUndoCategoryName;

    /// <summary>Low-level hook callback: a double-click was detected somewhere on screen. Toggle the
    /// overlay only when it landed on the bare desktop (not on a fence box, not on another window).</summary>
    private void OnDesktopDoubleClick(int x, int y)
    {
        if (_fenceLayer == null) return;
        IntPtr hw = FenceNative.WindowFromPoint(new FenceNative.POINT { X = x, Y = y });
        if (hw == _fenceLayer.Hwnd) return;       // double-click on a fence box → let FenceLayer handle it
        if (!IsDesktopWindow(hw)) return;          // taskbar / other app window → ignore
        _fenceLayer.ToggleHidden();
        HostLog.Write($"桌面双击 → 围栏 ToggleHidden（hidden={_fenceLayer.Hidden}）");
    }

    /// <summary>True if <paramref name="hwnd"/> belongs to the desktop background window tree
    /// (Progman / WorkerW / SHELLDLL_DefView / SysListView32). When fences are active the native
    /// desktop icons are hidden, so any hit on this tree is the empty desktop background.</summary>
    private static bool IsDesktopWindow(IntPtr hwnd)
    {
        IntPtr h = hwnd;
        for (int i = 0; i < 24 && h != IntPtr.Zero; i++)
        {
            var sb = new StringBuilder(64);
            if (FenceNative.GetClassName(h, sb, sb.Capacity) > 0)
            {
                string cls = sb.ToString();
                if (cls is "Progman" or "WorkerW" or "SHELLDLL_DefView" or "SysListView32")
                    return true;
            }
            h = FenceNative.GetParent(h);
        }
        return false;
    }

    /// <summary>Idle auto-hide: if the overlay has been shown and untouched for
    /// <see cref="FenceIdleHideSeconds"/>, collapse it away.</summary>
    private void OnIdleTick(object? sender, EventArgs e)
    {
        if (_fenceLayer != null && !_fenceLayer.Hidden &&
            (DateTime.UtcNow - _fenceLayer.LastActivityUtc).TotalSeconds > FenceIdleHideSeconds)
        {
            _fenceLayer.HideFences();
            HostLog.Write("空闲超时 → 围栏自动隐藏");
        }
    }

    public void ShowMainWindow()
    {
        ShowInTaskbar = true;   // restore taskbar presence lost when launched with --background
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    public void StopWallpaperFromTray()
    {
        _wallpaper.StopDynamic();
        _settings.RendererPid = 0;
        _settings.Save();
        RefreshWallpaperStateUI();
    }

    public void ExitKeepWallpaper()
    {
        _forceClose = true;
        _settings.Save();
        Application.Current.Shutdown();
    }

    public void ExitStopWallpaper()
    {
        _forceClose = true;
        _wallpaper.StopDynamic();
        _settings.RendererPid = 0;
        _settings.Save();
        Application.Current.Shutdown();
    }

    /// <summary>Trigger an immediate rotation (called from the tray menu).</summary>
    public void RotateNow() => _rotator?.RotateNow();

    /// <summary>Push the current settings into the window controls (e.g. after a tray change).</summary>
    public void SyncAudioUI()
    {
        ChkAudio.IsChecked = _settings.AudioEnabled;
        VolSlider.Value = _settings.Volume;
        VolLabel.Text = $"{(int)VolSlider.Value}%";
    }

    private void BtnBackup_Click(object sender, RoutedEventArgs e)
    {
        var dir = new BackupManager().CreateBaseline();
        Status.Text = $"基线备份已保存：\n{dir}";
    }

    private void BtnRestore_Click(object sender, RoutedEventArgs e)
    {
        var latest = RestoreManager.FindLatestBackup();
        if (latest is null)
        {
            Status.Text = "未找到基线备份。";
            return;
        }
        new RestoreManager().RestoreFrom(latest);
        Status.Text = $"已从以下位置恢复：\n{latest}\n（资源管理器已重启。）";
    }

    private void BtnLoadTheme_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var theme = ThemeService.Current.LoadDefault();
            ThemeInfo.Text = $"已加载：{theme.Name}\n模式：{theme.ColorMode}\nID：{theme.Id}";
            Status.Text = "主题已加载。背景/前景色已更新。";
        }
        catch (Exception ex)
        {
            Status.Text = $"主题加载失败：\n{ex.Message}";
        }
    }

    private void BtnSetStatic_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择静态壁纸图片",
            Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp;*.webp|All files|*.*"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            _wallpaper.SetStatic(dlg.FileName);
            Status.Text = $"静态壁纸已设置：\n{dlg.FileName}";
            WallpaperInfo.Text = $"类型：静态\n文件：{Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex)
        {
            Status.Text = $"静态壁纸设置失败：\n{ex.Message}";
        }
    }

    private void BtnStartDynamic_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择动态壁纸媒体（视频）",
            Filter = "Videos|*.mp4;*.mkv;*.mov;*.webm;*.avi|All files|*.*"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            bool audioOn = ChkAudio.IsChecked == true;
            int vol = (int)VolSlider.Value;
            _wallpaper.StopByPid(_settings.RendererPid);
            _wallpaper.StartDynamic(dlg.FileName, audioOn, vol);
            string warn = _wallpaper.IsDegraded ? "\n⚠️ 降级模式（图标被覆盖）—— 请反馈给开发者！" : "";
            string audio = audioOn ? $"\n声音：开启（音量 {vol}%）" : "\n声音：关闭（静音）";
            Status.Text = $"动态壁纸已启动。\n渲染进程 PID：{_wallpaper.RendererPid}\n渲染策略：{_wallpaper.RenderStrategy}{warn}{audio}\n日志：{HostLog.LogPath}";
            WallpaperInfo.Text = $"类型：动态（mpv）\n文件：{Path.GetFileName(dlg.FileName)}";

            // Persist so the wallpaper (and its sound settings) survive a future app restart.
            _settings.AudioEnabled = audioOn;
            _settings.Volume = vol;
            _settings.LastMedia = dlg.FileName;
            _settings.RendererPid = _wallpaper.RendererPid;
            _settings.Save();
        }
        catch (Exception ex)
        {
            Status.Text = $"动态壁纸启动失败：\n{ex.Message}";
            WallpaperInfo.Text = "类型：无";
        }
    }

    private void BtnStartTest_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _wallpaper.StartTestPattern();
            string warn = _wallpaper.IsDegraded ? "\n⚠️ 降级模式（图标被覆盖）。" : "";
            Status.Text =
                $"测试图案（品红）已启动。\n渲染进程 PID：{_wallpaper.RendererPid}\n" +
                $"渲染策略：{_wallpaper.RenderStrategy}{warn}\n日志：{HostLog.LogPath}\n" +
                "如果在图标后方看到品红色，说明渲染目标正确，问题仅在 mpv。";
            WallpaperInfo.Text = "类型：测试图案（品红）";
        }
        catch (Exception ex)
        {
            Status.Text = $"测试图案启动失败：\n{ex.Message}";
            WallpaperInfo.Text = "类型：无";
        }
    }

    private void BtnStopDynamic_Click(object sender, RoutedEventArgs e)
    {
        _wallpaper.StopDynamic();
        _settings.RendererPid = 0;
        _settings.Save();
        Status.Text = "动态壁纸已停止。";
        WallpaperInfo.Text = "类型：无";
    }

    private void VolSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressAudioEvents) return;   // scene apply already pushed volume through the engine
        if (VolLabel != null)
            VolLabel.Text = $"{(int)e.NewValue}%";
        _settings.Volume = (int)e.NewValue; // remember live; persisted on start/stop/close
        if (_uiReady && _wallpaper.IsDynamicRunning)
        {
            MpvIpc.SetVolume(_settings.Volume);
            _tray?.RefreshSoundLabel();
        }
    }

    // ---- Wallpaper Library (time-based rotation) ----

    private void ChkRotation_Changed(object sender, RoutedEventArgs e)
    {
        // Suppressed only while a scene apply pushes its own value in (it already called SetEnabled).
        if (_suppressRotationEvents) return;
        bool on = ChkRotation.IsChecked == true;
        _rotator?.SetEnabled(on);
        LibStatus.Text = on ? "自动轮换已开启。" : "自动轮换已关闭。";
    }

    private void ChkLaunchOnBoot_Changed(object sender, RoutedEventArgs e)
    {
        bool on = ChkLaunchOnBoot.IsChecked == true;
        _settings.LaunchOnStartup = on;
        _settings.Save();
        StartupManager.SetEnabled(on);
        _tray?.RefreshLaunchOnBootLabel();
    }

    /// <summary>Toggle login auto-start from the tray menu (keeps the window checkbox in sync).</summary>
    public void ToggleLaunchOnBoot()
    {
        bool on = !_settings.LaunchOnStartup;
        _settings.LaunchOnStartup = on;
        _settings.Save();
        StartupManager.SetEnabled(on);
        ChkLaunchOnBoot.IsChecked = on;
        _tray?.RefreshLaunchOnBootLabel();
    }

    // ---- Desktop organization (Phase 3): icon hiding + scenes ----

    public bool IconsHidden => _iconHider.Current == IconVisibility.Hidden;
    public IconVisibility IconState => _iconHider.Current;

    private void ChkHideIcons_Changed(object sender, RoutedEventArgs e)
    {
        if (!_uiReady || _suppressIconEvents) return;
        RunIconApply(ChkHideIcons.IsChecked == true);
    }

    /// <summary>
    /// P1-6: an icon toggle round-trips through Explorer — up to three ladder steps, each a
    /// SendMessageTimeout with a 1s budget plus a 150ms settle. On the UI thread that is a visible
    /// freeze, so the shell work runs on the pool and only the (cheap) UI sync comes back via the
    /// Dispatcher.
    /// P1-7: the intent is persisted ONLY when the shell was readable.
    /// P1-8: the outcome is always surfaced to the user instead of being dropped.
    /// </summary>
    private void RunIconApply(bool hidden)
    {
        if (System.Threading.Interlocked.CompareExchange(ref _desktopBusy, 1, 0) != 0)
        {
            Status.Text = "桌面操作正在进行中，请稍候…";
            // Put the checkbox back where reality says it is, so it does not lie about a rejected click.
            SyncIconUI();
            return;
        }

        DesktopStatus.Text = hidden ? "正在隐藏桌面图标…" : "正在显示桌面图标…";

        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            IconApplyResult result;
            try
            {
                result = _iconHider.ApplyDetailed(hidden);
            }
            catch (Exception ex)
            {
                HostLog.Write("图标切换异常", ex);
                result = new IconApplyResult(IconApplyOutcome.Unknown, IconVisibility.Unknown, "异常");
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _desktopBusy, 0);
            }

            // P1-7: an Unknown result means we never read reality — writing intent here would
            // silently overwrite the user's preference with a value we never verified.
            if (result.IsDeterministic)
            {
                _settings.DesiredIconsHidden = hidden;
                _settings.Save();
            }

            if (_shuttingDown) return;
            Dispatcher.BeginInvoke(() =>
            {
                if (_shuttingDown) return;
                SyncIconUI();
                Status.Text = result.Success
                    ? $"桌面图标：{(hidden ? "已隐藏" : "已显示")}（{result.Strategy}）"
                    : $"桌面图标切换未生效 —— {result.Describe()}";
                if (!result.IsDeterministic)
                    Status.Text += "\n偏好未写入（避免用未知状态覆盖你的选择）；可稍后重试或运行诊断。";
            });
        });
    }

    private void ChkRestoreIcons_Changed(object sender, RoutedEventArgs e)
    {
        if (!_uiReady) return;
        _settings.RestoreIconsOnExit = ChkRestoreIconsOnExit.IsChecked == true;
        _settings.Save();
    }

    private void BtnApplyScene_Click(object sender, RoutedEventArgs e)
    {
        if (CmbScene.SelectedItem is string name)
            ApplyScene(name);
    }

    /// <summary>
    /// Apply a named scene (called from the main window and the tray submenu).
    /// P1-6: a scene switch restarts the mpv renderer and toggles the shell icons, which is far too
    /// slow for the UI thread — the whole apply runs on the pool and only the control sync marshals back.
    /// P1-8: the scene result carries its own message (including partial-success notes such as a
    /// missing fixed wallpaper), so the user always learns what really happened.
    /// </summary>
    public void ApplyScene(string name)
    {
        if (System.Threading.Interlocked.CompareExchange(ref _desktopBusy, 1, 0) != 0)
        {
            Status.Text = "桌面操作正在进行中，请稍候…";
            return;
        }

        Status.Text = $"正在应用场景：{name}…";

        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            SceneApplyResult result;
            try
            {
                result = _scenes.ApplySceneDetailed(name, _wallpaper, _rotator!, _iconHider, _settings);
            }
            catch (Exception ex)
            {
                HostLog.Write($"应用场景「{name}」异常", ex);
                result = new SceneApplyResult(false, $"应用场景失败：{name} —— {ex.Message}");
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _desktopBusy, 0);
            }

            if (_shuttingDown) return;
            Dispatcher.BeginInvoke(() =>
            {
                if (_shuttingDown) return;
                if (result.Success)
                {
                    SyncIconUI();
                    // Push scene values into the controls WITHOUT re-firing their handlers: the scene
                    // already applied rotation/audio, so a bounce-back would re-run SetEnabled (an extra
                    // rotation tick → visible double-flash, P1-3) and a redundant mpv IPC write.
                    SetAudioUISilently(_settings.AudioEnabled, _settings.Volume);
                    SetRotationCheckboxSilently(_settings.RotationEnabled);
                    CmbScene.SelectedItem = name;
                }
                Status.Text = result.Message;
            });
        });
    }

    /// <summary>Toggle icon visibility from the tray menu.</summary>
    public void ToggleHideIcons()
    {
        var current = _iconHider.Current;
        if (current == IconVisibility.Unknown)
        {
            // P1-8: do not guess. Toggling from an unknown state would pick a direction at random.
            Status.Text = "无法读取桌面图标状态（资源管理器未就绪），已取消切换。";
            UpdateDesktopStatus();
            return;
        }
        RunIconApply(current != IconVisibility.Hidden);
    }

    // ---- Desktop organization (Phase 1+2): Fences (icon virtualization) ----

    /// <summary>Button handler for the desktop-tidy toggle.</summary>
    private void BtnToggleFences_Click(object sender, RoutedEventArgs e) => ToggleFences();

    /// <summary>
    /// Enable / disable the Fences layer. Fences and native icons are mutually exclusive: enabling
    /// hides the native desktop icons; disabling restores them. The shell work (icon hide/show) runs
    /// on the thread pool (P1-6); only the cheap UI sync + window creation marshals back to the
    /// Dispatcher. Every shell call is wrapped so a missing shell never throws into the UI thread.
    /// </summary>
    private void ToggleFences()
    {
        if (System.Threading.Interlocked.CompareExchange(ref _desktopBusy, 1, 0) != 0)
        {
            Status.Text = "桌面操作正在进行中，请稍候…";
            HostLog.Write("ToggleFences：跳过（桌面操作忙，_desktopBusy=1）。");
            return;
        }

        bool enable = _fenceLayer == null;
        HostLog.Write($"ToggleFences：入口 enable={enable} _fenceLayer={( _fenceLayer == null ? "null" : "set")}");
        Status.Text = enable ? "正在启用桌面整理…" : "正在停用桌面整理…";

        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            IconApplyResult? iconResult = null;
            try
            {
                // Hide native icons when enabling, restore when disabling.
                iconResult = _iconHider.ApplyDetailed(enable);
            }
            catch (Exception ex)
            {
                HostLog.Write("Fences 图标切换异常", ex);
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _desktopBusy, 0);
            }

            if (_shuttingDown)
            {
                HostLog.Write("ToggleFences：UI 回调未调度（_shuttingDown）。");
                return;
            }
            Dispatcher.BeginInvoke(() =>
            {
                if (_shuttingDown)
                {
                    HostLog.Write("ToggleFences：UI 回调被跳过（_shuttingDown）。");
                    return;
                }
                try
                {
                    if (enable) EnableFences();
                    else DisableFences();

                    Status.Text = $"桌面整理已{(enable ? "启用" : "停用")}" +
                        (iconResult is { } r && !r.Success ? $"（图标状态：{r.Describe()}）" : "");
                }
                catch (Exception ex)
                {
                    HostLog.Write("ToggleFences UI 阶段异常", ex);
                    Status.Text = $"桌面整理切换失败：{ex.Message}";
                }
            });
        });
    }

    /// <summary>Show the fences layer on the UI thread (enumerates + classifies the desktop).</summary>
    private void EnableFences()
    {
        var layout = _fenceStore.Load();
        var items = DesktopItemEnumerator.Enumerate();

        // M3.28: Only auto-classify on FIRST run (no saved member paths).
        // Once the user has manually organized items (import/drag), those MemberPaths
        // are the source of truth. Re-classifying would overwrite user's layout.
        bool hasExistingMembers = layout.Categories.Any(c => c.MemberPaths.Count > 0);
        if (!hasExistingMembers)
        {
            FenceClassifier.Apply(items, layout.Categories, layout.Overrides);
            HostLog.Write("EnableFences：首次运行，自动分类完成。");
        }
        else
        {
            HostLog.Write($"EnableFences：跳过自动分类（已保存 {layout.Categories.Sum(c => c.MemberPaths.Count)} 个成员路径），保留用户手动整理。");
        }

        HostLog.Write($"EnableFences：入口 items={items.Count} categories={layout.Categories.Count} FencesEnabled(旧)={layout.FencesEnabled}");
        _fenceLayer = new FenceLayer();
        _fenceLayer.Show(items.ToArray(), layout);
        _fenceLayer.UndoStateChanged += () => _tray?.RefreshUndoItem();
        HostLog.Write("EnableFences：_fenceLayer.Show 已调用（窗口创建结果见 FenceLayer 日志）。");
        layout.FencesEnabled = true;
        _fenceStore.Save(layout);
        if (BtnToggleFences != null) BtnToggleFences.Content = "停用桌面整理";
        HostLog.Write("EnableFences：完成，桌面整理已启用。");
    }

    /// <summary>Close the fences layer on the UI thread and persist the disabled state.</summary>
    private void DisableFences()
    {
        HostLog.Write($"DisableFences：入口 _fenceLayer={( _fenceLayer == null ? "null" : "set")}");
        _fenceLayer?.Close();
        _fenceLayer = null;
        var layout = _fenceStore.Load();
        layout.FencesEnabled = false;
        _fenceStore.Save(layout);
        if (BtnToggleFences != null) BtnToggleFences.Content = "启用桌面整理";
    }

    /// <summary>Re-enable fences on startup (off the UI thread, with backoff) when the last session
    /// left them active. Mirrors <see cref="ApplyIconsWithRetry"/>; if the native icons cannot be
    /// hidden we back off and leave fences disabled rather than covering an un-hidden desktop.</summary>
    private void ApplyFencesWithRetryIfEnabled()
    {
        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            int[] backoff = { 500, 1000, 2000, 4000, 8000 };
            for (int attempt = 0; attempt <= backoff.Length; attempt++)
            {
                IconApplyResult result;
                try
                {
                    result = _iconHider.ApplyDetailed(true);
                }
                catch (Exception ex)
                {
                    HostLog.Write($"启动应用 Fences 图标隐藏（第 {attempt + 1} 次）异常", ex);
                    result = new IconApplyResult(IconApplyOutcome.Unknown, IconVisibility.Unknown, "异常");
                }

                if (result.Success)
                {
                    HostLog.Write("启动应用 Fences：图标已隐藏，调度 EnableFences（UI 线程）。");
                    if (!_shuttingDown) Dispatcher.BeginInvoke(() => { if (!_shuttingDown) EnableFences(); });
                    return;
                }

                if (attempt == backoff.Length)
                {
                    HostLog.Write($"启动应用 Fences 失败：重试 {attempt + 1} 次后仍未生效 —— {result.Describe()}。");
                    if (!_shuttingDown) Dispatcher.BeginInvoke(() =>
                    {
                        if (_shuttingDown) return;
                        DisableFences();
                        Status.Text = $"启动时未能启用桌面整理（图标无法隐藏）—— {result.Describe()}。";
                    });
                    return;
                }
                System.Threading.Thread.Sleep(backoff[attempt]);
            }
        });
    }

    /// <summary>Push icon state into the window controls + tray label.</summary>
    public void SyncIconUI()
    {
        SetIconCheckboxSilently(_iconHider.Current == IconVisibility.Hidden);
        _tray?.RefreshIconLabel();
        UpdateDesktopStatus();
    }

    /// <summary>
    /// Set the icon checkbox WITHOUT re-entering ChkHideIcons_Changed. Any programmatic sync (tray
    /// toggle, scene apply, shell state event) would otherwise bounce straight back into a second
    /// Apply — a double-toggle that can visibly flip the icons twice.
    /// </summary>
    private void SetIconCheckboxSilently(bool hidden)
    {
        bool prev = _suppressIconEvents;
        _suppressIconEvents = true;
        try { ChkHideIcons.IsChecked = hidden; }
        finally { _suppressIconEvents = prev; }
    }

    /// <summary>Set the rotation checkbox without re-triggering ChkRotation_Changed (avoids a second
    /// SetEnabled → extra immediate tick → double-flash).</summary>
    private void SetRotationCheckboxSilently(bool on)
    {
        bool prev = _suppressRotationEvents;
        _suppressRotationEvents = true;
        try { ChkRotation.IsChecked = on; }
        finally { _suppressRotationEvents = prev; }
    }

    /// <summary>Set the audio controls without re-triggering their handlers (avoids a redundant mpv
    /// IPC round-trip on the UI thread).</summary>
    private void SetAudioUISilently(bool audioEnabled, int volume)
    {
        bool prev = _suppressAudioEvents;
        _suppressAudioEvents = true;
        try
        {
            ChkAudio.IsChecked = audioEnabled;
            VolSlider.Value = volume;
            VolLabel.Text = $"{(int)VolSlider.Value}%";
        }
        finally { _suppressAudioEvents = prev; }
    }

    private void OnIconStateChanged(IconVisibility vis)
    {
        // Raised from a thread-pool worker (apply / retry loop). BeginInvoke, never Invoke: a blocking
        // Invoke from a worker onto a UI thread that is itself tearing down would deadlock.
        if (_shuttingDown) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_shuttingDown) return;
            SetIconCheckboxSilently(vis == IconVisibility.Hidden);
            _tray?.RefreshIconLabel();
            UpdateDesktopStatus();
        });
    }

    private void UpdateDesktopStatus()
    {
        bool want = _settings.DesiredIconsHidden;
        bool? actual = DesktopShell.AreIconsVisible();
        string actualStr = actual == null ? "未知（无法读取资源管理器）" : (actual == true ? "显示" : "隐藏");
        string wantStr = want ? "隐藏" : "显示";
        var text = new StringBuilder($"期望：{wantStr} ｜ 实际：{actualStr}");

        // P1-8 / P2: keep the last non-successful apply visible instead of letting it vanish.
        if (_iconHider.LastResult is { } last && !last.Success)
            text.Append($"\n上次操作：{last.Describe()}");

        DesktopStatus.Text = text.ToString();
        if (_lastTheme != null)
            DesktopStatus.Foreground = (actual != null && actual == !want)
                ? _lastTheme.TextTertiary
                : _lastTheme.TextSecondary;
    }

    /// <summary>Apply a desired icon state with exponential backoff, off the UI thread. Covers the
    /// --background login case where Explorer / Progman / DefView may not exist yet.</summary>
    private void ApplyIconsWithRetry(bool hidden)
    {
        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            int[] backoff = { 500, 1000, 2000, 4000, 8000 };
            for (int attempt = 0; attempt <= backoff.Length; attempt++)
            {
                IconApplyResult result;
                try
                {
                    result = _iconHider.ApplyDetailed(hidden);
                }
                catch (Exception ex)
                {
                    HostLog.Write($"启动应用图标意图（第 {attempt + 1} 次）异常", ex);
                    result = new IconApplyResult(IconApplyOutcome.Unknown, IconVisibility.Unknown, "异常");
                }

                if (result.Success)
                {
                    HostLog.Write($"启动应用图标意图成功（第 {attempt + 1} 次，{result.Strategy}）。");
                    if (!_shuttingDown) Dispatcher.BeginInvoke(() => { if (!_shuttingDown) SyncIconUI(); });
                    return;
                }

                // P1-8: do not drop the last attempt's outcome on the floor — the user needs to know
                // the desktop is not in the state their settings claim.
                if (attempt == backoff.Length)
                {
                    HostLog.Write($"启动应用图标意图失败：重试 {attempt + 1} 次后仍未生效 —— {result.Describe()}。");
                    if (_shuttingDown) return;
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (_shuttingDown) return;
                        SyncIconUI();
                        Status.Text = $"启动时未能应用「{(hidden ? "隐藏" : "显示")}桌面图标」—— {result.Describe()}。";
                    });
                    return;
                }
                System.Threading.Thread.Sleep(backoff[attempt]);
            }
        });
    }

    private void IntervalSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (IntervalLabel != null)
            IntervalLabel.Text = $"{(int)e.NewValue} 分钟";
        _settings.RotationIntervalMinutes = (int)e.NewValue;
        _settings.Save();
        _rotator?.ApplyInterval();
    }

    private void BtnRotateNow_Click(object sender, RoutedEventArgs e)
    {
        _rotator?.RotateNow();
    }

    private void BtnOpenLibrary_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string path = _rotator?.LibraryPath
                ?? Path.Combine(AppContext.BaseDirectory, "WallpaperLibrary");
            _rotator?.EnsureLibraryExists();
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            Status.Text = $"壁纸库已打开:\n{path}";
        }
        catch (Exception ex)
        {
            Status.Text = $"打开壁纸库失败:\n{ex.Message}";
        }
    }

    /// <summary>Rotator runs on a thread-pool thread; marshal the status text back to the UI thread.</summary>
    private void OnRotatorStatus(string msg)
    {
        Dispatcher.Invoke(() =>
        {
            if (LibStatus != null) LibStatus.Text = msg;
        });
    }

    private void ChkAudio_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressAudioEvents) return;   // scene apply already pushed audio through the engine
        _settings.AudioEnabled = ChkAudio.IsChecked == true;
        _settings.Save();
        if (_uiReady && _wallpaper.IsDynamicRunning)
        {
            bool on = _settings.AudioEnabled;
            MpvIpc.SetMute(!on);
            if (on) MpvIpc.SetVolume(_settings.Volume);
            _tray?.RefreshSoundLabel();
        }
    }

    /// <summary>Reflect the (possibly adopted) renderer state in the Wallpaper info box.</summary>
    private void RefreshWallpaperStateUI()
    {
        if (_wallpaper.IsDynamicRunning)
        {
            string text = "类型：动态（mpv）—— 运行中";
            if (!string.IsNullOrEmpty(_settings.LastMedia))
                text += $"\n文件：{Path.GetFileName(_settings.LastMedia)}";
            WallpaperInfo.Text = text;
        }
        else
        {
            WallpaperInfo.Text = "类型：无";
        }
    }

    private void BtnOpenLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!File.Exists(HostLog.LogPath))
            {
                Status.Text = $"暂无日志。文件位置：\n{HostLog.LogPath}";
                return;
            }
            Process.Start(new ProcessStartInfo(HostLog.LogPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Status.Text = $"无法打开日志（{ex.Message}）。\n尾部内容：\n{HostLog.ReadTail(12)}";
        }
    }

    private void BtnShowLogTail_Click(object sender, RoutedEventArgs e)
    {
        DiagInfo.Text = HostLog.ReadTail(30);
        Status.Text = $"日志尾部已显示。完整文件：\n{HostLog.LogPath}";
    }

    private void BtnDiagnose_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            DiagInfo.Text = WallpaperDiagnostics.Run(_wallpaper) + Environment.NewLine +
                            DesktopDiagnostics.Report(_settings, _iconHider, _scenes);
            Status.Text = "诊断完成（结果也已追加到日志）。";
        }
        catch (Exception ex)
        {
            DiagInfo.Text = $"诊断失败：{ex}";
            Status.Text = "诊断失败。";
        }
    }

    private void BtnCopyDiag_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string diag = string.IsNullOrWhiteSpace(DiagInfo.Text)
                ? WallpaperDiagnostics.Run(_wallpaper) + Environment.NewLine +
                  DesktopDiagnostics.Report(_settings, _iconHider, _scenes)
                : DiagInfo.Text;

            string payload =
                $"=== diagnostics ==={Environment.NewLine}{diag}{Environment.NewLine}" +
                $"=== log tail ==={Environment.NewLine}{HostLog.ReadTail(40)}";

            DiagInfo.Text = payload;
            Clipboard.SetText(payload);
            Status.Text = "诊断信息与日志已复制到剪贴板。";
        }
        catch (Exception ex)
        {
            Status.Text = $"复制失败：{ex.Message}\n日志文件：{HostLog.LogPath}";
        }
    }

    private void OnThemeChanged(object? sender, ResolvedTheme theme)
    {
        _lastTheme = theme;
        RootPanel.Background = theme.SurfaceBase;

        // The panel is Top-aligned inside a ScrollViewer, so any area below it would show the
        // bare window. Paint the window with an opaque version of the same surface colour.
        Background = Opaque(theme.SurfaceBase) ?? Background;

        TitleBlock.Foreground = theme.TextPrimary;
        SubtitleBlock.Foreground = theme.TextSecondary;
        ThemeInfo.Foreground = theme.TextSecondary;
        WallpaperInfo.Foreground = theme.TextSecondary;
        ChkAudio.Foreground = theme.TextPrimary;
        VolLabel.Foreground = theme.TextSecondary;
        ChkRotation.Foreground = theme.TextPrimary;
        ChkLaunchOnBoot.Foreground = theme.TextPrimary;
        IntervalLabel.Foreground = theme.TextSecondary;
        LibStatus.Foreground = theme.TextSecondary;
        DiagInfo.Foreground = theme.TextSecondary;
        DiagInfo.CaretBrush = theme.TextSecondary;
        DiagInfo.SelectionBrush = theme.Primary;
        Status.Foreground = theme.TextTertiary;
        Status.CaretBrush = theme.TextTertiary;
        Status.SelectionBrush = theme.Primary;

        // Phase 3 controls
        ChkHideIcons.Foreground = theme.TextPrimary;
        ChkRestoreIconsOnExit.Foreground = theme.TextPrimary;
        CmbScene.Foreground = theme.TextPrimary;
        CmbScene.Background = theme.SurfaceRaised;
        UpdateDesktopStatus();

        foreach (var child in LogicalTreeHelper.GetChildren(RootPanel))
        {
            if (child is GroupBox gb)
            {
                gb.Foreground = theme.TextPrimary;
                if (gb.Content is Panel p)
                    StylePanelButtons(p, theme);
            }
            else if (child is Panel panel)
            {
                StylePanelButtons(panel, theme);
            }
        }

    }

    /// <summary>Same colour, alpha forced to 255. Returns null for non-solid brushes.</summary>
    private static SolidColorBrush? Opaque(Brush? brush)
    {
        if (brush is not SolidColorBrush sb) return null;
        Color c = sb.Color;
        return new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B));
    }

    private static void StylePanelButtons(Panel panel, ResolvedTheme theme)
    {
        foreach (var c in LogicalTreeHelper.GetChildren(panel))
        {
            if (c is Button btn)
            {
                btn.Background = theme.SurfaceRaised;
                btn.Foreground = theme.TextPrimary;
                btn.BorderBrush = theme.SurfaceBorderStrong;
                btn.Tag = theme.Primary;
            }
            else if (c is Panel nested)
            {
                StylePanelButtons(nested, theme);
            }
        }
    }
}
