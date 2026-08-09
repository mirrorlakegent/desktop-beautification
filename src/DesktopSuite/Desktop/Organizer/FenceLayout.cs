using System;
using System.Collections.Generic;

namespace DesktopSuite.Desktop.Organizer;

/// <summary>
/// The complete persisted Fences layout. Serialized to
/// <c>%LocalAppData%\DesktopSuite\fences.json</c> by <see cref="FenceStore"/>.
/// </summary>
public sealed class FenceLayout
{
    /// <summary>Schema version. Bump when the on-disk shape changes so old files can be migrated.</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Whether the Fences layer is currently active (native icons hidden).</summary>
    public bool FencesEnabled { get; set; }

    /// <summary>All boxes, including the built-in "未分类" fallback.</summary>
    public List<FenceCategory> Categories { get; set; } = new();

    /// <summary>Per-item manual overrides: item path -&gt; category id (or empty/null to clear).</summary>
    public Dictionary<string, string?> Overrides { get; set; } = new();

    /// <summary>When the layout was last saved (diagnostics only).</summary>
    public DateTime LastSaved { get; set; }
}
