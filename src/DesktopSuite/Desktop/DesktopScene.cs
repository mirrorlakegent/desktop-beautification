namespace DesktopSuite.Desktop;

/// <summary>壁纸来源：跟随时段轮换，或固定某文件。</summary>
public enum WallpaperMode
{
    FollowRotation,
    Fixed
}

/// <summary>
/// 场景 = 一组命名桌面状态，一键切换。
/// 字段刻意保持简单，全部为纯数据（POCO），便于 JSON 序列化与单元测试。
/// </summary>
public sealed class DesktopScene
{
    public string Name { get; set; } = "";
    public bool IconsHidden { get; set; }
    public WallpaperMode WallpaperMode { get; set; } = WallpaperMode.FollowRotation;
    public string? FixedMediaPath { get; set; }
    public bool RotationEnabled { get; set; }
    public bool AudioEnabled { get; set; }
    public int Volume { get; set; } = 80;
}

/// <summary>
/// Outcome of applying a scene. <paramref name="Message"/> is a ready-to-display Chinese string that
/// already folds in any partial-success notes (e.g. icons unknown, fixed wallpaper file missing), so
/// callers never have to report a bare "失败" with no reason (P1-8).
/// </summary>
/// <param name="Success">True when the scene was found and committed without throwing.</param>
/// <param name="Message">Human-readable summary for the status bar.</param>
public sealed record SceneApplyResult(bool Success, string Message);
