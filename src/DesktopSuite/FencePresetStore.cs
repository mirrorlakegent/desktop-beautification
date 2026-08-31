using System.Text.Json;
using System.IO;
using System.Collections.Generic;
using System.Linq;

namespace DesktopSuite;

/// <summary>
/// M4-B Route B: named appearance presets. Serializes a <see cref="FenceAppearance"/> to a JSON file
/// under <c>%LocalAppData%\DesktopSuite\appearance-presets\</c>. This is the seed for the Phase 3 theme
/// engine — presets reuse the same <see cref="FenceAppearance"/> shape, so a future theme is just a
/// preset with extra (WPF resource) bindings.
/// </summary>
public static class FencePresetStore
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DesktopSuite", "appearance-presets");

    /// <summary>Create the directory and seed built-in presets if missing. Safe to call repeatedly.</summary>
    public static void SeedDefaults()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            // 默认 — the original hardcoded look.
            Ensure("默认", new FenceAppearance());
            // 玻璃拟态 — frosted on, low body opacity, slightly larger radius.
            Ensure("玻璃拟态", new FenceAppearance
            {
                Frosted = true,
                BodyOpacity = 120,
                FrostOpacity = 60,
                HeaderOpacity = 150,
                CornerRadius = 16
            });
            // 极简线框 — transparent body, custom border only.
            Ensure("极简线框", new FenceAppearance
            {
                BodyOpacity = 0,
                BorderOpacity = 160,
                BorderColorR = 120,
                BorderColorG = 160,
                BorderColorB = 210,
                CornerRadius = 8
            });
        }
        catch { /* best-effort seeding — never break startup */ }
    }

    private static void Ensure(string name, FenceAppearance a)
    {
        string path = Path.Combine(Dir, Sanitize(name) + ".json");
        if (!File.Exists(path))
            File.WriteAllText(path, JsonSerializer.Serialize(a, JsonOptions));
    }

    public static List<string> List()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            return Directory.GetFiles(Dir, "*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(n => n != null)
                .Select(n => n!)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToList();
        }
        catch { return new List<string>(); }
    }

    public static FenceAppearance? Load(string name)
    {
        try
        {
            string p = Path.Combine(Dir, Sanitize(name) + ".json");
            if (!File.Exists(p)) return null;
            return JsonSerializer.Deserialize<FenceAppearance>(File.ReadAllText(p));
        }
        catch { return null; }
    }

    public static void Save(string name, FenceAppearance a)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            string p = Path.Combine(Dir, Sanitize(name) + ".json");
            File.WriteAllText(p, JsonSerializer.Serialize(a, JsonOptions));
        }
        catch { /* best-effort */ }
    }

    public static void Delete(string name)
    {
        try
        {
            string p = Path.Combine(Dir, Sanitize(name) + ".json");
            if (File.Exists(p)) File.Delete(p);
        }
        catch { /* best-effort */ }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string Sanitize(string name)
    {
        var cleaned = string.Concat((name ?? "preset").Where(c => !Path.GetInvalidFileNameChars().Contains(c))).Trim();
        if (cleaned.Length == 0) cleaned = "preset";
        return cleaned.Replace(' ', '_');
    }
}
