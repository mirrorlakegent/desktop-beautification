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

    public static FenceAppearance FromSettings(AppSettings s) => new()
    {
        CornerRadius = s.FenceCornerRadius,
        BodyOpacity = s.FenceBodyOpacity,
        HeaderOpacity = s.FenceHeaderOpacity,
        TitleFontSize = s.FenceTitleFontSize,
        TitleAlign = s.FenceTitleAlign,
        ShowGlyph = s.FenceShowGlyph,
        Frosted = s.FenceFrosted,
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
    };
}
