namespace DesktopSuite.Desktop.Organizer;

/// <summary>Well-known constants shared across the Fences modules.</summary>
public static class FenceConstants
{
    /// <summary>
    /// Fixed id of the built-in "未分类" (uncategorized) fallback category. It is deliberately a
    /// constant (not a random GUID) so the classifier and store can always find the ultimate
    /// fallback box, and so persistence is stable.
    /// </summary>
    public const string UncategorizedId = "00000000-0000-0000-0000-000000000001";
}
