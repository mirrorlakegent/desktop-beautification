using System;
using DesktopSuite.Wallpaper;

namespace DesktopSuite.Desktop;

/// <summary>Live desktop-icon visibility, as observed from the shell (not from settings).</summary>
public enum IconVisibility
{
    Visible,
    Hidden,
    Unknown
}

/// <summary>
/// Why an <see cref="IconHider.ApplyDetailed"/> call ended the way it did.
///
/// The critical distinction (P1-7) is Unknown vs Failed:
///  - Unknown = we could not READ the shell, so we know nothing about reality. Callers MUST NOT
///    persist the user's intent on this outcome, or a transient unreadable shell silently rewrites
///    DesiredIconsHidden to a value we never verified.
///  - Failed   = the shell WAS readable and simply refused to obey. Reality is known, so persisting
///    intent is safe and a later retry can converge.
/// </summary>
public enum IconApplyOutcome
{
    /// <summary>The shell obeyed; reality now matches the request.</summary>
    Applied,
    /// <summary>Reality already matched the request; nothing was sent.</summary>
    AlreadyInState,
    /// <summary>Shell readable, but every strategy in the ladder failed to change it.</summary>
    Failed,
    /// <summary>Shell unreadable — reality is genuinely unknown. Do NOT persist intent.</summary>
    Unknown
}

/// <summary>Result of an icon apply attempt: the outcome, the reality we last observed, and which
/// strategy in the degrade ladder produced it (surfaced in diagnostics + the log).</summary>
public readonly record struct IconApplyResult(IconApplyOutcome Outcome, IconVisibility Reality, string Strategy)
{
    /// <summary>True when reality ended up matching what the caller asked for.</summary>
    public bool Success => Outcome is IconApplyOutcome.Applied or IconApplyOutcome.AlreadyInState;

    /// <summary>True when the shell was readable, so writing the user's intent to settings is safe (P1-7).</summary>
    public bool IsDeterministic => Outcome != IconApplyOutcome.Unknown;

    /// <summary>Short Chinese phrase for the status bar (P1-8: never fail silently).</summary>
    public string Describe() => Outcome switch
    {
        IconApplyOutcome.Applied        => $"已生效（{Strategy}）",
        IconApplyOutcome.AlreadyInState => "已处于目标状态",
        IconApplyOutcome.Failed         => "资源管理器未响应切换命令",
        _                               => "无法读取桌面图标层（状态未知）"
    };
}

/// <summary>
/// Toggle and observe desktop icon visibility.
///
/// Design rules (from the Phase 3 review):
///  - TRUTH SOURCE is the live SysListView32 WS_VISIBLE flag, never AppSettings. Settings only hold
///    the user's *intent* (DesiredIconsHidden); reality is always re-read before acting.
///  - NO handle caching — DesktopShell re-resolves every call, because Explorer can migrate DefView
///    when a wallpaper surface is spawned.
///  - PRIMARY path uses Explorer's own "toggle desktop icons" command (WM_COMMAND 0x7402 on DefView),
///    which keeps the desktop right-click menu alive and leaves the user a native escape hatch.
///  - DEGRADE chain: 0x7402 → retry once → ShowWindow(hide/show) → caller reports "unsupported".
///  - Every public method is wrapped so a missing shell never throws into the UI thread.
/// </summary>
public sealed class IconHider
{
    public event Action<IconVisibility>? StateChanged;

    public IconVisibility Current
    {
        get
        {
            bool? visible = DesktopShell.AreIconsVisible();
            if (visible == null) return IconVisibility.Unknown;
            return visible == true ? IconVisibility.Visible : IconVisibility.Hidden;
        }
    }

    /// <summary>Outcome of the most recent apply attempt, or null if none has run yet (P2: diagnostics).
    /// Nullable on purpose — a synthetic "Unknown" placeholder would read as a real failure in the UI.</summary>
    public IconApplyResult? LastResult { get; private set; }

    /// <summary>
    /// Idempotent: ensure the icons are in the desired state. Returns true only if reality matches.
    /// Thin wrapper over <see cref="ApplyDetailed"/>, kept so existing call sites stay valid.
    /// </summary>
    public bool Apply(bool hidden) => ApplyDetailed(hidden).Success;

    /// <summary>
    /// Idempotent apply that reports WHY it ended as it did, so callers can (a) tell the user
    /// (P1-8) and (b) decide whether persisting intent is safe (P1-7).
    /// </summary>
    public IconApplyResult ApplyDetailed(bool hidden)
    {
        IconApplyResult result;
        try
        {
            bool? initial = DesktopShell.AreIconsVisible();
            if (initial == null)
            {
                // P1-7: reality unreadable. Report Unknown so the caller skips the intent write.
                HostLog.Write($"IconHider.Apply(hidden={hidden}): 桌面图标层不可读 " +
                              "(SHELLDLL_DefView/SysListView32 未找到) — 判定为 Unknown，不落盘 intent。");
                result = new IconApplyResult(IconApplyOutcome.Unknown, IconVisibility.Unknown, "无（shell 不可读）");
            }
            else if (initial == !hidden)
            {
                HostLog.Write($"IconHider.Apply(hidden={hidden}): 已处于目标状态，未发送任何命令。");
                result = new IconApplyResult(IconApplyOutcome.AlreadyInState,
                                             hidden ? IconVisibility.Hidden : IconVisibility.Visible, "无需操作");
            }
            // Strategy ladder (see class doc):
            //   1) Explorer's native "toggle desktop icons" command (preserves the right-click menu).
            //   2) Retry the command once before degrading.
            //   3) Last resort: direct ShowWindow on the list view (may break the right-click menu).
            //
            // P1-1 fix: every verification treats "unknown" (null) as a HARD STOP. A transient
            // unreadable shell must never be mistaken for "didn't work" and escalated into the
            // destructive ShowWindow fallback (which would also be pointless if we can't read it back).
            else if (TryNative(hidden))
            {
                result = new IconApplyResult(IconApplyOutcome.Applied,
                                             hidden ? IconVisibility.Hidden : IconVisibility.Visible,
                                             "WM_COMMAND 0x7402");
            }
            else if (TryNative(hidden))
            {
                result = new IconApplyResult(IconApplyOutcome.Applied,
                                             hidden ? IconVisibility.Hidden : IconVisibility.Visible,
                                             "WM_COMMAND 0x7402（重试）");
            }
            else if (TryShowWindow(hidden))
            {
                result = new IconApplyResult(IconApplyOutcome.Applied,
                                             hidden ? IconVisibility.Hidden : IconVisibility.Visible,
                                             "ShowWindow 降级");
            }
            else
            {
                // Ladder exhausted. Re-read once to tell "shell refused" (Failed) apart from
                // "shell went unreadable mid-flight" (Unknown) — they need different caller handling.
                bool? after = DesktopShell.AreIconsVisible();
                if (after == null)
                {
                    HostLog.Write($"IconHider.Apply(hidden={hidden}) 失败：降级链耗尽且 shell 已不可读 → Unknown，不落盘 intent。");
                    result = new IconApplyResult(IconApplyOutcome.Unknown, IconVisibility.Unknown, "降级链耗尽");
                }
                else
                {
                    HostLog.Write($"IconHider.Apply(hidden={hidden}) 失败：降级链耗尽，" +
                                  $"资源管理器未响应；当前实际={(after == true ? "显示" : "隐藏")}。");
                    result = new IconApplyResult(IconApplyOutcome.Failed,
                                                 after == true ? IconVisibility.Visible : IconVisibility.Hidden,
                                                 "降级链耗尽");
                }
                LastResult = result;
                Raise();
                return result;
            }
        }
        catch (Exception ex)
        {
            HostLog.Write("IconHider.Apply 异常", ex);
            result = new IconApplyResult(IconApplyOutcome.Unknown, IconVisibility.Unknown, "异常");
        }

        LastResult = result;
        return result;
    }

    /// <summary>Send the native toggle command and confirm reality matches. Returns false if the
    /// command could not be sent, or if reality is unknown (null) — the caller must NOT escalate.</summary>
    private bool TryNative(bool hidden)
    {
        if (!ToggleViaCommand()) return false;
        bool? actual = DesktopShell.AreIconsVisible();
        if (actual == null) return false;
        return actual == !hidden;
    }

    /// <summary>Direct ShowWindow fallback and confirm reality matches. Returns false on failure or
    /// unknown reality (never escalate a destructive change we cannot verify).</summary>
    private bool TryShowWindow(bool hidden)
    {
        if (!ToggleViaShowWindow(hidden)) return false;
        bool? actual = DesktopShell.AreIconsVisible();
        if (actual == null) return false;
        return actual == !hidden;
    }

    public bool Toggle()
    {
        bool hide = Current != IconVisibility.Hidden;
        return Apply(hide);
    }

    /// <summary>
    /// Sync our *intent* to reality. Called when reality may have changed WITHOUT our action (e.g. the
    /// user used the right-click menu). We update DesiredIconsHidden to match — we do NOT force reality
    /// back, or we would fight the user. Only explicit user actions (checkbox / tray / scene) call Apply.
    /// </summary>
    public void ReconcileFromReality(AppSettings settings)
    {
        bool? actual = DesktopShell.AreIconsVisible();
        if (actual == null) return;
        bool hidden = actual == false;
        if (settings.DesiredIconsHidden != hidden)
        {
            settings.DesiredIconsHidden = hidden;
            settings.Save();
            Raise();
        }
    }

    private void Raise() => StateChanged?.Invoke(Current);

    private static bool ToggleViaCommand()
    {
        IntPtr dv = DesktopShell.FindDefView();
        if (dv == IntPtr.Zero) return false;

        // SendMessageTimeout (NOT SendMessage): if Explorer is hung we abort after 1s instead of
        // freezing the whole GUI thread. The P/Invoke returns IntPtr (the BOOL result); non-zero = success.
        IntPtr ok = NativeMethods.SendMessageTimeout(
            dv,
            NativeMethods.WM_COMMAND,
            new IntPtr(NativeMethods.SHELL_TOGGLE_DESKTOP_ICONS),
            IntPtr.Zero,
            NativeMethods.SMTO_NORMAL | NativeMethods.SMTO_ABORTIFHUNG,
            1000,
            out _);
        if (ok == IntPtr.Zero) return false;

        // Give Explorer a beat to actually flip the style before we re-read.
        System.Threading.Thread.Sleep(150);
        return true;
    }

    private static bool ToggleViaShowWindow(bool hidden)
    {
        IntPtr lv = DesktopShell.FindIconListView();
        if (lv == IntPtr.Zero) return false;
        NativeMethods.ShowWindow(lv, hidden ? NativeMethods.SW_HIDE : NativeMethods.SW_SHOW);
        return true;
    }
}
