using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;

namespace DesktopSuite.Themes;

/// <summary>
/// Loads a theme.json file and resolves all token references into WPF brushes.
/// Phase 0 focuses on the token palette; module layers are stored raw for later phases.
/// </summary>
public class ThemeLoader
{
    private readonly Dictionary<string, JsonNode?> _flatTokens = new();
    private readonly HashSet<string> _resolving = new();

    public ResolvedTheme LoadFromFile(string path)
    {
        if (!File.Exists(path))
            throw new ThemeLoadException($"Theme file not found: {path}");

        string json = File.ReadAllText(path);
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json, new JsonNodeOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            throw new ThemeLoadException("Invalid JSON in theme file.", ex);
        }

        if (root is null)
            throw new ThemeLoadException("Theme file is empty.");

        return LoadFromNode(root);
    }

    public ResolvedTheme LoadFromNode(JsonNode root)
    {
        string? formatVersion = root["formatVersion"]?.GetValue<string>();
        if (formatVersion != "1.0")
            throw new ThemeLoadException($"Unsupported formatVersion: {formatVersion ?? "(missing)"}. Expected 1.0.");

        string? id = root["id"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(id))
            throw new ThemeLoadException("Theme 'id' is required.");

        JsonNode? meta = root["meta"] ?? throw new ThemeLoadException("Theme 'meta' is required.");
        string? name = meta["name"]?.GetValue<string>() ?? id;
        string? colorMode = meta["colorMode"]?.GetValue<string>() ?? "dark";

        JsonNode? tokens = root["tokens"] ?? throw new ThemeLoadException("Theme 'tokens' is required.");
        JsonNode? palette = tokens["palette"] ?? throw new ThemeLoadException("Theme 'tokens.palette' is required.");

        _flatTokens.Clear();
        Flatten(palette, "palette");

        var theme = new ResolvedTheme
        {
            Id = id,
            Name = name,
            ColorMode = colorMode,
            WallpaperConfig = root["wallpaper"],
            DockConfig = root["dock"],
            WidgetConfig = root["widgets"],
            DesktopConfig = root["desktop"],
            ConsistencyConfig = root["consistency"]
        };

        // Resolve palette brushes
        theme.SurfaceBase = ResolveBrush("palette.surface.base");
        theme.SurfaceRaised = ResolveBrush("palette.surface.raised");
        theme.SurfaceSunken = ResolveBrush("palette.surface.sunken");
        theme.SurfaceBorder = ResolveBrush("palette.surface.border");
        theme.SurfaceBorderStrong = ResolveBrush("palette.surface.borderStrong");

        theme.Primary = ResolveBrush("palette.primary.500");
        theme.Secondary = ResolveBrush("palette.secondary.500");
        theme.Accent = ResolveBrush("palette.accent.500");
        theme.Neutral = ResolveBrush("palette.neutral.500");

        theme.TextPrimary = ResolveBrush("palette.text.primary");
        theme.TextSecondary = ResolveBrush("palette.text.secondary");
        theme.TextTertiary = ResolveBrush("palette.text.tertiary");
        theme.TextInverse = ResolveBrush("palette.text.inverse");
        theme.TextOnAccent = ResolveBrush("palette.text.onAccent");

        theme.Success = ResolveBrush("palette.semantic.success");
        theme.Warning = ResolveBrush("palette.semantic.warning");
        theme.Danger = ResolveBrush("palette.semantic.danger");
        theme.Info = ResolveBrush("palette.semantic.info");

        // Radius
        JsonNode? radius = tokens["radius"];
        if (radius is not null)
        {
            theme.RadiusSmall = GetNumber(radius["sm"], 8);
            theme.RadiusMedium = GetNumber(radius["md"], 14);
            theme.RadiusLarge = GetNumber(radius["lg"], 22);
            theme.RadiusPill = GetNumber(radius["pill"], 9999);
        }

        return theme;
    }

    private void Flatten(JsonNode node, string path)
    {
        _flatTokens[path] = node;

        if (node is JsonObject obj)
        {
            foreach (var prop in obj)
            {
                string childPath = $"{path}.{prop.Key}";
                if (prop.Value is not null)
                    Flatten(prop.Value, childPath);
            }
        }
    }

    private Brush ResolveBrush(string path)
    {
        if (!_flatTokens.TryGetValue(path, out JsonNode? node) || node is null)
            return Brushes.Magenta; // obvious missing-token indicator

        var rgb = ResolveColorNode(node, path);
        return ColorEngine.ToBrush(rgb);
    }

    private RgbByte ResolveColorNode(JsonNode node, string path)
    {
        if (_resolving.Contains(path))
            throw new ThemeLoadException($"Circular color reference detected at '{path}'.");

        _resolving.Add(path);
        try
        {
            return ResolveColorNodeInternal(node, path);
        }
        finally
        {
            _resolving.Remove(path);
        }
    }

    private RgbByte ResolveColorNodeInternal(JsonNode node, string path)
    {
        // 1. Hex string
        if (node is JsonValue value && value.TryGetValue(out string? hex))
        {
            return ColorEngine.HexToRgb(hex!);
        }

        // 2. OKLCh object
        if (node is JsonObject obj)
        {
            if (obj.ContainsKey("$ref"))
            {
                string? refPath = obj["$ref"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(refPath))
                    throw new ThemeLoadException($"Empty $ref at '{path}'.");

                if (!_flatTokens.TryGetValue(refPath, out JsonNode? target) || target is null)
                    throw new ThemeLoadException($"$ref target not found: '{refPath}' (from '{path}').");

                var rgb = ResolveColorNode(target, refPath);
                double alpha = obj.ContainsKey("alpha") ? GetNumber(obj["alpha"], 1.0) : rgb.A / 255.0;
                if (obj.ContainsKey("adjust"))
                {
                    var oklch = ColorEngine.RgbToOklch(rgb);
                    var adjusted = ApplyAdjust(oklch, obj["adjust"]);
                    adjusted = adjusted with { Alpha = alpha };
                    return ColorEngine.OklchToRgb(adjusted);
                }
                return new RgbByte(rgb.R, rgb.G, rgb.B, (byte)Math.Round(alpha * 255));
            }

            if (obj.ContainsKey("$derive"))
            {
                // Phase 0: cannot sample wallpaper at runtime. Use fallback or accent.
                string? derive = obj["$derive"]?.GetValue<string>();
                JsonNode? fallbackNode = obj["fallback"];
                if (fallbackNode is not null)
                    return ResolveColorNode(fallbackNode, path + ".fallback");

                if (derive == "system.accent")
                {
                    var sys = SystemParameters.WindowGlassColor;
                    return new RgbByte(sys.R, sys.G, sys.B, sys.A);
                }

                // Default neutral gray fallback so UI doesn't crash.
                return new RgbByte(128, 128, 128);
            }

            if (obj.ContainsKey("$contrast"))
            {
                return ResolveContrast(obj["$contrast"]!, path);
            }

            if (obj.ContainsKey("l") && obj.ContainsKey("c") && obj.ContainsKey("h"))
            {
                double l = GetNumber(obj["l"], 0);
                double c = GetNumber(obj["c"], 0);
                double h = GetNumber(obj["h"], 0);
                double a = GetNumber(obj["alpha"], 1.0);
                return ColorEngine.OklchToRgb(new Oklch(l, c, h, a));
            }
        }

        throw new ThemeLoadException($"Unrecognized color value at '{path}': {node.ToJsonString()}");
    }

    private RgbByte ResolveContrast(JsonNode contrastNode, string path)
    {
        if (contrastNode is not JsonObject contrast)
            return new RgbByte(255, 255, 255);

        string? against = contrast["against"]?.GetValue<string>() ?? "auto:widget";
        double target = GetNumber(contrast["target"], 75);
        string? seedHex = contrast["seed"]?.GetValue<string>();
        string? prefer = contrast["prefer"]?.GetValue<string>() ?? "auto";

        // Determine seed color.
        Oklch seed;
        if (!string.IsNullOrEmpty(seedHex))
        {
            seed = ColorEngine.RgbToOklch(ColorEngine.HexToRgb(seedHex));
        }
        else
        {
            seed = new Oklch(0.5, 0, 0); // neutral seed
        }

        // Determine background lightness.
        double bgL;
        if (against.StartsWith("auto:", StringComparison.OrdinalIgnoreCase))
        {
            // Phase 0: no runtime wallpaper sampling. Approximate from theme surface base.
            var surface = ColorEngine.RgbToOklch(ResolveBrushToRgb("palette.surface.base"));
            bgL = surface.L;
        }
        else
        {
            var bgRgb = ResolveBrushToRgb(against);
            bgL = ColorEngine.RgbToOklch(bgRgb).L;
        }

        // Simplified contrast solver: push lightness away from background.
        // APCA-ish: target Lc ~ target. Lc is roughly |Lfg - Lbg| * 100 for normal cases.
        double desiredDelta = target / 100.0;
        bool goDark = bgL > 0.55 || (prefer == "dark");
        if (prefer == "light") goDark = false;
        if (prefer == "auto" && bgL <= 0.45) goDark = false;

        double targetL = goDark
            ? Math.Max(0, bgL - desiredDelta)
            : Math.Min(1, bgL + desiredDelta);

        // Preserve seed hue/chroma but cap chroma loss for readability.
        var result = seed with { L = Math.Clamp(targetL, 0.05, 0.95) };
        return ColorEngine.OklchToRgb(result);
    }

    private RgbByte ResolveBrushToRgb(string path)
    {
        if (!_flatTokens.TryGetValue(path, out JsonNode? node) || node is null)
            return new RgbByte(128, 128, 128);
        return ResolveColorNode(node, path);
    }

    private static Oklch ApplyAdjust(Oklch color, JsonNode? adjustNode)
    {
        if (adjustNode is not JsonObject adjust)
            return color;

        double l = color.L + GetNumber(adjust["l"], 0);
        double c = color.C + GetNumber(adjust["c"], 0);
        double h = color.H + GetNumber(adjust["h"], 0);
        return new Oklch(Math.Clamp(l, 0, 1), Math.Clamp(c, 0, 0.4), h % 360, color.Alpha);
    }

    private static double GetNumber(JsonNode? node, double defaultValue)
    {
        if (node is null) return defaultValue;
        if (node is JsonValue v && v.TryGetValue(out double d)) return d;
        if (node is JsonValue vi && vi.TryGetValue(out int i)) return i;
        return defaultValue;
    }
}
