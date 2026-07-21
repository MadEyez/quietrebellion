/*
 * BmapIcons.cs – Programmatic tray icons (no .ico files needed).
 *
 * Generates 16x16 filled-circle icons at runtime using System.Drawing.
 * Color encodes connection + battery state:
 *   Green   – connected, battery > 20 %
 *   Yellow  – connected, battery 10–20 %
 *   Red     – connected, battery < 10 %  (also triggers low-battery toast)
 *   Blue    – searching / connecting
 *   Gray    – disconnected
 */

using System.Drawing;
using System.Runtime.InteropServices;

namespace BoseCtl;

public static class BmapIcons
{
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint hIcon);

    // Icon.FromHandle wraps an existing GDI handle without owning it.
    // Clone() creates a new managed Icon with its own copy so we can safely
    // call DestroyIcon on the original HICON from GetHicon().
    private static Icon Make(Color fill)
    {
        using var bmp = new Bitmap(16, 16);
        using var g   = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        using var br  = new SolidBrush(fill);
        g.FillEllipse(br, 0, 0, 15, 15);
        var hIcon = bmp.GetHicon();
        var icon  = (Icon)Icon.FromHandle(hIcon).Clone();
        DestroyIcon(hIcon);
        return icon;
    }

    /// <summary>Battery-aware connected icon (green/yellow/red).</summary>
    public static Icon Connected(int batteryPct) => batteryPct switch
    {
        < 10  => Make(Color.FromArgb(239,  68,  68)),  // red
        < 20  => Make(Color.FromArgb(234, 179,   8)),  // yellow
        _     => Make(Color.FromArgb( 34, 197,  94)),  // green
    };

    /// <summary>Blue spinning-dot analogue: "searching / connecting".</summary>
    public static Icon Searching() => Make(Color.FromArgb(96, 165, 250));

    /// <summary>Gray circle: device not found / disconnected.</summary>
    public static Icon Disconnected() => Make(Color.FromArgb(156, 163, 175));
}

