using System;
using System.Runtime.InteropServices;
using DesktopSuite.Wallpaper;

namespace DesktopSuite.Desktop.Organizer;

/// <summary>
/// Global low-level mouse hook (<c>WH_MOUSE_LL</c>) that detects a double-click anywhere and
/// reports the screen coordinates through a callback. This is how we let the user toggle the fences
/// overlay by double-clicking the empty desktop background: the overlay window is click-through
/// outside its boxes, so it can never see that double-click through its own <c>WndProc</c>.
///
/// The hook is installed on the calling thread, which MUST pump a message loop — the WPF Dispatcher
/// thread qualifies. The callback therefore runs on that same thread (UI thread), so it is safe to
/// touch WPF/Win32 state directly. Keep the callback cheap: it fires on every mouse button-down
/// system-wide.
/// </summary>
public sealed class DesktopDoubleClickHook : IDisposable
{
    private readonly FenceNative.LowLevelMouseProc _proc; // held alive: OS keeps a raw pointer
    private IntPtr _hookId = IntPtr.Zero;
    private readonly Action<int, int> _onDoubleClick;
    private readonly object _gate = new();

    // Double-click tracking: a click is "primed" after the first WM_LBUTTONDOWN; if a second one
    // arrives within GetDoubleClickTime() and close to the first, it is a double-click.
    private bool _primed;
    private int _lastDownTick;
    private int _lastX, _lastY;

    public DesktopDoubleClickHook(Action<int, int> onDoubleClick)
    {
        _onDoubleClick = onDoubleClick ?? throw new ArgumentNullException(nameof(onDoubleClick));
        _proc = HookCallback;

        // WH_MOUSE_LL callbacks run in the installing thread's context, so passing the current
        // process module handle (or null) is sufficient — no DLL injection across processes.
        _hookId = FenceNative.SetWindowsHookEx(
            FenceNative.WH_MOUSE_LL,
            _proc,
            NativeMethods.GetModuleHandle(null),
            0);
        if (_hookId == IntPtr.Zero)
            HostLog.Write("DesktopDoubleClickHook：SetWindowsHookEx 失败（可能受安全软件限制）");
        else
            HostLog.Write("DesktopDoubleClickHook：已安装 WH_MOUSE_LL 全局鼠标钩子");
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam == (IntPtr)FenceNative.WM_LBUTTONDOWN)
        {
            var info = Marshal.PtrToStructure<FenceNative.MSLLHOOKSTRUCT>(lParam);
            int now = Environment.TickCount & 0x7FFFFFFF; // avoid negative wrap
            int dblMs = FenceNative.GetDoubleClickTime();
            int tolX = NativeMethods.GetSystemMetrics(FenceNative.SM_CXDOUBLECLK) / 2;
            int tolY = NativeMethods.GetSystemMetrics(FenceNative.SM_CYDOUBLECLK) / 2;

            lock (_gate)
            {
                if (_primed)
                {
                    // unchecked uint arithmetic makes the 49.7-day TickCount wrap safe.
                    uint delta = unchecked((uint)(now - _lastDownTick));
                    if (delta <= (uint)dblMs &&
                        Math.Abs(info.pt.X - _lastX) <= tolX &&
                        Math.Abs(info.pt.Y - _lastY) <= tolY)
                    {
                        _primed = false; // consume the pair
                        _onDoubleClick?.Invoke(info.pt.X, info.pt.Y);
                        return FenceNative.CallNextHookEx(_hookId, nCode, wParam, lParam);
                    }
                }
                _primed = true;
                _lastDownTick = now;
                _lastX = info.pt.X;
                _lastY = info.pt.Y;
            }
        }
        return FenceNative.CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hookId != IntPtr.Zero)
        {
            FenceNative.UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
        HostLog.Write("DesktopDoubleClickHook：已卸载全局鼠标钩子");
        GC.SuppressFinalize(this);
    }
}
