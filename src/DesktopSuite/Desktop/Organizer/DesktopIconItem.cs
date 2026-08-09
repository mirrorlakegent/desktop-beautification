using System;

namespace DesktopSuite.Desktop.Organizer;

/// <summary>Kind of a desktop item, used by the classifier's <see cref="RuleDimension.Kind"/> dimension.</summary>
public enum IconKind
{
    Shortcut,
    File,
    Folder
}

/// <summary>
/// A single desktop icon, captured as pure metadata. Fences NEVER moves the real file — this record
/// only describes what is on the desktop so it can be grouped into boxes. Double-clicking an item
/// launches the real file via <c>Process.Start(UseShellExecute=true, Path)</c>, so disabling Fences
/// leaves the native desktop completely untouched.
/// </summary>
public sealed class DesktopIconItem
{
    /// <summary>File name without path (e.g. "report.docx").</summary>
    public string Name { get; init; } = "";

    /// <summary>Full path; always lives under the DesktopDirectory.</summary>
    public string Path { get; init; } = "";

    /// <summary>Shortcut / File / Folder.</summary>
    public IconKind Kind { get; init; }

    /// <summary>
    /// Heuristic source, e.g. an item that points at the browser download folder. Phase 2 leaves this
    /// null (no heuristic yet); the SourcePath rule dimension simply never matches until it is populated.
    /// </summary>
    public string? Source { get; set; }

    /// <summary>Last-modified timestamp of the underlying file/folder.</summary>
    public DateTime LastModified { get; init; }

    /// <summary>Category this item currently belongs to (filled by <see cref="FenceClassifier"/>).</summary>
    public string? CategoryId { get; set; }

    /// <summary>
    /// Manual override set by the user. Highest priority: when non-null it wins over the auto classifier.
    /// Persisted into <see cref="FenceLayout.Overrides"/> keyed by <see cref="Path"/>.
    /// </summary>
    public string? OverrideCategoryId { get; set; }
}
