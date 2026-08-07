using System.IO;
using System.Text;
using DesktopSuite.Wallpaper;

namespace DesktopSuite.Desktop;

/// <summary>
/// Read-only probe for the desktop-icon layer, appended to the wallpaper diagnostics. Kept in the
/// Desktop namespace (not Wallpaper) so there is no cyclical dependency: Wallpaper → Desktop would
/// otherwise point back at Wallpaper via the engine reference inside DesktopSceneManager.
/// </summary>
public static class DesktopDiagnostics
{
    /// <summary>
    /// Probe the icon layer, and — when the caller supplies them — the recorded intent, the outcome of
    /// the last apply, and whether the wallpaper library the built-in scenes depend on is actually
    /// present on disk (P1-5 runtime validation).
    /// All parameters are optional so existing no-arg call sites keep working.
    /// </summary>
    public static string Report(AppSettings? settings = null,
                                IconHider? hider = null,
                                DesktopSceneManager? scenes = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("== desktop icons ==");
        IntPtr dv = DesktopShell.FindDefView();
        sb.AppendLine($"DefView  : {Hex(dv)}");
        IntPtr lv = DesktopShell.FindIconListView();
        sb.AppendLine($"ListView : {Hex(lv)}");
        bool? visible = DesktopShell.AreIconsVisible();
        sb.AppendLine($"visible  : {(visible == null ? "unknown (shell unavailable)" : (visible == true ? "yes" : "no"))}");
        sb.AppendLine($"strategy : {(DesktopShell.IsVisible(dv) ? "DefView visible" : "DefView hidden")}");

        // Intent vs reality is the single most useful line when a user reports "it didn't work".
        if (settings != null)
        {
            sb.AppendLine($"intent   : {(settings.DesiredIconsHidden ? "hidden" : "visible")} (DesiredIconsHidden)");
            sb.AppendLine($"on exit  : {(settings.RestoreIconsOnExit ? "restore icons" : "leave as-is")}");
            sb.AppendLine($"scene    : {settings.ActiveSceneName ?? "(none)"}");
        }
        if (hider != null)
        {
            sb.AppendLine(hider.LastResult is { } r
                ? $"last op  : {r.Outcome} via {r.Strategy} (reality={r.Reality})"
                : "last op  : (none this session)");
        }

        // P1-5: the built-in scenes reference fixed media inside WallpaperLibrary. If the library was
        // not packaged, those scenes quietly do nothing — surface that here instead.
        sb.AppendLine();
        sb.AppendLine("== wallpaper library ==");
        string root = Path.Combine(AppContext.BaseDirectory, "WallpaperLibrary");
        sb.AppendLine($"root     : {root}");
        sb.AppendLine($"exists   : {(Directory.Exists(root) ? "yes" : "NO — 壁纸库缺失（未随发布包分发？）")}");
        if (scenes != null)
        {
            foreach (var s in scenes.Scenes)
            {
                if (s.WallpaperMode != WallpaperMode.Fixed) continue;
                if (string.IsNullOrWhiteSpace(s.FixedMediaPath))
                {
                    sb.AppendLine($"scene 「{s.Name}」: fixed mode but no path set");
                    continue;
                }
                bool ok = File.Exists(s.FixedMediaPath);
                sb.AppendLine($"scene 「{s.Name}」: {(ok ? "OK" : "MISSING")} {s.FixedMediaPath}");
            }
        }
        return sb.ToString();
    }

    private static string Hex(IntPtr h) => h == IntPtr.Zero ? "0 (none)" : $"0x{h.ToInt64():X}";
}
