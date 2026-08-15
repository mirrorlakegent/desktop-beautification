using System.Collections.Generic;

namespace DesktopSuite.Desktop.Organizer;

/// <summary>
/// One "fence" — a box on the desktop that collects a category of icons.
///
/// Geometry (<see cref="X"/>, <see cref="Y"/>, <see cref="Width"/>, <see cref="Height"/>) is in
/// virtual-screen physical pixels and may be negative (multi-monitor). A single layout spans all
/// monitors; Phase 2 only places boxes inside the primary monitor's work area, leaving multi-monitor
/// clamping to a later phase.
///
/// Members are stored as <see cref="MemberPaths"/> (paths, not objects) so the persisted JSON stays
/// stable across sessions even if icon objects are re-enumerated.
/// </summary>
public sealed class FenceCategory
{
    /// <summary>Stable id. The built-in "未分类" box uses the fixed <see cref="FenceConstants.UncategorizedId"/>;
    /// other categories get a fresh GUID at creation time.</summary>
    public string Id { get; set; } = "";

    /// <summary>Human label shown in the box header (工作 / 娱乐 / 工具 / 临时 / 未分类).</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>Optional glyph for the box header (an emoji string is fine for Phase 2).</summary>
    public string? IconRef { get; set; }

    /// <summary>Virtual-screen X (left) in physical pixels.</summary>
    public double X { get; set; }

    /// <summary>Virtual-screen Y (top) in physical pixels.</summary>
    public double Y { get; set; }

    /// <summary>Box width in LOGICAL pixels (96-DPI basis). <c>FenceLayer.BuildBoxes</c> multiplies
    /// this by the DPI scale to get physical pixels, so boxes grow with DPI (DPI-aware).</summary>
    public double Width { get; set; }

    /// <summary>Box height in LOGICAL pixels (96-DPI basis). Same DPI treatment as <see cref="Width"/>.</summary>
    public double Height { get; set; }

    /// <summary>When true the box body is collapsed (only the header shows).</summary>
    public bool Collapsed { get; set; }

    /// <summary>When true, the classifier assigns items to this category using <see cref="Rules"/>.</summary>
    public bool AutoClassify { get; set; }

    /// <summary>Auto-classification rules, evaluated by <see cref="FenceClassifier"/>.</summary>
    public List<ClassificationRule> Rules { get; set; } = new();

    /// <summary>Paths of member items, recomputed by <see cref="FenceClassifier.Apply"/>.</summary>
    public List<string> MemberPaths { get; set; } = new();
}
