using System;

namespace DesktopSuite.Desktop.Organizer;

/// <summary>Which property of a <see cref="DesktopIconItem"/> a <see cref="ClassificationRule"/> tests.</summary>
public enum RuleDimension
{
    /// <summary>Compare the file extension against a comma-separated list (e.g. ".lnk,.exe,.docx").</summary>
    Extension,

    /// <summary>Substring (or '|'-separated list of substrings) match against the item name.</summary>
    NameKeyword,

    /// <summary>Substring match against <see cref="DesktopIconItem.Source"/> (e.g. "Downloads").</summary>
    SourcePath,

    /// <summary>Exact kind match: "Folder" / "File" / "Shortcut".</summary>
    Kind
}

/// <summary>
/// A single classification rule belonging to a <see cref="FenceCategory"/>. Rules from all
/// auto-classifying categories are collected and evaluated by priority (highest first); the first
/// hit wins.
/// </summary>
public sealed class ClassificationRule
{
    /// <summary>Which dimension to test.</summary>
    public RuleDimension Dimension { get; set; }

    /// <summary>
    /// Pattern syntax depends on <see cref="Dimension"/>:
    ///  - Extension : ".lnk,.exe,.docx,.xlsx,.pptx" (leading dot optional)
    ///  - NameKeyword : "微信|QQ|Steam|游戏" (matches any '|'-separated token as a substring)
    ///  - SourcePath : "Downloads"
    ///  - Kind : "Folder" / "File" / "Shortcut"
    /// </summary>
    public string Pattern { get; set; } = "";

    /// <summary>Comparison flavour for string matches; defaults to case-insensitive.</summary>
    public StringComparison Comparison { get; set; } = StringComparison.OrdinalIgnoreCase;

    /// <summary>Higher priority rules are evaluated first. Ties are broken by rule order.</summary>
    public int Priority { get; set; }

    /// <summary>The category id this rule assigns a matching item to.</summary>
    public string TargetCategoryId { get; set; } = "";
}
