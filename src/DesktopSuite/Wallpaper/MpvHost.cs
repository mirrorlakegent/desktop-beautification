using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace DesktopSuite.Wallpaper;

/// <summary>
/// Renders a video into a native window using mpv's --wid embedding.
/// This is the "integrate Lively first" approach: same technique, no full Lively install.
/// </summary>
public sealed class MpvHost : IDisposable
{
    private Process? _process;
    private readonly IntPtr _targetHwnd;
    private readonly string _mediaPath;
    private readonly string _mpvPath;
    private readonly bool _audioEnabled;
    private readonly int _volume;

    /// <summary>mpv JSON-IPC named pipe name (combined with \\.\pipe\ on Windows).</summary>
    public const string IpcPipeName = "desktopsuite-wp";

    public bool IsRunning => _process is { HasExited: false };
    public string MpvPath => _mpvPath;
    public string? CommandLine { get; private set; }
    public bool AudioEnabled => _audioEnabled;
    public int Volume => _volume;

    /// <summary>Raised when mpv exits on its own (crash, bad file, bad option...).</summary>
    public event EventHandler<int>? Exited;

    /// <summary>
    /// audioEnabled defaults to false: a wallpaper should stay silent unless the user opts in.
    /// volume is clamped to 0-100 (mpv's native range); ignored when audio is disabled.
    /// </summary>
    public MpvHost(IntPtr targetHwnd, string mediaPath, bool audioEnabled = false, int volume = 80)
    {
        _targetHwnd = targetHwnd;
        _mediaPath = Path.GetFullPath(mediaPath);
        _audioEnabled = audioEnabled;
        _volume = Math.Clamp(volume, 0, 100);
        _mpvPath = ResolveMpv()
            ?? throw new FileNotFoundException(
                "mpv.exe not found. Place mpv.exe next to the app or add it to PATH.", "mpv.exe");
    }

    public void Start()
    {
        if (_process != null) return;
        if (!File.Exists(_mediaPath))
            throw new FileNotFoundException("Media file not found.", _mediaPath);

        string args = BuildArguments(_targetHwnd, _mediaPath, _audioEnabled, _volume);
        CommandLine = $"\"{_mpvPath}\" {args}";
        HostLog.Write($"Launching mpv: {CommandLine}");

        var psi = new ProcessStartInfo
        {
            FileName = _mpvPath,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(_mpvPath) ?? AppContext.BaseDirectory,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        _process = Process.Start(psi)
            ?? throw new InvalidOperationException("Process.Start returned null for mpv.");

        // Streams MUST be drained. mpv writes continuously; an unread pipe fills up and
        // blocks the player, which looks exactly like "the wallpaper silently does nothing".
        _process.OutputDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) HostLog.Write($"[mpv] {e.Data}"); };
        _process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) HostLog.Write($"[mpv:err] {e.Data}"); };
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) =>
        {
            int code = -1;
            try { code = _process?.ExitCode ?? -1; } catch { }
            HostLog.Write($"mpv exited with code {code}");
            Exited?.Invoke(this, code);
        };

        HostLog.Write($"mpv started, pid {_process.Id}");
    }

    public void Stop()
    {
        if (_process == null) return;
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(2000);
            }
        }
        catch { }
        finally
        {
            _process.Dispose();
            _process = null;
        }
    }

    public void Dispose() => Stop();

    private static string BuildArguments(IntPtr hwnd, string mediaPath, bool audioEnabled, int volume)
    {
        // Every option here is verified against mpv's manual. A single unknown option makes
        // mpv abort at startup with exit code 1 and no visible window -- that was the original
        // failure: "--log-level" does not exist in mpv (it is "--msg-level").
        var opts = new List<string>
        {
            $"--wid={(long)hwnd}",      // embed into our native child window
            "--no-config",              // ignore any bundled/user mpv.conf that could override vo
            "--loop-file=inf",
            "--hwdec=auto",             // keep CPU usage low
            "--no-osc",
            "--no-osd-bar",
            "--no-border",
            "--keepaspect=no",          // fill the whole desktop
            "--image-display-duration=inf", // keep a still image on screen indefinitely (no-op for video)
            "--ontop=no",
            "--cursor-autohide=no",
            "--stop-screensaver=no",
            "--no-input-default-bindings",
            "--input-vo-keyboard=no",   // never steal keystrokes from the desktop
            "--input-cursor=no",
            "--force-window=yes",
            "--idle=no",
            "--msg-level=all=warn",     // correct spelling of the log-level option
            Quote(mediaPath)
        };

        // Always bring up the audio device (no --no-audio) so sound can be toggled at runtime via
        // the mpv IPC pipe. Start muted when the user opted out; unmute at the chosen volume otherwise.
        // The IPC server lets the GUI/tray change mute/volume without restarting the renderer.
        opts.Insert(opts.Count - 1, $"--volume={volume}");
        opts.Insert(opts.Count - 1, audioEnabled ? "--mute=no" : "--mute=yes");
        opts.Insert(opts.Count - 1, $@"--input-ipc-server=\\.\pipe\{IpcPipeName}");

        return string.Join(" ", opts);
    }

    private static string Quote(string path) => path.Contains(' ') ? $"\"{path}\"" : path;

    /// <summary>Locate mpv.exe without requiring an install. Returns null when unavailable.</summary>
    public static string? ResolveMpv()
    {
        // 1. Same directory as the executing assembly.
        string appDir = AppContext.BaseDirectory;
        string local = Path.Combine(appDir, "mpv.exe");
        if (File.Exists(local)) return local;

        // 2. Any mpv.exe visible on PATH.
        string? pathVar = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathVar))
        {
            foreach (var dir in pathVar.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                string candidate;
                try { candidate = Path.Combine(dir.Trim(), "mpv.exe"); }
                catch { continue; }
                if (File.Exists(candidate)) return candidate;
            }
        }

        // 3. Common Lively / portable locations.
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] likely =
        {
            Path.Combine(localAppData, "LivelyWallpaper", "mpv", "mpv.exe"),
            Path.Combine(localAppData, "Programs", "Lively Wallpaper", "Plugins", "mpv", "mpv.exe"),
            Path.Combine(appDir, "mpv", "mpv.exe"),
            Path.Combine(appDir, "Plugins", "mpv", "mpv.exe")
        };
        return likely.FirstOrDefault(File.Exists);
    }
}
