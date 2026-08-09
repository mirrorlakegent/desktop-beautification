using System;
using System.Collections.Generic;
using System.IO;
using DesktopSuite.Wallpaper;

namespace DesktopSuite.Desktop.Organizer;

/// <summary>
/// Enumerates the top-level entries of the user's desktop directory and turns each into a
/// <see cref="DesktopIconItem"/>. Only the desktop root is scanned (IncludeSubdirectories=false) —
/// sub-folders on the desktop are treated as their own items, not recursed into.
///
/// Phase 1+2 does NOT extract thumbnails (the box shows the name + a type glyph). Icon extraction,
/// if added later, would use <c>System.Drawing.Icon.ExtractAssociatedIcon(path)</c> converted to a
/// <c>System.Windows.Media.Imaging.BitmapImage</c> (watch the WPF/WinForms type clash — use fully
/// qualified names or a local <c>using</c>).
/// </summary>
public static class DesktopItemEnumerator
{
    public static List<DesktopIconItem> Enumerate()
    {
        var items = new List<DesktopIconItem>();
        try
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrEmpty(desktop) || !Directory.Exists(desktop))
                return items;

            // Top-level only: files and directories directly on the desktop.
            string[] entries = Directory.GetFileSystemEntries(desktop);
            foreach (var p in entries)
            {
                try
                {
                    string name = Path.GetFileName(p);
                    bool isDirectory = (File.GetAttributes(p) & FileAttributes.Directory) != 0;

                    IconKind kind = isDirectory
                        ? IconKind.Folder
                        : (name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
                            ? IconKind.Shortcut
                            : IconKind.File);

                    DateTime lastModified;
                    try { lastModified = File.GetLastWriteTime(p); }
                    catch { lastModified = DateTime.MinValue; }

                    items.Add(new DesktopIconItem
                    {
                        Name = name,
                        Path = p,
                        Kind = kind,
                        Source = null,   // heuristic source not implemented in Phase 1+2
                        LastModified = lastModified
                    });
                }
                catch (Exception ex)
                {
                    // A single unreadable entry must not abort the whole enumeration.
                    HostLog.Write($"DesktopItemEnumerator: 跳过无法读取的项 {p}", ex);
                }
            }
        }
        catch (Exception ex)
        {
            HostLog.Write("DesktopItemEnumerator.Enumerate 失败", ex);
        }
        return items;
    }
}
