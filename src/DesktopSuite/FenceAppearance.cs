using DesktopSuite.Wallpaper;

namespace DesktopSuite;

/// <summary>
/// M4-B: user-tunable appearance of fence boxes. A small DTO that maps to/from <see cref="AppSettings"/>
/// so the persisted values stay in one place. Instances are passed to <see cref="Desktop.Organizer.FenceLayer"/>
/// via <c>SetAppearance</c>; <see cref="Clone"/> keeps an immutable snapshot for cache invalidation.
/// </summary>
public sealed class FenceAppearance
{
    /// <summary>Box corner radius in logical (96-DPI) pixels.</summary>
    public int CornerRadius { get; set; } = 10;
    /// <summary>Box body background alpha (0-255). Lower = more wallpaper shows through.</summary>
    public int BodyOpacity { get; set; } = 180;
    /// <summary>Box header background alpha (0-255).</summary>
    public int HeaderOpacity { get; set; } = 200;
    /// <summary>Box title font size in logical pixels.</summary>
    public float TitleFontSize { get; set; } = 13;
    /// <summary>Title horizontal alignment: 0 = Left (near), 1 = Center.</summary>
    public int TitleAlign { get; set; } = 0;
    /// <summary>Whether to draw the category's emoji glyph before the title.</summary>
    public bool ShowGlyph { get; set; } = true;
    /// <summary>Frosted-glass (毛玻璃) mode: blur the wallpaper behind each box.</summary>
    public bool Frosted { get; set; } = false;
    /// <summary>Frosted glass tint opacity (0-255). Lower = more transparent/see-through.</summary>
    public int FrostOpacity { get; set; } = 50;

    // ---- M4-B Route B: appearance deepening ----
    /// <summary>Box drop shadow (additive, drawn behind the box). Off by default.</summary>
    public bool BoxShadowEnabled { get; set; } = false;
    /// <summary>Shadow offset in logical px (applied to both x and y, down-right).</summary>
    public int ShadowOffset { get; set; } = 10;
    /// <summary>Shadow blur radius in logical px.</summary>
    public int ShadowBlur { get; set; } = 16;
    /// <summary>Shadow opacity (0-255). Uses <see cref="BorderColorR/G/B"/> as the shadow tint.</summary>
    public int ShadowOpacity { get; set; } = 140;
    /// <summary>Custom border color R (0-255). Also reused as the box-shadow tint.</summary>
    public int BorderColorR { get; set; } = 64;
    /// <summary>Custom border color G (0-255).</summary>
    public int BorderColorG { get; set; } = 70;
    /// <summary>Custom border color B (0-255).</summary>
    public int BorderColorB { get; set; } = 86;
    /// <summary>Custom border opacity (0-255). 0 = auto (border alpha tracks body opacity, legacy behavior). ≥16 = use custom color.</summary>
    public int BorderOpacity { get; set; } = 0;
    /// <summary>Title font family (whitelist-enforced). Default "Segoe UI" to match legacy look.</summary>
    public string TitleFontFamily { get; set; } = "Segoe UI";

    /// <summary>Whitelist of title font families. v8 proved GDI+ emoji fallback is fragile; we keep to
    /// fonts known to fallback to Segoe UI Emoji cleanly under <c>TextRenderer</c>. Invalid families are
    /// rejected at load time (see <see cref="IsFontFamilyAllowed"/>) so rendering never throws.</summary>
    public static readonly HashSet<string> AllowedFontFamilies = new(StringComparer.OrdinalIgnoreCase)
    {
        "Segoe UI", "Microsoft YaHei UI", "Microsoft YaHei", "微软雅黑",
        "SimSun", "宋体", "Consolas", "Microsoft Sans Serif"
    };

    public static bool IsFontFamilyAllowed(string? family) =>
        family != null && AllowedFontFamilies.Contains(family);

    public static FenceAppearance FromSettings(AppSettings s) => new()
    {
        CornerRadius = s.FenceCornerRadius,
        BodyOpacity = s.FenceBodyOpacity,
        HeaderOpacity = s.FenceHeaderOpacity,
        TitleFontSize = s.FenceTitleFontSize,
        TitleAlign = s.FenceTitleAlign,
        ShowGlyph = s.FenceShowGlyph,
        Frosted = s.FenceFrosted,
        FrostOpacity = s.FenceFrostOpacity,
        BoxShadowEnabled = s.FenceBoxShadowEnabled,
        ShadowOffset = s.FenceShadowOffset,
        ShadowBlur = s.FenceShadowBlur,
        ShadowOpacity = s.FenceShadowOpacity,
        BorderColorR = s.FenceBorderColorR,
        BorderColorG = s.FenceBorderColorG,
        BorderColorB = s.FenceBorderColorB,
        BorderOpacity = s.FenceBorderOpacity,
        TitleFontFamily = s.FenceTitleFontFamily,
    };

    public void ApplyTo(AppSettings s)
    {
        s.FenceCornerRadius = CornerRadius;
        s.FenceBodyOpacity = BodyOpacity;
        s.FenceHeaderOpacity = HeaderOpacity;
        s.FenceTitleFontSize = TitleFontSize;
        s.FenceTitleAlign = TitleAlign;
        s.FenceShowGlyph = ShowGlyph;
        s.FenceFrosted = Frosted;
        s.FenceFrostOpacity = FrostOpacity;
        s.FenceBoxShadowEnabled = BoxShadowEnabled;
        s.FenceShadowOffset = ShadowOffset;
        s.FenceShadowBlur = ShadowBlur;
        s.FenceShadowOpacity = ShadowOpacity;
        s.FenceBorderColorR = BorderColorR;
        s.FenceBorderColorG = BorderColorG;
        s.FenceBorderColorB = BorderColorB;
        s.FenceBorderOpacity = BorderOpacity;
        s.FenceTitleFontFamily = TitleFontFamily;
    }

    public FenceAppearance Clone() => new()
    {
        CornerRadius = CornerRadius,
        BodyOpacity = BodyOpacity,
        HeaderOpacity = HeaderOpacity,
        TitleFontSize = TitleFontSize,
        TitleAlign = TitleAlign,
        ShowGlyph = ShowGlyph,
        Frosted = Frosted,
        FrostOpacity = FrostOpacity,
        BoxShadowEnabled = BoxShadowEnabled,
        ShadowOffset = ShadowOffset,
        ShadowBlur = ShadowBlur,
        ShadowOpacity = ShadowOpacity,
        BorderColorR = BorderColorR,
        BorderColorG = BorderColorG,
        BorderColorB = BorderColorB,
        BorderOpacity = BorderOpacity,
        TitleFontFamily = TitleFontFamily,
    };
}
