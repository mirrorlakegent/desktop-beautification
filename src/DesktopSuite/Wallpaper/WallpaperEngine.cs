using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace DesktopSuite.Wallpaper;

/// <summary>
/// Orchestrates static and dynamic wallpapers.
/// Phase 1-2: static via Win32 SPI/COM; dynamic via an isolated renderer process
/// that embeds mpv into the wallpaper layer behind the desktop icons.
/// </summary>
public sealed class WallpaperEngine : IDisposable
{
    private readonly WorkerWHost _workerW = new();
    private Process? _rendererProcess;

    public bool IsDynamicRunning => _rendererProcess is { HasExited: false };
    public int RendererPid => _rendererProcess?.Id ?? 0;

    /// <summary>Last line the renderer reported, surfaced in the UI when something fails.</summary>
    public string? LastRendererMessage { get; private set; }

    /// <summary>How the render target was obtained on the most recent start.</summary>
    public string RenderStrategy => _workerW.Strategy;

    /// <summary>True when we had to fall back to Progman, which hides the desktop icons.</summary>
    public bool IsDegraded => _workerW.IsDegraded;

    /// <summary>
    /// Set a static image on all monitors.
    /// Stops any running dynamic wallpaper first to avoid fighting with the renderer.
    /// </summary>
    public void SetStatic(string imagePath)
    {
        StopDynamic();
        StaticWallpaper.SetWallpaperAllMonitorsPerMonitor(imagePath);
        HostLog.Write($"Static wallpaper set: {imagePath}");
    }

    /// <summary>
    /// Start a dynamic wallpaper in a separate renderer process.
    /// The renderer joins a Job Object with KILL_ON_JOB_CLOSE so it cannot outlive this app.
    /// audioEnabled defaults to false (silent wallpaper); volume is 0-100 and only used when audio is on.
    /// </summary>
    public void StartDynamic(string mediaPath, bool audioEnabled = false, int volume = 80)
    {
        if (!File.Exists(mediaPath))
            throw new FileNotFoundException("Media file not found.", mediaPath);

        // Pre-flight in the parent so the UI can give a precise reason instead of silence.
        string? mpv = MpvHost.ResolveMpv();
        if (mpv is null)
        {
            throw new FileNotFoundException(
                "mpv.exe was not found. Copy mpv.exe into the app output folder " +
                $"({AppContext.BaseDirectory}) or add it to PATH.", "mpv.exe");
        }
        int vol = Math.Clamp(volume, 0, 100);
        HostLog.Write($"--- StartDynamic --- media='{mediaPath}' mpv='{mpv}' audio={(audioEnabled ? "on" : "off")} volume={vol}");

        StopDynamic();

        IntPtr workerHwnd = _workerW.Acquire();
        HostLog.Write($"Render target 0x{workerHwnd.ToInt64():X} via {_workerW.Strategy}");

        string audioArg = $"--audio {(audioEnabled ? "on" : "off")} --volume {vol}";
        LaunchRenderer(workerHwnd, $"--media \"{mediaPath}\" {audioArg}");
    }

    /// <summary>
    /// Start a solid-colour probe (magenta) rendered through mpv.
    /// Used to separate "is the render target visible?" from "is my real video working?" -- the
    /// two failures that otherwise look identical on screen. We feed mpv a tiny solid-colour BMP
    /// so the probe reuses the exact same rendering path as a real video (mpv does the compositing
    /// against the layered wallpaper window; a plain GDI self-drawn child window would not show).
    /// </summary>
    public void StartTestPattern()
    {
        string? mpv = MpvHost.ResolveMpv();
        if (mpv is null)
        {
            throw new FileNotFoundException(
                "mpv.exe was not found. Copy mpv.exe into the app output folder " +
                $"({AppContext.BaseDirectory}) or add it to PATH.", "mpv.exe");
        }

        StopDynamic();

        IntPtr workerHwnd = _workerW.Acquire();
        HostLog.Write($"--- StartTestPattern --- render target 0x{workerHwnd.ToInt64():X} via {_workerW.Strategy}");

        string bmp = MakeSolidBmp(0xFF00FF);
        LaunchRenderer(workerHwnd, $"--media \"{bmp}\"");
    }

    /// <summary>Writes a tiny solid-colour BMP (default 64x64) and returns its path.</summary>
    private static string MakeSolidBmp(uint rgb)
    {
        const int w = 64, h = 64;
        byte r = (byte)(rgb >> 16), g = (byte)(rgb >> 8), b = (byte)rgb;
        int stride = ((w * 3 + 3) / 4) * 4;
        int pixelBytes = stride * h;
        int fileSize = 14 + 40 + pixelBytes;

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        // BITMAPFILEHEADER
        bw.Write((byte)'B'); bw.Write((byte)'M');
        bw.Write(fileSize);
        bw.Write((ushort)0); bw.Write((ushort)0);
        bw.Write(14 + 40);
        // BITMAPINFOHEADER
        bw.Write(40);
        bw.Write(w); bw.Write(h);
        bw.Write((ushort)1);
        bw.Write((ushort)24);
        bw.Write(0);
        bw.Write(pixelBytes);
        bw.Write(0); bw.Write(0);
        bw.Write(0); bw.Write(0);
        // Pixels, bottom-up, BGR with 4-byte row padding.
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                bw.Write(b); bw.Write(g); bw.Write(r);
            }
            for (int p = stride - w * 3; p > 0; p--) bw.Write((byte)0);
        }

        string path = Path.Combine(Path.GetTempPath(), $"ds_test_{Guid.NewGuid().ToString("N")}.bmp");
        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    /// <summary>
    /// Launch a copy of ourselves in wallpaper-host mode: same binary, dedicated renderer
    /// process (three-process isolation goal).
    ///
    /// IMPORTANT: under 'dotnet run', MainModule.FileName points at dotnet.exe, which would
    /// choke on --wallpaper-host. Always prefer the real apphost EXE in the output folder.
    /// </summary>
    private void LaunchRenderer(IntPtr workerHwnd, string extraArgs)
    {
        string apphost = Path.Combine(AppContext.BaseDirectory, "DesktopSuite.exe");
        string exe = File.Exists(apphost)
            ? apphost
            : (Process.GetCurrentProcess().MainModule?.FileName ?? apphost);

        string args = $"--wallpaper-host {(long)workerHwnd} {extraArgs}";
        HostLog.Write($"Spawning renderer: \"{exe}\" {args}");

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        // Exactly one Start(). A duplicate here would orphan an untracked renderer that keeps
        // running forever and fights the tracked one over the same wallpaper surface.
        _rendererProcess = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start the wallpaper renderer process.");

        _rendererProcess.OutputDataReceived += OnRendererOutput;
        _rendererProcess.ErrorDataReceived += OnRendererOutput;
        _rendererProcess.BeginOutputReadLine();
        _rendererProcess.BeginErrorReadLine();

        // NOTE: deliberately NOT placing the renderer in a Job Object with KILL_ON_JOB_CLOSE.
        // The wallpaper is meant to outlive the GUI, so the renderer must run independently of
        // this process's lifetime. Stale renderers are reclaimed via StopByPid/Adopt on the
        // next launch (PID persisted in settings).

        // Give it a beat: if the renderer dies immediately, report it instead of pretending success.
        if (_rendererProcess.WaitForExit(1200))
        {
            int code = _rendererProcess.ExitCode;
            string tail = LastRendererMessage ?? "(no output)";
            HostLog.Write($"Renderer exited early with code {code}: {tail}");
            throw new InvalidOperationException(
                $"The wallpaper renderer exited immediately (code {code}).\n{tail}\nLog: {HostLog.LogPath}");
        }

        HostLog.Write($"Renderer alive, pid {_rendererProcess.Id}");
    }

    private void OnRendererOutput(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Data)) return;
        LastRendererMessage = e.Data;
        HostLog.Write($"[renderer] {e.Data}");
    }

    public void StopDynamic()
    {
        if (_rendererProcess != null)
        {
            try
            {
                if (!_rendererProcess.HasExited)
                {
                    _rendererProcess.Kill(entireProcessTree: true);
                    _rendererProcess.WaitForExit(2000);
                }
            }
            catch { }
            _rendererProcess.Dispose();
            _rendererProcess = null;
            HostLog.Write("Renderer stopped.");
        }
    }

    /// <summary>
    /// Kill a renderer we do not have an in-memory handle for (e.g. one started by a previous
    /// GUI session). Safe no-op when the pid is stale, the process is already gone, or the pid
    /// was recycled to an unrelated process (guarded by the DesktopSuite process name).
    /// </summary>
    public void StopByPid(int pid)
    {
        if (pid <= 0 || pid == Environment.ProcessId) return;
        try
        {
            var p = Process.GetProcessById(pid);
            if (!p.ProcessName.Equals("DesktopSuite", StringComparison.OrdinalIgnoreCase)) return;
            if (!p.HasExited)
            {
                p.Kill(entireProcessTree: true);
                p.WaitForExit(2000);
            }
        }
        catch { }
    }

    /// <summary>
    /// Re-adopt a still-running renderer from a previous session so the UI can reflect and
    /// later stop it. Does nothing if the pid is invalid, the process is dead, the pid was
    /// recycled to an unrelated process, or we already track a live renderer.
    /// </summary>
    public void Adopt(int pid)
    {
        if (pid <= 0 || pid == Environment.ProcessId || _rendererProcess is { HasExited: false }) return;
        try
        {
            var p = Process.GetProcessById(pid);
            if (p.HasExited || !p.ProcessName.Equals("DesktopSuite", StringComparison.OrdinalIgnoreCase)) return;
            _rendererProcess = p;
            _rendererProcess.EnableRaisingEvents = true;
            _rendererProcess.Exited += (_, _) => { _rendererProcess = null; };
            HostLog.Write($"Adopted existing renderer pid {pid}");
        }
        catch { }
    }

    /// <summary>
    /// Change the running renderer's mute/volume at runtime through mpv's JSON IPC pipe, so the
    /// user can toggle sound from the tray (or the main window) without restarting the wallpaper.
    /// MpvIpc swallows a missing/busy pipe; callers still persist intent to settings either way.
    /// </summary>
    public void SetAudioRuntime(bool enabled, int volume)
    {
        try
        {
            if (enabled)
            {
                MpvIpc.SetMute(false);
                MpvIpc.SetVolume(Math.Clamp(volume, 0, 100));
            }
            else
            {
                MpvIpc.SetMute(true);
            }
        }
        catch { }
    }

    public void Dispose()
    {
        // Intentionally do NOT stop the renderer here. The wallpaper is designed to persist after
        // the GUI closes; the renderer runs detached. Only release our local window handle.
        _workerW.Dispose();
    }
}
