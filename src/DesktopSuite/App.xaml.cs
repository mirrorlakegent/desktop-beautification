using System;
using System.IO;
using System.Windows;
using System.Threading;
using System.Windows.Threading;
using DesktopSuite.Themes;
using DesktopSuite.Wallpaper;

namespace DesktopSuite;

public partial class App : Application
{
    private WallpaperChildWindow? _hostWindow;
    private MpvHost? _mpv;

    private static readonly string SingleInstanceMutexName = "DesktopSuiteSingleInstance";
    private static readonly string ShowEventName = "DesktopSuiteShowWindow";
    private static Mutex? _singleInstanceMutex;
    private static EventWaitHandle? _showEvent;
    private static MainWindow? _mainWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Renderer mode: run a bare message pump for the native child window and never show UI.
        // base.OnStartup is deliberately skipped so App.xaml's StartupUri cannot create MainWindow here.
        if (Array.IndexOf(e.Args, "--wallpaper-host") >= 0)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            RunWallpaperHost(e.Args);
            Dispatcher.PushFrame(new DispatcherFrame());
            return;
        }

        // Elevated helper for the shortcut-arrow tweak: delete/restore the HKLM IsShortcut marker and
        // exit immediately. Runs BEFORE the single-instance mutex so it never collides with the live GUI
        // instance (which owns that mutex) — it must perform its registry write and terminate, with no
        // UI and no mutex handshake.
        // NOTE: On Win11 26100+ this is a no-op (ApplyIsShortcut returns false) — the feature is
        // unsupported due to Microsoft's shell32.dll icon pipeline refactor.
        int isIdx = Array.IndexOf(e.Args, "--apply-ishortcut");
        if (isIdx >= 0 && isIdx + 1 < e.Args.Length)
        {
            if (!ShellTweaks.IsShortcutSupported)
            {
                HostLog.Write($"去箭头：Build {Environment.OSVersion.Version.Build} 不支持，跳过");
                Environment.Exit(2);  // distinct code for "unsupported"
                return;
            }
            bool enable = e.Args[isIdx + 1].Equals("on", StringComparison.OrdinalIgnoreCase);
            int code = ShellTweaks.ApplyIsShortcut(enable) ? 0 : 1;
            Environment.Exit(code);
            return;
        }

        // P1.5: single-instance guard (GUI only — the renderer host above is exempt).
        // Use initiallyOwned=false so we never re-enter a mutex the current thread already holds; we
        // acquire it explicitly below. This also lets us RECOVER from an ABANDONED mutex left by a
        // previous instance that crashed without releasing it — otherwise a crash would block all
        // future relaunch until the kernel object was cleared by something else.
        _singleInstanceMutex = new Mutex(false, SingleInstanceMutexName, out bool createdNew);
        if (createdNew)
        {
            // We created it (unowned) — take ownership now.
            _singleInstanceMutex.WaitOne();
        }
        else
        {
            // The mutex already existed. Distinguish a live second instance from a crashed one:
            // WaitOne(0) returns false if a live owner holds it (-> defer and exit), or throws
            // AbandonedMutexException if the prior owner died (-> we legitimately take over).
            bool liveInstance = true;
            try { if (_singleInstanceMutex.WaitOne(0)) liveInstance = false; }
            catch (AbandonedMutexException) { liveInstance = false; }

            if (liveInstance)
            {
                try { using var ev = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName); ev.Set(); } catch { }
                Shutdown();
                return;
            }
            // else: sole instance recovered from an abandoned mutex — continue normally.
        }
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        ThreadPool.QueueUserWorkItem(_ =>
        {
            while (true)
            {
                try { _showEvent.WaitOne(); }
                catch { break; }
                var w = _mainWindow;
                if (w != null) w.Dispatcher.Invoke(() => w.ShowMainWindow());
            }
        });

        bool background = Array.IndexOf(e.Args, "--background") >= 0;

        // Keep the login auto-start Run entry pointed at this exact EXE (it may have been moved).
        try { StartupManager.SelfHeal(); }
        catch (Exception ex) { HostLog.Write($"Startup self-heal failed: {ex.Message}"); }

        // Load the default theme at startup so buttons/window colours are correct immediately.
        // If the preset is missing, keep the built-in defaults and log quietly.
        try
        {
            ThemeService.Current.LoadDefault();
        }
        catch (Exception ex)
        {
            HostLog.Write($"Default theme load failed: {ex.Message}");
            // Leave the built-in WPF defaults so the UI still opens.
        }

        var main = new MainWindow();
        _mainWindow = main;
        this.MainWindow = main;   // pin explicitly; we no longer use StartupUri

        // P1-2: a Windows shutdown/logoff tears the process down WITHOUT raising Window.OnClosed, so
        // the exit-time icon restore never ran and the user came back to a headless desktop.
        SessionEnding += OnSessionEnding;

        // ...and a window-independent backstop. WPF delivers Application.SessionEnding through a
        // window HWND, but the --background login path below never calls Show(), so the main window
        // has no handle and that event would never arrive — precisely the case where the icons are
        // most likely hidden. SystemEvents owns its own hidden message window, so it still fires.
        // Both routes funnel into the latched RestoreIconsOnTeardown, so they cannot double-run.
        Microsoft.Win32.SystemEvents.SessionEnding += OnSystemSessionEnding;
        if (background)
        {
            // Silent login-launch: run resident in the tray, never surface the window.
            main.Visibility = Visibility.Hidden;
            main.ShowInTaskbar = false;
        }
        else
        {
            main.Show();
        }
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
    }

    /// <summary>
    /// P1-2: restore the desktop icons when Windows is shutting down or logging the user off.
    ///
    /// This is the ONLY teardown notification we get for a reboot/logoff — OnClosed never fires — so
    /// without it a user who had icons hidden would find an empty desktop after restarting.
    ///
    /// We deliberately do NOT set e.Cancel: blocking a shutdown to tidy up icons is far worse than
    /// leaving them hidden. The work is a couple of window messages and is latched inside
    /// RestoreIconsOnTeardown, so it stays well within the shutdown grace period and cannot double-run
    /// if OnClosed happens to follow.
    /// </summary>
    private void OnSessionEnding(object sender, SessionEndingCancelEventArgs e)
    {
        try
        {
            string reason = e.ReasonSessionEnding == ReasonSessionEnding.Logoff ? "系统注销" : "系统关机";
            HostLog.Write($"SessionEnding（{e.ReasonSessionEnding}）—— 尝试恢复桌面图标。");
            _mainWindow?.RestoreIconsOnTeardown(reason);
        }
        catch (Exception ex)
        {
            // Never let cleanup throw into the shutdown path.
            HostLog.Write("SessionEnding 处理失败", ex);
        }
    }

    /// <summary>
    /// Window-independent twin of <see cref="OnSessionEnding"/>. Raised on the SystemEvents helper
    /// thread, which is fine for logging but NOT for touching UI-thread resources. We marshal the
    /// restore call through the main window's Dispatcher so RestoreIconsOnTeardown runs on the UI
    /// thread where _iconHider and _suppressIconEvents are safe to touch.
    /// </summary>
    private void OnSystemSessionEnding(object sender, Microsoft.Win32.SessionEndingEventArgs e)
    {
        try
        {
            HostLog.Write($"SystemEvents.SessionEnding（{e.Reason}）—— 尝试恢复桌面图标。");
            string reason = e.Reason == Microsoft.Win32.SessionEndReasons.Logoff ? "系统注销" : "系统关机";

            // V11A fix: marshal to the UI thread. RestoreIconsOnTeardown touches _settings,
            // _iconHider, and _suppressIconEvents — all UI-thread-affined. A direct call from the
            // SystemEvents MTA thread could race with UI event handlers or deadlock on COM shell calls.
            // Dispatcher.Invoke is synchronous; if the UI thread is blocked we fall back to a
            // best-effort direct call (better to try than to silently skip the restore).
            var w = _mainWindow;
            if (w != null)
            {
                try
                {
                    w.Dispatcher.Invoke(() => w.RestoreIconsOnTeardown(reason),
                        System.Windows.Threading.DispatcherPriority.Send);
                }
                catch (Exception ex)
                {
                    // Dispatcher unavailable (app shutting down) — try a direct call as last resort.
                    HostLog.Write("SystemEvents.SessionEnding: Dispatcher.Invoke failed, trying direct call", ex);
                    w.RestoreIconsOnTeardown(reason);
                }
            }
        }
        catch (Exception ex)
        {
            HostLog.Write("SystemEvents.SessionEnding 处理失败", ex);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // SystemEvents keeps a static, process-wide subscriber list — detach or we leak this App.
        try { Microsoft.Win32.SystemEvents.SessionEnding -= OnSystemSessionEnding; } catch { }
        _mpv?.Dispose();
        _hostWindow?.Dispose();
        try { _singleInstanceMutex?.ReleaseMutex(); } catch { }
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// Command line:
    ///   DesktopSuite.exe --wallpaper-host &lt;hwnd&gt; --media "&lt;path&gt;" [--audio on|off] [--volume 0-100]
    ///
    /// Creates a native child window under the wallpaper layer and embeds mpv into it.
    /// Audio is off by default (silent wallpaper); pass --audio on and a --volume to enable sound.
    /// Any failure exits the process immediately with a distinct code so the parent can report it.
    /// </summary>
    private void RunWallpaperHost(string[] args)
    {
        Report($"Renderer starting. args=[{string.Join(" ", args)}]");

        // Declared up front: the `||` short-circuit below means TryParse may never run,
        // so the compiler cannot prove definite assignment on an inline `out` variable.
        long hwndLong = 0;

        int hostIdx = Array.IndexOf(args, "--wallpaper-host");
        if (hostIdx < 0 || hostIdx + 1 >= args.Length || !long.TryParse(args[hostIdx + 1], out hwndLong))
        {
            Report("Invalid or missing --wallpaper-host value.");
            Environment.Exit(2);
        }

        IntPtr workerHwnd = new(hwndLong);

        string? media = null;
        int mediaIdx = Array.IndexOf(args, "--media");
        if (mediaIdx >= 0 && mediaIdx + 1 < args.Length)
            media = args[mediaIdx + 1].Trim('"');

        bool audioEnabled = false;
        int volume = 80;
        int audioIdx = Array.IndexOf(args, "--audio");
        if (audioIdx >= 0 && audioIdx + 1 < args.Length &&
            args[audioIdx + 1].Trim().Equals("on", StringComparison.OrdinalIgnoreCase))
        {
            audioEnabled = true;
        }
        int volIdx = Array.IndexOf(args, "--volume");
        if (volIdx >= 0 && volIdx + 1 < args.Length && int.TryParse(args[volIdx + 1], out int v))
            volume = Math.Clamp(v, 0, 100);

        if (string.IsNullOrWhiteSpace(media) || !System.IO.File.Exists(media))
        {
            Report($"Renderer needs --media pointing at an existing file. Got: {media ?? "(null)"}");
            Environment.Exit(3);
        }

        try
        {
            // The host window is a plain child of the wallpaper layer; mpv does the actual
            // compositing (which works against the layered WorkerW), so we never self-draw.
            _hostWindow = new WallpaperChildWindow();
            IntPtr child = _hostWindow.Create(workerHwnd);

            _mpv = new MpvHost(child, media!, audioEnabled, volume);

            // If mpv dies (bad codec, unknown option, missing DLL) the renderer has nothing left
            // to do. Exit so the parent notices instead of leaving a zombie process behind.
            _mpv.Exited += (_, code) =>
            {
                Report($"mpv exited (code {code}); shutting the renderer down.");
                Environment.Exit(code == 0 ? 0 : 4);
            };

            _mpv.Start();
            Report($"Renderer ready. child=0x{child.ToInt64():X} mpv='{_mpv.MpvPath}'");
        }
        catch (Exception ex)
        {
            Report($"Renderer failed: {ex.GetType().Name}: {ex.Message}");
            HostLog.Write("Renderer failure detail", ex);
            Environment.Exit(1);
        }
    }

    /// <summary>Write to both the shared log file and stderr (the parent drains that pipe).</summary>
    private static void Report(string message)
    {
        HostLog.Write(message);
        try { Console.Error.WriteLine(message); Console.Error.Flush(); } catch { }
    }
}
