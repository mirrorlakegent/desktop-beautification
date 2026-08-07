using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace DesktopSuite.Wallpaper;

/// <summary>
/// Sets static wallpapers per monitor using Win32 SPI and the IDesktopWallpaper COM API.
/// Does not modify system files or the registry directly beyond the documented wallpaper APIs.
/// </summary>
public static class StaticWallpaper
{
    // CLSID_DesktopWallpaper (shell32). Activated via CLSID because there is no managed coclass.
    private static readonly Guid CLSID_DesktopWallpaper = new("C2CF3110-460E-4fc1-B9D0-8A1C0C9CC4BD");

    /// <summary>
    /// Create an IDesktopWallpaper instance, failing loudly instead of dereferencing a null Type.
    /// </summary>
    private static NativeMethods.IDesktopWallpaper CreateDesktopWallpaper()
    {
        Type? comType = Type.GetTypeFromCLSID(CLSID_DesktopWallpaper);
        if (comType is null)
            throw new PlatformNotSupportedException("IDesktopWallpaper (CLSID_DesktopWallpaper) is not registered on this system.");

        object? instance = Activator.CreateInstance(comType);
        if (instance is not NativeMethods.IDesktopWallpaper dw)
            throw new PlatformNotSupportedException("Failed to activate IDesktopWallpaper.");

        return dw;
    }

    /// <summary>
    /// Set the same image on all monitors (legacy SPI path; Windows replicates to all displays).
    /// </summary>
    public static void SetWallpaperAllMonitors(string imagePath)
    {
        if (!File.Exists(imagePath))
            throw new FileNotFoundException("Wallpaper image not found.", imagePath);

        // SPI_SETDESKWALLPAPER uses the path directly; SPIF_SENDCHANGE notifies Explorer.
        bool ok = NativeMethods.SystemParametersInfo(
            NativeMethods.SPI_SETDESKWALLPAPER,
            0,
            Path.GetFullPath(imagePath),
            NativeMethods.SPIF_UPDATEINIFILE | NativeMethods.SPIF_SENDCHANGE);

        if (!ok)
            throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error(), "SPI_SETDESKWALLPAPER failed.");
    }

    /// <summary>
    /// Returns the device path of each active monitor, in wallpaper-API order.
    /// </summary>
    public static IReadOnlyList<string> GetMonitorDevicePaths()
    {
        var list = new List<string>();
        try
        {
            var dw = CreateDesktopWallpaper();
            try
            {
                dw.GetMonitorDevicePathCount(out uint count);
                for (uint i = 0; i < count; i++)
                {
                    dw.GetMonitorDevicePathAt(i, out string path);
                    list.Add(path);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(dw);
            }
        }
        catch (COMException)
        {
            // IDesktopWallpaper is unavailable on very old Windows builds; fall back to empty list.
        }
        catch (PlatformNotSupportedException)
        {
            // Same fallback: callers degrade to the legacy SPI path.
        }
        return list;
    }

    /// <summary>
    /// Set a specific image on a specific monitor. Requires Windows 8+ IDesktopWallpaper.
    /// </summary>
    public static void SetWallpaperPerMonitor(string monitorDevicePath, string imagePath)
    {
        if (!File.Exists(imagePath))
            throw new FileNotFoundException("Wallpaper image not found.", imagePath);

        var dw = CreateDesktopWallpaper();
        try
        {
            int hr = dw.SetWallpaper(monitorDevicePath, Path.GetFullPath(imagePath));
            if (hr < 0)
                Marshal.ThrowExceptionForHR(hr);
        }
        finally
        {
            Marshal.ReleaseComObject(dw);
        }
    }

    /// <summary>
    /// Set the image on every detected monitor.
    /// </summary>
    public static void SetWallpaperAllMonitorsPerMonitor(string imagePath)
    {
        var monitors = GetMonitorDevicePaths();
        if (monitors.Count == 0)
        {
            // COM API unavailable or no monitors; fall back to SPI.
            SetWallpaperAllMonitors(imagePath);
            return;
        }

        foreach (var monitor in monitors)
        {
            try
            {
                SetWallpaperPerMonitor(monitor, imagePath);
            }
            catch (COMException)
            {
                // Ignore per-monitor failures (e.g., disconnected display still enumerated).
            }
        }
    }
}
