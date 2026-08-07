using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DesktopSuite.Wallpaper;

namespace DesktopSuite;

/// <summary>
/// Time-based wallpaper rotator. Watches the clock, picks a random file from the
/// current time-of-day bucket in the WallpaperLibrary, and applies it through the
/// existing mpv-backed WallpaperEngine — so static images and videos both render
/// behind the desktop icons, consistently.
///
/// Design notes (lead synthesis, 2026-08-03):
///  - Library layout: &lt;root&gt;/WallpaperLibrary/&lt;时段&gt;/{静态壁纸,动态壁纸}/
///  - Periods: 清晨5-7 / 早上8-10 / 中午11-12 / 下午13-16 / 傍晚17-18 / 黄昏19-20 / 晚上21-22 / 深夜23-4
///  - Random-without-immediate-repeat via a per-period shuffled bag.
///  - The wallpaper process is intentionally detached; rotation just re-launches it on the
///    same WorkerW surface, re-adopting the prior pid first.
/// </summary>
public sealed class WallpaperRotator : IDisposable
{
    private static readonly string[] Periods =
    {
        "清晨", "早上", "中午", "下午", "傍晚", "黄昏", "晚上", "深夜"
    };

    private static readonly HashSet<string> ImageExt =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".bmp", ".webp", ".gif"
        };
    private static readonly HashSet<string> VideoExt =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".mkv", ".mov", ".webm", ".avi"
        };

    private readonly WallpaperEngine _engine;
    private readonly AppSettings _settings;
    private readonly System.Timers.Timer _timer = new() { AutoReset = true };
    private readonly object _gate = new();
    private readonly Dictionary<string, Queue<string>> _bags = new();
    private string _activePeriod = "";
    private string? _lastApplied;

    public event Action<string>? StatusChanged;

    public WallpaperRotator(WallpaperEngine engine, AppSettings settings)
    {
        _engine = engine;
        _settings = settings;
        _timer.Elapsed += (_, _) => Tick();
        ApplyInterval();
    }

    public string LibraryPath =>
        string.IsNullOrWhiteSpace(_settings.LibraryPath)
            ? Path.Combine(AppContext.BaseDirectory, "WallpaperLibrary")
            : _settings.LibraryPath;

    /// <summary>Create the full folder tree (8 periods x static/dynamic) plus a hint file in each.</summary>
    public void EnsureLibraryExists()
    {
        string root = LibraryPath;
        Directory.CreateDirectory(root);
        foreach (var period in Periods)
        {
            var pdir = Path.Combine(root, period);
            Directory.CreateDirectory(Path.Combine(pdir, "静态壁纸"));
            Directory.CreateDirectory(Path.Combine(pdir, "动态壁纸"));
            string hint = Path.Combine(pdir, "把壁纸放进这里.txt");
            if (!File.Exists(hint))
                File.WriteAllText(hint,
                    $"把「{period}」时段的壁纸放进下面两个文件夹：\n" +
                    "  静态壁纸/ — 图片（jpg / png / webp / bmp / gif）\n" +
                    "  动态壁纸/ — 视频（mp4 / webm / mkv / mov / avi）\n" +
                    "软件会按当前时间自动选用本时段目录，并每隔一段时间随机换一张。");
        }
    }

    public void SetEnabled(bool on)
    {
        _settings.RotationEnabled = on;
        _settings.Save();
        if (on) { EnsureLibraryExists(); Start(); }
        else Stop();
    }

    public void ApplyInterval()
    {
        int mins = Math.Clamp(_settings.RotationIntervalMinutes, 5, 120);
        _timer.Interval = Math.Max(1000, mins * 60000);
    }

    public void Start()
    {
        if (!_settings.RotationEnabled) return;
        EnsureLibraryExists();
        _timer.Start();
        // Apply the current period immediately, but off the UI thread so the ~1.2s renderer
        // readiness wait never blocks the window from showing.
        System.Threading.ThreadPool.QueueUserWorkItem(_ => Tick());
    }

    public void Stop() => _timer.Stop();

    public void RotateNow() => Tick();

    public void Dispose() => _timer.Dispose();

    private void Tick()
    {
        lock (_gate)
        {
            string period = CurrentPeriod(DateTime.Now);
            string? file = PickFile(period);
            if (file is null)
            {
                StatusChanged?.Invoke($"时段「{period}」暂无壁纸，已跳过");
                return;
            }

            // No need to restart the renderer when the same file is already showing and still alive
            // (e.g. only one wallpaper in the period, or a long video) — avoids needless flicker and
            // process churn. Runtime sound changes still apply immediately via mpv's IPC pipe.
            if (file == _lastApplied && _engine.IsDynamicRunning)
            {
                StatusChanged?.Invoke($"时段「{period}」继续播放 {Path.GetFileName(file)}");
                return;
            }

            try
            {
                _engine.StopByPid(_settings.RendererPid);
                _engine.StartDynamic(file, audioEnabled: _settings.AudioEnabled, volume: _settings.Volume);
                _settings.LastMedia = file;
                _settings.RendererPid = _engine.RendererPid;
                _settings.Save();
                _lastApplied = file;
                StatusChanged?.Invoke($"时段「{period}」→ {Path.GetFileName(file)}");
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"轮换失败：{ex.Message}");
            }
        }
    }

    private string? PickFile(string period)
    {
        var files = GatherFiles(period);
        if (files.Count == 0) return null;

        if (!_bags.TryGetValue(period, out var bag) || bag.Count == 0 || _activePeriod != period)
        {
            bag = new Queue<string>(Shuffle(files));
            _bags[period] = bag;
            _activePeriod = period;
        }

        string pick = bag.Dequeue();
        if (bag.Count == 0)
            _bags[period] = new Queue<string>(Shuffle(files)); // refill so the next tick keeps going

        // Avoid repeating the last pick when there is an alternative.
        if (files.Count > 1 && pick == _lastApplied && bag.Count > 0)
        {
            var alt = bag.Dequeue();
            bag.Enqueue(pick);
            pick = alt;
        }
        return pick;
    }

    private List<string> GatherFiles(string period)
    {
        var result = new List<string>();
        var periodDir = Path.Combine(LibraryPath, period);
        foreach (var sub in new[] { "静态壁纸", "动态壁纸" })
        {
            var dir = Path.Combine(periodDir, sub);
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                var ext = Path.GetExtension(f);
                if (ImageExt.Contains(ext) || VideoExt.Contains(ext))
                    result.Add(f);
            }
        }
        return result;
    }

    private static string CurrentPeriod(DateTime t)
    {
        int h = t.Hour;
        if (h >= 5 && h < 8) return "清晨";
        if (h >= 8 && h < 11) return "早上";
        if (h >= 11 && h < 13) return "中午";
        if (h >= 13 && h < 17) return "下午";
        if (h >= 17 && h < 19) return "傍晚";
        if (h >= 19 && h < 21) return "黄昏";
        if (h >= 21 && h < 23) return "晚上";
        return "深夜"; // 23, 0-4
    }

    private static IEnumerable<string> Shuffle(IReadOnlyList<string> items)
    {
        var arr = items.ToArray();
        for (int i = arr.Length - 1; i > 0; i--)
        {
            int j = Random.Shared.Next(i + 1);
            (arr[i], arr[j]) = (arr[j], arr[i]);
        }
        return arr;
    }
}
