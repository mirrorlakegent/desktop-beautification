using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DesktopSuite.Wallpaper;

/// <summary>
/// Read-only environment probe for the wallpaper pipeline.
///
/// Deliberately side-effect free: it never sends WM_SPAWN_WORKER and never creates windows,
/// so running it cannot change the very state we are trying to observe. That makes it safe
/// to press the button before, during and after a failed dynamic-wallpaper attempt.
/// </summary>
public static class WallpaperDiagnostics
{
    public static string Run(WallpaperEngine? engine = null)
    {
        var sb = new StringBuilder();

        sb.AppendLine("== app ==");
        sb.AppendLine($"base dir : {AppContext.BaseDirectory}");
        sb.AppendLine($"pid      : {Environment.ProcessId}");
        sb.AppendLine($"os       : {Environment.OSVersion.Version}");

        sb.AppendLine();
        sb.AppendLine("== mpv ==");
        string? mpv = MpvHost.ResolveMpv();
        if (mpv is null)
        {
            sb.AppendLine("mpv.exe  : NOT FOUND");
            sb.AppendLine($"  expected: {Path.Combine(AppContext.BaseDirectory, "mpv.exe")}");
        }
        else
        {
            sb.AppendLine($"mpv.exe  : {mpv}");
            try { sb.AppendLine($"  size   : {new FileInfo(mpv).Length / 1024 / 1024} MB"); } catch { }
        }

        sb.AppendLine();
        sb.AppendLine("== desktop shell ==");
        IntPtr progman = NativeMethods.FindWindow("Progman", null);
        sb.AppendLine($"Progman  : {Hex(progman)}");

        if (progman != IntPtr.Zero)
        {
            bool defViewInProgman =
                NativeMethods.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero;
            sb.AppendLine($"  DefView inside Progman : {defViewInProgman}");
        }

        var workers = EnumerateWorkerW();
        sb.AppendLine($"WorkerW count : {workers.Count}");
        foreach (var (hwnd, hasDefView, w, h, vis) in workers)
            sb.AppendLine($"  {Hex(hwnd)}  DefView={hasDefView,-5} vis={vis,-5} {w,5}x{h,-5}");

        sb.AppendLine($"predicted strategy : {WorkerWHost.PreviewStrategy()}");

        if (progman != IntPtr.Zero)
        {
            sb.AppendLine("Progman children (class/size):");
            IntPtr child = NativeMethods.FindWindowEx(progman, IntPtr.Zero, null, null);
            int n = 0;
            while (child != IntPtr.Zero && n < 32)
            {
                NativeMethods.GetWindowRect(child, out var r);
                bool dv = NativeMethods.FindWindowEx(child, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero;
                sb.AppendLine($"  {Hex(child)} {ClassOf(child),-12} dv={dv,-5} " +
                              $"vis={NativeMethods.IsWindowVisible(child),-5} {r.Width}x{r.Height}");
                child = NativeMethods.FindWindowEx(progman, child, null, null);
                n++;
            }

            sb.AppendLine("Z-order below Progman (GW_HWNDPREV):");
            IntPtr z = progman;
            int zi = 0;
            while (z != IntPtr.Zero && zi < 16)
            {
                NativeMethods.GetWindowRect(z, out var r);
                sb.AppendLine($"  [{zi}] {Hex(z)} {ClassOf(z),-10} {r.Width}x{r.Height}");
                z = NativeMethods.GetWindow(z, NativeMethods.GW_HWNDPREV);
                zi++;
            }
        }

        sb.AppendLine();
        sb.AppendLine("== renderer ==");
        if (engine is null)
        {
            sb.AppendLine("engine  : (not supplied)");
        }
        else
        {
            sb.AppendLine($"running : {engine.IsDynamicRunning}");
            sb.AppendLine($"pid     : {(engine.RendererPid > 0 ? engine.RendererPid.ToString() : "-")}");
        }

        sb.AppendLine();
        sb.AppendLine("== log ==");
        sb.AppendLine($"path : {HostLog.LogPath}");
        try
        {
            var fi = new FileInfo(HostLog.LogPath);
            sb.AppendLine(fi.Exists
                ? $"size : {fi.Length} bytes, modified {fi.LastWriteTime:HH:mm:ss}"
                : "size : (file does not exist yet)");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"size : (unreadable: {ex.Message})");
        }

        string report = sb.ToString();
        // Write only a marker to the wallpaper log; the full report goes to the UI.
        // (Writing the whole report here would echo it back into the "log tail" the user copies.)
        HostLog.Write($"Diagnostics run at {DateTime.Now:HH:mm:ss} (full report returned to UI).");
        return report;
    }

    private static List<(IntPtr Hwnd, bool HasDefView, int W, int H, bool Visible)> EnumerateWorkerW()
    {
        var list = new List<(IntPtr, bool, int, int, bool)>();
        NativeMethods.EnumWindows((hWnd, _) =>
        {
            if (!IsClass(hWnd, "WorkerW")) return true;
            bool hasDefView =
                NativeMethods.FindWindowEx(hWnd, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero;
            NativeMethods.GetWindowRect(hWnd, out var r);
            list.Add((hWnd, hasDefView, r.Width, r.Height, NativeMethods.IsWindowVisible(hWnd)));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    private static bool IsClass(IntPtr hWnd, string className)
    {
        var sb = new StringBuilder(256);
        int len = NativeMethods.GetClassName(hWnd, sb, sb.Capacity);
        return len > 0 && string.Equals(sb.ToString(), className, StringComparison.Ordinal);
    }

    private static string ClassOf(IntPtr hWnd)
    {
        var sb = new StringBuilder(256);
        int len = NativeMethods.GetClassName(hWnd, sb, sb.Capacity);
        return len > 0 ? sb.ToString() : string.Empty;
    }

    private static string Hex(IntPtr h) => h == IntPtr.Zero ? "0 (none)" : $"0x{h.ToInt64():X}";
}
