using System;
using System.IO;
using System.Windows;
using System.Windows.Media;

namespace DesktopSuite.Themes;

/// <summary>
/// Holds the currently active theme and notifies renderers when it changes.
/// Singleton-ish via App.xaml.cs registration.
/// </summary>
public class ThemeService
{
    private static ThemeService? _current;
    public static ThemeService Current => _current ??= new ThemeService();

    private ResolvedTheme _theme = new();

    public ResolvedTheme Theme
    {
        get => _theme;
        private set
        {
            _theme = value;
            ThemeChanged?.Invoke(this, value);
        }
    }

    public event EventHandler<ResolvedTheme>? ThemeChanged;

    public ResolvedTheme Load(string themeFilePath)
    {
        var loader = new ThemeLoader();
        Theme = loader.LoadFromFile(themeFilePath);
        ApplyToApplicationResources(Theme);
        return Theme;
    }

    /// <summary>
    /// Pushes theme colours into Application.Resources so the global Button style and any
    /// DynamicResource references update without touching every control by hand.
    /// </summary>
    public void ApplyToApplicationResources(ResolvedTheme theme)
    {
        if (Application.Current is null) return;

        void Put(string key, Brush brush)
        {
            // Replace immutable/frozen brushes with mutable ones so future theme changes
            // actually mutate the resource and WPF re-evaluates DynamicResource bindings.
            if (brush is SolidColorBrush scb && scb.CanFreeze && scb.IsFrozen)
                brush = new SolidColorBrush(scb.Color);

            if (Application.Current.Resources.Contains(key))
                Application.Current.Resources[key] = brush;
            else
                Application.Current.Resources.Add(key, brush);
        }

        Put("ButtonBackgroundBrush", theme.SurfaceRaised);
        Put("ButtonForegroundBrush", theme.TextPrimary);
        Put("ButtonBorderBrush", theme.SurfaceBorderStrong);
        Put("ButtonHoverBrush", theme.Primary);
        Put("ButtonPressedBrush", theme.SurfaceSunken);
        Put("ButtonFocusBrush", theme.Accent);
        Put("WindowBackgroundBrush", theme.SurfaceBase);
        Put("WindowForegroundBrush", theme.TextPrimary);
    }

    public ResolvedTheme LoadDefault()
    {
        string appDir = AppContext.BaseDirectory;
        string presetPath = Path.Combine(appDir, "Themes", "presets", "obsidian-glass.theme.json");
        if (File.Exists(presetPath))
            return Load(presetPath);

        // Fallback to project-relative path when running under dotnet run from repo root.
        presetPath = Path.Combine(appDir, "..", "..", "..", "Themes", "presets", "obsidian-glass.theme.json");
        if (File.Exists(presetPath))
            return Load(presetPath);

        throw new ThemeLoadException("Default theme not found. Expected Themes\\presets\\obsidian-glass.theme.json beside the EXE.");
    }
}
