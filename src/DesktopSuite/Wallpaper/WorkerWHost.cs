using System;
using System.Collections.Generic;
using System.Threading;

namespace DesktopSuite.Wallpaper;

/// <summary>
/// Finds (or creates) the window layer that sits behind the desktop icons.
///
/// Explorer has two common desktop shell shapes:
///   A) SHELLDLL_DefView lives inside a WorkerW; the wallpaper surface is another WorkerW nearby.
///   B) SHELLDLL_DefView stays inside Progman; the wallpaper surface is a WorkerW created on demand.
///
/// The most reliable rule across both shapes is:
///   1. Try to reuse an existing large WorkerW that does not host DefView.
///   2. If none exists, snapshot the current WorkerW set, send WM_SPAWN_WORKER to Progman,
///      and use the newly-created WorkerW.
///
/// The older "take the WorkerW immediately after the DefView holder in Z-order" rule works on
/// many machines but fails when the real wallpaper WorkerW is elsewhere in Z-order or has not
/// been spawned yet, which is exactly what the diagnostics screenshot showed.
/// </summary>
public sealed class WorkerWHost : IDisposable
{
    private IntPtr _target;

    public IntPtr Handle => _target;

    /// <summary>Which strategy produced the handle. Useful in logs when nothing shows up.</summary>
    public string Strategy { get; private set; } = "none";

    /// <summary>True when we had to parent onto Progman, which covers the desktop icons.</summary>
    public bool IsDegraded => Strategy == "progman-fallback";

    /// <summary>
    /// Side-effect-free prediction of the strategy <see cref="Acquire"/> will use.
    /// Does NOT send WM_SPAWN_WORKER, so it is safe to call from diagnostics and probes.
    /// Returns "workerw-*" if a usable WorkerW already exists, "workerw-will-spawn" if Acquire
    /// will need to create one, or "progman-fallback" if no WorkerW exists and Progman would be used.
    /// </summary>
    public static string PreviewStrategy()
    {
        IntPtr progman = NativeMethods.FindWindow("Progman", null);
        if (progman == IntPtr.Zero) return "unavailable (no Progman)";
        if (TryResolve(progman, out _, out string how)) return how;
        return "workerw-will-spawn";
    }

    /// <summary>
    /// Ensure a render target exists and return its handle. Safe to call multiple times.
    /// </summary>
    public IntPtr Acquire()
    {
        if (_target != IntPtr.Zero && NativeMethods.IsWindow(_target))
            return _target;

        IntPtr progman = NativeMethods.FindWindow("Progman", null);
        if (progman == IntPtr.Zero)
            throw new InvalidOperationException("Cannot find the Progman window; the desktop shell may not be running.");

        // 1. Reuse an existing wallpaper WorkerW without touching Explorer. The wallpaper surface
        //    is the first full-size WorkerW *below* Progman in z-order that does not host the icons.
        if (TryResolve(progman, out IntPtr found, out string how))
            return Commit(found, how);

        // 2. Spawn attempts (different parameter sets), re-resolving after each. Some shells only
        //    create the wallpaper WorkerW once 0x052C has been sent.
        HostLog.Write("No existing usable WorkerW; sending WM_SPAWN_WORKER and re-checking.");
        Spawn(progman, 0x0, 0x0);
        if (TryResolve(progman, out found, out how))
            return Commit(found, how);

        Spawn(progman, 0xD, 0x1);
        if (TryResolve(progman, out found, out how))
            return Commit(found, how);

        // 3. Last try: maybe a WorkerW appeared but was not recognised immediately. Wait a beat.
        if (WaitForExistingTarget(out found, out how))
            return Commit(found, how);

        // Degraded: render onto Progman. Content becomes visible but sits ON TOP of the icons,
        // so it is a last resort, and the caller is told about it via IsDegraded.
        _target = progman;
        Strategy = "progman-fallback";
        HostLog.Write($"WARNING: no usable WorkerW after spawn attempts; using Progman {Hex(progman)}. " +
                      "Desktop icons will be covered.");
        DumpShellTopology();
        return _target;
    }

    private IntPtr Commit(IntPtr hwnd, string how)
    {
        NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOW);
        _target = hwnd;
        Strategy = how;
        NativeMethods.GetWindowRect(hwnd, out var r);
        HostLog.Write($"Render target acquired: {Hex(hwnd)} via {how} " +
                      $"rect=({r.Left},{r.Top})-({r.Right},{r.Bottom}) {r.Width}x{r.Height}");
        return _target;
    }

    private static void Spawn(IntPtr progman, int wParam, int lParam)
    {
        HostLog.Write($"Sending WM_SPAWN_WORKER(0x052C) wParam=0x{wParam:X} lParam=0x{lParam:X} to Progman {Hex(progman)}");
        NativeMethods.SendMessageTimeout(
            progman,
            NativeMethods.WM_SPAWN_WORKER,
            new IntPtr(wParam),
            new IntPtr(lParam),
            NativeMethods.SMTO_NORMAL,
            1000,
            out _);
    }

    /// <summary>
    /// Resolve the wallpaper surface. Unified rule that works for both shell shapes:
    ///
    ///   The wallpaper layer is the first full-size WorkerW *below* Progman in z-order (GW_HWNDPREV)
    ///   that does NOT host the desktop icons (SHELLDLL_DefView). On shape A the DefView lives in a
    ///   WorkerW above the wallpaper WorkerW; on shape B it stays in Progman and the wallpaper
    ///   WorkerW is simply the window directly under Progman. Scanning downward from Progman and
    ///   skipping the icon host covers both.
    ///
    /// Visibility is intentionally NOT required: the wallpaper WorkerW sits at the very bottom of
    /// the desktop and IsWindowVisible returns false for it, which is exactly why an earlier version
    /// kept falling back to Progman. Size is the real discriminator.
    /// </summary>
    private static bool TryResolve(IntPtr progman, out IntPtr found, out string how)
    {
        found = IntPtr.Zero;
        how = "none";

        // 1. Top-level WorkerW directly below Progman in z-order (classic shape A / shape B with a
        //    top-level wallpaper WorkerW). Visibility is not required (it reports as hidden).
        IntPtr candidate = NativeMethods.GetWindow(progman, NativeMethods.GW_HWNDPREV);
        for (int i = 0; i < 12 && candidate != IntPtr.Zero; i++)
        {
            if (IsClassWorkerW(candidate) && !HoldsDefView(candidate) && IsLargeEnough(candidate))
            {
                found = candidate;
                how = "workerw-below-progman";
                return true;
            }
            candidate = NativeMethods.GetWindow(candidate, NativeMethods.GW_HWNDPREV);
        }

        // 2. On some Win11 builds the wallpaper WorkerW is created as a *child* of Progman rather
        //    than a top-level sibling. Walk Progman's children and take the full-size WorkerW that
        //    is not the icon host.
        IntPtr child = NativeMethods.FindWindowEx(progman, IntPtr.Zero, "WorkerW", null);
        int n = 0;
        while (child != IntPtr.Zero && n < 32)
        {
            if (!HoldsDefView(child) && IsLargeEnough(child))
            {
                found = child;
                how = "workerw-child-of-progman";
                return true;
            }
            child = NativeMethods.FindWindowEx(progman, child, "WorkerW", null);
            n++;
        }

        return false;
    }

    private static bool WaitForExistingTarget(out IntPtr found, out string how)
    {
        found = IntPtr.Zero;
        how = "none";
        IntPtr progman = NativeMethods.FindWindow("Progman", null);
        if (progman == IntPtr.Zero) return false;
        for (int i = 0; i < 20; i++)
        {
            if (TryResolve(progman, out found, out how))
                return true;
            Thread.Sleep(50);
        }
        return false;
    }

    private static bool IsClassWorkerW(IntPtr hWnd) => IsClass(hWnd, "WorkerW");

    private static bool HoldsDefView(IntPtr hWnd) =>
        NativeMethods.FindWindowEx(hWnd, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero;

    /// <summary>
    /// A real wallpaper surface spans most of the virtual desktop. We require at least 50% of the
    /// virtual screen area so the tiny unrelated WorkerW windows (often 170x47) are ignored.
    /// Visibility is deliberately NOT checked: the wallpaper WorkerW reports as not visible because
    /// it sits at the very bottom of the desktop stack.
    /// </summary>
    private static bool IsLargeEnough(IntPtr hWnd)
    {
        if (!NativeMethods.IsWindow(hWnd)) return false;
        if (!NativeMethods.GetWindowRect(hWnd, out var r)) return false;
        if (r.Width <= 0 || r.Height <= 0) return false;

        int vw = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
        int vh = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);
        if (vw <= 0 || vh <= 0) return true;

        long need = (long)vw * vh / 2; // 50% of the virtual desktop
        return (long)r.Width * r.Height >= need;
    }

    /// <summary>Walk the z-order chain downward from Progman. Written only on the degraded path so
    /// we can see exactly what TryResolve saw (and why it skipped each window).</summary>
    private static void DumpShellTopology()
    {
        try
        {
            IntPtr progman = NativeMethods.FindWindow("Progman", null);
            var lines = new List<string> { "Shell topology (Progman downward):" };
            IntPtr w = progman;
            for (int i = 0; i < 16 && w != IntPtr.Zero; i++)
            {
                NativeMethods.GetWindowRect(w, out var r);
                bool defView = HoldsDefView(w);
                bool big = IsLargeEnough(w);
                lines.Add($"  [{i}] {Hex(w)} {ClassOf(w),-8} defView={defView,-5} " +
                          $"big={big,-5} {r.Width}x{r.Height} @({r.Left},{r.Top})");
                w = NativeMethods.GetWindow(w, NativeMethods.GW_HWNDPREV);
            }
            HostLog.Write(string.Join(Environment.NewLine, lines));
        }
        catch { }
    }

    private static bool IsClass(IntPtr hWnd, string className) =>
        string.Equals(ClassOf(hWnd), className, StringComparison.Ordinal);

    private static string ClassOf(IntPtr hWnd)
    {
        var sb = new System.Text.StringBuilder(256);
        int len = NativeMethods.GetClassName(hWnd, sb, sb.Capacity);
        return len > 0 ? sb.ToString() : string.Empty;
    }

    private static string Hex(IntPtr h) => h == IntPtr.Zero ? "0(none)" : $"0x{h.ToInt64():X}";

    public void Dispose()
    {
        // We intentionally do NOT destroy WorkerW/Progman: they belong to Explorer.
        // The renderer's own child window is destroyed by the renderer process.
        _target = IntPtr.Zero;
    }
}
