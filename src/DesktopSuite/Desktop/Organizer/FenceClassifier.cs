using System;
using System.Collections.Generic;
using System.IO;

namespace DesktopSuite.Desktop.Organizer;

/// <summary>
/// Pure classification logic. No state, no shell, no UI — given items + categories + overrides it
/// decides which category each item belongs to. Safe to call from any thread.
///
/// Precedence (highest first):
///   1. Manual override  (<see cref="FenceLayout.Overrides"/>, keyed by item path).
///   2. Auto rules, collected from every <see cref="FenceCategory"/> with <see cref="FenceCategory.AutoClassify"/>
///      true, evaluated by descending <see cref="ClassificationRule.Priority"/>; first match wins.
///   3. Built-in "未分类" fallback.
/// </summary>
public sealed class FenceClassifier
{
    /// <summary>Classification for a single item. Returns the target category id, or null if even the
    /// fallback was missing (should not happen once <see cref="FenceStore"/> guarantees the fallback).</summary>
    public static string? Classify(DesktopIconItem item, List<FenceCategory> categories, Dictionary<string, string?> overrides)
    {
        // 1) Manual override wins unconditionally.
        if (overrides.TryGetValue(item.Path, out var ov) && !string.IsNullOrEmpty(ov))
            return ov;

        // 2) Collect auto rules, sort by descending priority (stable).
        var candidates = new List<(ClassificationRule rule, string categoryId)>();
        foreach (var cat in categories)
        {
            if (!cat.AutoClassify) continue;
            foreach (var rule in cat.Rules)
                candidates.Add((rule, cat.Id));
        }
        candidates.Sort((a, b) => b.rule.Priority.CompareTo(a.rule.Priority));

        foreach (var (rule, categoryId) in candidates)
        {
            if (Matches(item, rule))
                return categoryId;
        }

        // 3) Fallback to the built-in "未分类" box.
        foreach (var cat in categories)
        {
            if (cat.Id == FenceConstants.UncategorizedId)
                return cat.Id;
        }
        // Last resort: any category whose display name is the fallback label.
        foreach (var cat in categories)
        {
            if (string.Equals(cat.DisplayName, "未分类", StringComparison.Ordinal))
                return cat.Id;
        }
        return null;
    }

    private static bool Matches(DesktopIconItem item, ClassificationRule rule)
    {
        switch (rule.Dimension)
        {
            case RuleDimension.Extension:
            {
                string ext = Path.GetExtension(item.Path);
                if (ext.Length == 0) return false;
                foreach (var tok in rule.Pattern.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    var t = tok.Trim();
                    if (t.Length == 0) continue;
                    string full = t.StartsWith(".", StringComparison.Ordinal) ? t : "." + t;
                    if (ext.Equals(full, rule.Comparison))
                        return true;
                }
                return false;
            }

            case RuleDimension.NameKeyword:
            {
                // A '|'-separated list of substrings; match any of them.
                foreach (var tok in rule.Pattern.Split('|', StringSplitOptions.RemoveEmptyEntries))
                {
                    var t = tok.Trim();
                    if (t.Length != 0 && item.Name.IndexOf(t, rule.Comparison) >= 0)
                        return true;
                }
                return false;
            }

            case RuleDimension.SourcePath:
                return item.Source != null && item.Source.IndexOf(rule.Pattern, rule.Comparison) >= 0;

            case RuleDimension.Kind:
                return item.Kind.ToString().Equals(rule.Pattern, rule.Comparison);

            default:
                return false;
        }
    }

    /// <summary>
    /// Classify every item and refill each category's <see cref="FenceCategory.MemberPaths"/> from
    /// scratch (old members are cleared first). Also stamps <see cref="DesktopIconItem.CategoryId"/>
    /// and <see cref="DesktopIconItem.OverrideCategoryId"/> for callers that render from items.
    /// </summary>
    public static void Apply(List<DesktopIconItem> items, List<FenceCategory> categories, Dictionary<string, string?> overrides)
    {
        foreach (var cat in categories)
            cat.MemberPaths.Clear();

        foreach (var item in items)
        {
            string? catId = Classify(item, categories, overrides);
            item.CategoryId = catId;
            item.OverrideCategoryId = overrides.TryGetValue(item.Path, out var ov) ? ov : null;

            if (catId == null) continue;
            var cat = categories.Find(c => c.Id == catId);
            cat?.MemberPaths.Add(item.Path);
        }
    }
}
