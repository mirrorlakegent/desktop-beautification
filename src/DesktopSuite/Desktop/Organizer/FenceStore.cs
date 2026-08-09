using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using DesktopSuite.Wallpaper;

namespace DesktopSuite.Desktop.Organizer;

/// <summary>
/// Loads/saves the Fences layout from <c>%LocalAppData%\DesktopSuite\fences.json</c>, using the same
/// atomic-write pattern as <see cref="AppSettings"/> (<c>File.Replace</c> temp -&gt; original). All
/// failures are swallowed so a corrupt or missing file never breaks the app — <see cref="Load"/>
/// simply returns a fresh <see cref="DefaultLayout"/>.
/// </summary>
public sealed class FenceStore
{
    private static readonly string FilePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "DesktopSuite", "fences.json");

    private static FenceStore? _current;
    public static FenceStore Current => _current ??= new FenceStore();

    /// <summary>Load the persisted layout, or a default one if absent/corrupt. Guarantees the
    /// "未分类" fallback box is present.</summary>
    public FenceLayout Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<FenceLayout>(json);
                if (loaded != null)
                {
                    EnsureBuiltInCategories(loaded);
                    return loaded;
                }
                // File existed but deserialized to null → treat as corrupt.
                BackupCorrupt(FilePath);
            }
        }
        catch (Exception ex)
        {
            HostLog.Write("FenceStore.Load 失败，回退到默认布局", ex);
            try { if (File.Exists(FilePath)) BackupCorrupt(FilePath); } catch { }
        }
        return DefaultLayout();
    }

    /// <summary>Atomically persist the layout. Failures are swallowed (best-effort).</summary>
    public void Save(FenceLayout layout)
    {
        try
        {
            layout.LastSaved = DateTime.Now;
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var json = JsonSerializer.Serialize(layout, new JsonSerializerOptions { WriteIndented = true });

            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, json);
            if (File.Exists(FilePath))
                File.Replace(temp, FilePath, null);
            else
                File.Move(temp, FilePath);
        }
        catch (Exception ex)
        {
            HostLog.Write("FenceStore.Save 失败", ex);
            try { if (File.Exists(FilePath + ".tmp")) File.Delete(FilePath + ".tmp"); } catch { }
        }
    }

    /// <summary>Make sure the built-in "未分类" fallback always exists (it is the classifier's last resort).</summary>
    public void EnsureBuiltInCategories(FenceLayout layout)
    {
        if (layout.Categories == null) layout.Categories = new List<FenceCategory>();
        if (!layout.Categories.Exists(c => c.Id == FenceConstants.UncategorizedId))
            layout.Categories.Add(MakeUncategorized());
    }

    /// <summary>
    /// A sensible first-run layout: the four preset boxes (工作 / 娱乐 / 工具 / 临时) plus the
    /// "未分类" fallback, placed in a 2-column grid inside the primary monitor's work area. Each
    /// preset ships with example auto-classification rules.
    /// </summary>
    public static FenceLayout DefaultLayout()
    {
        var layout = new FenceLayout { SchemaVersion = 1, FencesEnabled = false };

        // Primary monitor work area (excludes the taskbar). NativeMethods gives us the RECT directly.
        var wa = PrimaryWorkArea();
        double col0 = wa.Left + 24;
        double col1 = wa.Left + 24 + 260;
        double row0 = wa.Top + 24;
        double row1 = wa.Top + 24 + 300;
        double row2 = wa.Top + 24 + 600;
        const double boxW = 240;
        const double boxH = 280;

        layout.Categories.Add(MakeUncategorized());

        layout.Categories.Add(new FenceCategory
        {
            Id = Guid.NewGuid().ToString(),
            DisplayName = "工作",
            IconRef = "💼",
            X = col0, Y = row0, Width = boxW, Height = boxH,
            AutoClassify = true,
            Rules =
            {
                new ClassificationRule { Dimension = RuleDimension.Extension, Pattern = ".lnk,.exe,.docx,.xlsx,.pptx", Priority = 100, TargetCategoryId = "" },
                new ClassificationRule { Dimension = RuleDimension.NameKeyword, Pattern = "报告|方案|计划|合同", Priority = 95, TargetCategoryId = "" }
            }
        });

        layout.Categories.Add(new FenceCategory
        {
            Id = Guid.NewGuid().ToString(),
            DisplayName = "娱乐",
            IconRef = "🎮",
            X = col1, Y = row0, Width = boxW, Height = boxH,
            AutoClassify = true,
            Rules =
            {
                new ClassificationRule { Dimension = RuleDimension.NameKeyword, Pattern = "微信|QQ|Steam|游戏|网易云", Priority = 90, TargetCategoryId = "" }
            }
        });

        layout.Categories.Add(new FenceCategory
        {
            Id = Guid.NewGuid().ToString(),
            DisplayName = "工具",
            IconRef = "🛠️",
            X = col0, Y = row1, Width = boxW, Height = boxH,
            AutoClassify = true,
            Rules =
            {
                new ClassificationRule { Dimension = RuleDimension.Extension, Pattern = ".zip,.rar,.7z,.msi,.ps1,.iso", Priority = 80, TargetCategoryId = "" }
            }
        });

        layout.Categories.Add(new FenceCategory
        {
            Id = Guid.NewGuid().ToString(),
            DisplayName = "临时",
            IconRef = "📥",
            X = col1, Y = row1, Width = boxW, Height = boxH,
            AutoClassify = true,
            // SourcePath "Downloads" only matches once Source is populated (Phase 2 leaves Source null,
            // so this box stays empty until the heuristic is implemented — known limitation).
            Rules =
            {
                new ClassificationRule { Dimension = RuleDimension.SourcePath, Pattern = "Downloads", Priority = 70, TargetCategoryId = "" }
            }
        });

        // Repair the TargetCategoryId of every rule now that the ids are known.
        foreach (var cat in layout.Categories)
            foreach (var rule in cat.Rules)
                if (string.IsNullOrEmpty(rule.TargetCategoryId))
                    rule.TargetCategoryId = cat.Id;

        // Default box geometry for the fallback.
        var unc = layout.Categories.Find(c => c.Id == FenceConstants.UncategorizedId)!;
        unc.X = col0; unc.Y = row2; unc.Width = boxW; unc.Height = boxH;
        unc.AutoClassify = false; // it is the fallback, never an auto target

        return layout;
    }

    private static FenceCategory MakeUncategorized() => new()
    {
        Id = FenceConstants.UncategorizedId,
        DisplayName = "未分类",
        IconRef = "📦",
        X = 0, Y = 0, Width = 240, Height = 280,
        AutoClassify = false
    };

    /// <summary>Primary monitor work area via MONITORINFO (no WinForms dependency).</summary>
    private static NativeMethods.RECT PrimaryWorkArea()
    {
        try
        {
            IntPtr hMonitor = NativeMethods.MonitorFromWindow(
                NativeMethods.GetDesktopWindow(), NativeMethods.MONITOR_DEFAULTTOPRIMARY);
            var mi = new NativeMethods.MONITORINFO();
            if (NativeMethods.GetMonitorInfo(hMonitor, ref mi))
                return mi.rcWork;
        }
        catch (Exception ex)
        {
            HostLog.Write("FenceStore.PrimaryWorkArea 失败，回退到虚拟屏", ex);
        }
        // Fallback: the whole virtual screen.
        return new NativeMethods.RECT
        {
            Left = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN),
            Top = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN),
            Right = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN) +
                    NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN),
            Bottom = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN) +
                     NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN)
        };
    }

    private static void BackupCorrupt(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                string backup = path + ".corrupt." + DateTime.Now.ToString("yyyyMMddHHmmss");
                File.Move(path, backup);
                HostLog.Write($"fences.json 损坏 — 已备份到 {Path.GetFileName(backup)}。");
            }
        }
        catch { /* best-effort */ }
    }
}
