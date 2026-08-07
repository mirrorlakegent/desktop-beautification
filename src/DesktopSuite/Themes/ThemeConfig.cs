using System.Windows.Media;

namespace DesktopSuite.Themes;

/// <summary>
/// Runtime-resolved theme consumable by WPF renderers.
/// All color references ($ref, $derive, $contrast) have been evaluated to concrete brushes.
/// </summary>
public class ResolvedTheme
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ColorMode { get; set; } = "dark";

    // Palette
    public Brush SurfaceBase { get; set; } = Brushes.Transparent;
    public Brush SurfaceRaised { get; set; } = Brushes.Transparent;
    public Brush SurfaceSunken { get; set; } = Brushes.Transparent;
    public Brush SurfaceBorder { get; set; } = Brushes.Transparent;
    public Brush SurfaceBorderStrong { get; set; } = Brushes.Transparent;

    public Brush Primary { get; set; } = Brushes.DodgerBlue;
    public Brush Secondary { get; set; } = Brushes.Gray;
    public Brush Accent { get; set; } = Brushes.Teal;
    public Brush Neutral { get; set; } = Brushes.Gray;

    public Brush TextPrimary { get; set; } = Brushes.White;
    public Brush TextSecondary { get; set; } = Brushes.LightGray;
    public Brush TextTertiary { get; set; } = Brushes.Gray;
    public Brush TextInverse { get; set; } = Brushes.Black;
    public Brush TextOnAccent { get; set; } = Brushes.White;

    public Brush Success { get; set; } = Brushes.Green;
    public Brush Warning { get; set; } = Brushes.Orange;
    public Brush Danger { get; set; } = Brushes.Red;
    public Brush Info { get; set; } = Brushes.Blue;

    // Geometry / motion
    public double RadiusSmall { get; set; } = 8;
    public double RadiusMedium { get; set; } = 14;
    public double RadiusLarge { get; set; } = 22;
    public double RadiusPill { get; set; } = 9999;

    // Module configuration (kept as raw nodes for later phases)
    public object? WallpaperConfig { get; set; }
    public object? DockConfig { get; set; }
    public object? WidgetConfig { get; set; }
    public object? DesktopConfig { get; set; }
    public object? ConsistencyConfig { get; set; }
}

public class ThemeLoadException : System.Exception
{
    public ThemeLoadException(string message) : base(message) { }
    public ThemeLoadException(string message, System.Exception inner) : base(message, inner) { }
}
