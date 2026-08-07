using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using DesktopSuite.Wallpaper;

namespace DesktopSuite.Desktop;

/// <summary>
/// Manages the list of desktop "scenes" (named desktop-state presets) and applies them. Persistence
/// lives in its OWN file (scenes.json) so it never pollutes settings.json.
///
/// Apply order (per the Phase 3 review): 图标可见性 → 轮换开关 → 壁纸 → 声音. We switch icons first
/// so there is no flicker of the wallpaper restarting behind visible icons.
/// </summary>
public sealed class DesktopSceneManager
{
    private static readonly string FilePath =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DesktopSuite", "scenes.json");

    private static readonly HashSet<string> VideoExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".mov", ".webm", ".avi"
    };

    public IReadOnlyList<DesktopScene> Scenes { get; private set; } = new List<DesktopScene>();
    public DesktopScene? Active { get; private set; }

    public DesktopSceneManager()
    {
        LoadOrCreate();
    }

    private void LoadOrCreate()
    {
        if (File.Exists(FilePath))
        {
            try
            {
                var list = JsonSerializer.Deserialize<List<DesktopScene>>(File.ReadAllText(FilePath));
                if (list is { Count: > 0 })
                {
                    Scenes = list;
                    return;
                }
            }
            catch (Exception ex)
            {
                HostLog.Write("Scene load failed, regenerating built-ins", ex);
            }
        }
        Scenes = BuiltInScenes();
        Save();
    }

    /// <summary>Built-in, ready-to-use presets. Fixed wallpapers point at the bundled library so the
    /// effect is visible immediately; if those files are missing the scene simply leaves rotation off.</summary>
    private static List<DesktopScene> BuiltInScenes()
    {
        string lib = Path.Combine(AppContext.BaseDirectory, "WallpaperLibrary");
        return new List<DesktopScene>
        {
            new()
            {
                Name = "日常",
                IconsHidden = false,
                WallpaperMode = WallpaperMode.FollowRotation,
                RotationEnabled = true,
                AudioEnabled = false,
                Volume = 80
            },
            new()
            {
                Name = "专注",
                IconsHidden = true,
                WallpaperMode = WallpaperMode.Fixed,
                FixedMediaPath = Path.Combine(lib, "深夜", "动态壁纸", "milkyway-1.mp4"),
                RotationEnabled = false,
                AudioEnabled = false,
                Volume = 0
            },
            new()
            {
                Name = "演示",
                IconsHidden = true,
                WallpaperMode = WallpaperMode.Fixed,
                FixedMediaPath = Path.Combine(lib, "晚上", "动态壁纸", "night-city-1.mp4"),
                RotationEnabled = false,
                AudioEnabled = false,
                Volume = 0
            },
        };
    }

    public DesktopScene? Find(string name)
    {
        foreach (var s in Scenes)
            if (string.Equals(s.Name, name, StringComparison.Ordinal))
                return s;
        return null;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath,
                JsonSerializer.Serialize(Scenes, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            HostLog.Write("Scene save failed", ex);
        }
    }

    /// <summary>Apply a named scene. Returns true if it was found and applied without throwing.
    /// Thin wrapper over <see cref="ApplySceneDetailed"/>, kept so existing call sites stay valid.</summary>
    public bool ApplyScene(string name, WallpaperEngine engine, WallpaperRotator rotator,
                           IconHider hider, AppSettings settings)
        => ApplySceneDetailed(name, engine, rotator, hider, settings).Success;

    /// <summary>
    /// Apply a named scene and report what actually happened.
    ///
    /// P1-4 (atomicity): every AppSettings field this method can touch is snapshotted on entry. The
    /// mutations are staged in memory and committed with a SINGLE Save() at the end; if any step
    /// throws, the snapshot is restored and re-saved so settings.json can never hold a half-applied
    /// scene. (rotator.SetEnabled persists RotationEnabled on its own, so the rollback path also
    /// re-invokes it to put the rotator back in agreement with the restored settings.)
    ///
    /// P1-7: the icon intent (DesiredIconsHidden) is only written when the shell was actually
    /// readable — an Unknown result leaves the previous intent untouched and is logged instead.
    /// </summary>
    public SceneApplyResult ApplySceneDetailed(string name, WallpaperEngine engine, WallpaperRotator rotator,
                                               IconHider hider, AppSettings settings)
    {
        var scene = Find(name);
        if (scene is null) return new SceneApplyResult(false, $"未找到场景「{name}」");

        // --- snapshot for rollback (P1-4) ---
        bool prevRotation = settings.RotationEnabled;
        bool prevAudio = settings.AudioEnabled;
        int prevVolume = settings.Volume;
        bool prevIcons = settings.DesiredIconsHidden;
        string? prevScene = settings.ActiveSceneName;
        DesktopScene? prevActive = Active;

        var notes = new List<string>();

        try
        {
            // 1) icons
            IconApplyResult iconResult = hider.ApplyDetailed(scene.IconsHidden);
            if (!iconResult.Success)
                notes.Add($"图标：{iconResult.Describe()}");

            // 2) rotation toggle
            rotator.SetEnabled(scene.RotationEnabled);
            settings.RotationEnabled = scene.RotationEnabled;

            // 3) wallpaper
            if (scene.WallpaperMode == WallpaperMode.Fixed)
            {
                if (string.IsNullOrWhiteSpace(scene.FixedMediaPath))
                {
                    notes.Add("壁纸：该场景未指定固定壁纸路径");
                }
                else if (!File.Exists(scene.FixedMediaPath))
                {
                    // P1-5: the bundled WallpaperLibrary may be missing entirely (not packaged into the
                    // release, or deleted by the user). Previously this branch silently did nothing and
                    // the user saw "scene applied" with an unchanged wallpaper. Say so explicitly.
                    notes.Add($"壁纸：文件缺失「{Path.GetFileName(scene.FixedMediaPath)}」，壁纸未切换");
                    HostLog.Write($"场景「{scene.Name}」固定壁纸缺失：{scene.FixedMediaPath} " +
                                  "（检查 WallpaperLibrary 是否随发布包一起分发）");
                }
                else
                {
                    ApplyMedia(engine, scene.FixedMediaPath);
                }
            }
            else if (scene.RotationEnabled)
            {
                // FollowRotation: rotation was enabled in step 2 (SetEnabled(true) already kicks off a
                // single tick that applies the current-period wallpaper). Do NOT also call RotateNow()
                // here — that would queue a second immediate tick and cause a visible double-flash (P1-3).
            }

            // 4) audio
            settings.AudioEnabled = scene.AudioEnabled;
            settings.Volume = scene.Volume;
            if (engine.IsDynamicRunning)
                engine.SetAudioRuntime(scene.AudioEnabled, scene.Volume);

            // P1-7: only record the icon intent when reality was readable.
            if (iconResult.IsDeterministic)
            {
                settings.DesiredIconsHidden = scene.IconsHidden;
            }
            else
            {
                HostLog.Write($"场景「{scene.Name}」：图标状态未知（{iconResult.Strategy}），" +
                              $"保留既有 DesiredIconsHidden={prevIcons}，不写入场景意图。");
            }

            settings.ActiveSceneName = scene.Name;
            settings.Save();     // single commit point (P1-4)
            Active = scene;
            Save();

            string msg = notes.Count == 0
                ? $"已应用场景：{scene.Name}"
                : $"已应用场景：{scene.Name}（{string.Join("；", notes)}）";
            return new SceneApplyResult(true, msg);
        }
        catch (Exception ex)
        {
            // P1-4: restore the snapshot so nothing half-applied survives on disk.
            HostLog.Write($"ApplyScene「{scene.Name}」失败，正在回滚设置", ex);
            settings.RotationEnabled = prevRotation;
            settings.AudioEnabled = prevAudio;
            settings.Volume = prevVolume;
            settings.DesiredIconsHidden = prevIcons;
            settings.ActiveSceneName = prevScene;
            Active = prevActive;
            try { settings.Save(); } catch { }
            try { rotator.SetEnabled(prevRotation); } catch { }
            return new SceneApplyResult(false, $"应用场景失败：{scene.Name} —— {ex.Message}（设置已回滚）");
        }
    }

    private static void ApplyMedia(WallpaperEngine engine, string path)
    {
        string ext = Path.GetExtension(path);
        if (VideoExt.Contains(ext))
            engine.StartDynamic(path, audioEnabled: false, volume: 0);
        else
            engine.SetStatic(path);
    }
}
