using System.Globalization;

namespace RepoWatch.Core.Layout;

/// <summary>A rectangle in physical (virtual-screen) pixels.</summary>
public readonly record struct PixelBox(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;

    public PixelBox Intersect(PixelBox other)
    {
        var x = Math.Max(X, other.X);
        var y = Math.Max(Y, other.Y);
        var right = Math.Min(Right, other.Right);
        var bottom = Math.Min(Bottom, other.Bottom);
        return right > x && bottom > y ? new PixelBox(x, y, right - x, bottom - y) : default;
    }
}

public readonly record struct ScreenInfo(PixelBox Bounds, PixelBox WorkingArea, double Scaling, bool IsPrimary);

/// <summary>Window placement rules that do not depend on a UI framework.</summary>
public static class PlacementPolicy
{
    /// <summary>Pixels of the window's top strip that must be on a working area for the window to count as reachable.</summary>
    public const int MinimumVisibleWidth = 64;
    public const int MinimumVisibleHeight = 24;
    public const int EdgeMargin = 24;

    /// <summary>
    /// Identifies a monitor layout (bounds and scaling of every screen, order-independent), so a
    /// placement saved on a laptop panel is not applied when docked to a different set of displays.
    /// </summary>
    public static string DisplayKey(IEnumerable<ScreenInfo> screens)
    {
        ArgumentNullException.ThrowIfNull(screens);
        return string.Join('|', screens
            .Select(s => string.Create(CultureInfo.InvariantCulture,
                $"{s.Bounds.Width}x{s.Bounds.Height}{s.Bounds.X:+0;-0}{s.Bounds.Y:+0;-0}@{s.Scaling:0.##}"))
            .Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Returns <paramref name="window"/> unchanged if its top strip is on some screen's working area
    /// (so it can be dragged); otherwise moves it, shrunk to fit if needed, to the primary working area.
    /// </summary>
    public static PixelBox EnsureReachable(PixelBox window, IReadOnlyList<ScreenInfo> screens)
    {
        ArgumentNullException.ThrowIfNull(screens);
        if (screens.Count == 0)
        {
            return window;
        }

        var topStrip = window with { Height = Math.Min(window.Height, MinimumVisibleHeight * 2) };
        var reachable = screens.Any(s =>
        {
            var overlap = topStrip.Intersect(s.WorkingArea);
            return overlap.Width >= Math.Min(MinimumVisibleWidth, window.Width) && overlap.Height >= Math.Min(MinimumVisibleHeight, topStrip.Height);
        });

        return reachable ? window : DefaultPosition(window.Width, window.Height, Primary(screens));
    }

    /// <summary>Top-right corner of the working area, inset by a margin; the size is clamped to fit.</summary>
    public static PixelBox DefaultPosition(int width, int height, ScreenInfo screen)
    {
        var area = screen.WorkingArea;
        var margin = (int)Math.Round(EdgeMargin * screen.Scaling);
        var w = Math.Min(width, Math.Max(1, area.Width - (2 * margin)));
        var h = Math.Min(height, Math.Max(1, area.Height - (2 * margin)));
        return new PixelBox(area.Right - margin - w, area.Y + margin, w, h);
    }

    public static ScreenInfo Primary(IReadOnlyList<ScreenInfo> screens)
    {
        ArgumentNullException.ThrowIfNull(screens);
        return screens.FirstOrDefault(s => s.IsPrimary) is { WorkingArea.Width: > 0 } primary ? primary : screens[0];
    }
}
