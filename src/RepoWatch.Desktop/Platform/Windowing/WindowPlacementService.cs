using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using RepoWatch.Core.Layout;
using RepoWatch.Core.Settings;
using RepoWatch.Desktop.Services;

namespace RepoWatch.Desktop.Platform.Windowing;

/// <summary>
/// Restores and remembers the widget's placement per display configuration and brings it back
/// onto a screen when displays change. Sizes are stored in device-independent pixels.
/// </summary>
public sealed class WindowPlacementService(SettingsService settings, ILogger<WindowPlacementService> logger)
{
    public const double DefaultWidth = 400;
    public const double DefaultHeight = 520;
    private const int MaxRememberedPlacements = 8;

    public void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        Restore(window);

        var saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        saveTimer.Tick += (_, _) =>
        {
            saveTimer.Stop();
            Save(window);
        };

        void ScheduleSave()
        {
            if (window.IsVisible && window.WindowState == WindowState.Normal)
            {
                saveTimer.Stop();
                saveTimer.Start();
            }
        }

        window.PositionChanged += (_, _) => ScheduleSave();
        window.SizeChanged += (_, _) => ScheduleSave();
        window.Closing += (_, _) => Save(window);
        window.Screens.Changed += (_, _) => Dispatcher.UIThread.Post(() => OnScreensChanged(window));
    }

    /// <summary>Moves the window back onto a working area if it is not reachable (e.g. after a monitor was removed).</summary>
    public void EnsureReachable(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var screens = Screens(window);
        var current = CurrentBox(window);
        var corrected = PlacementPolicy.EnsureReachable(current, screens);
        if (corrected != current)
        {
            logger.LogInformation("Widget was off-screen; moved to the primary display");
            Apply(window, corrected, screens);
        }
    }

    private void Restore(Window window)
    {
        var screens = Screens(window);
        if (screens.Count == 0)
        {
            return;
        }

        var key = PlacementPolicy.DisplayKey(screens);
        var placements = settings.App.Window.Placements;
        var exact = placements.FirstOrDefault(p => p.DisplayKey == key);
        var fallback = exact ?? placements.FirstOrDefault();

        var width = Math.Max(window.MinWidth, fallback?.Width ?? DefaultWidth);
        var height = Math.Max(window.MinHeight, fallback?.Height ?? DefaultHeight);
        var primary = PlacementPolicy.Primary(screens);

        PixelBox box;
        if (fallback is not null)
        {
            var scale = ScalingAt(screens, (int)fallback.X, (int)fallback.Y);
            box = new PixelBox((int)fallback.X, (int)fallback.Y, (int)Math.Round(width * scale), (int)Math.Round(height * scale));
        }
        else
        {
            box = PlacementPolicy.DefaultPosition((int)Math.Round(width * primary.Scaling), (int)Math.Round(height * primary.Scaling), primary);
        }

        Apply(window, PlacementPolicy.EnsureReachable(box, screens), screens);
    }

    private void OnScreensChanged(Window window)
    {
        var screens = Screens(window);
        var key = PlacementPolicy.DisplayKey(screens);
        var placement = settings.App.Window.Placements.FirstOrDefault(p => p.DisplayKey == key);
        if (placement is not null)
        {
            var scale = ScalingAt(screens, (int)placement.X, (int)placement.Y);
            var box = new PixelBox((int)placement.X, (int)placement.Y, (int)Math.Round(placement.Width * scale), (int)Math.Round(placement.Height * scale));
            Apply(window, PlacementPolicy.EnsureReachable(box, screens), screens);
        }
        else
        {
            EnsureReachable(window);
        }
    }

    private void Save(Window window)
    {
        if (window.WindowState != WindowState.Normal || window.ClientSize.Width <= 0)
        {
            return;
        }

        var screens = Screens(window);
        if (screens.Count == 0)
        {
            return;
        }

        var placement = new WindowPlacement
        {
            DisplayKey = PlacementPolicy.DisplayKey(screens),
            X = window.Position.X,
            Y = window.Position.Y,
            Width = Math.Round(window.ClientSize.Width),
            Height = Math.Round(window.ClientSize.Height),
        };

        settings.UpdateApp(s => s with
        {
            Window = s.Window with
            {
                Placements = s.Window.Placements
                    .Where(p => p.DisplayKey != placement.DisplayKey)
                    .Prepend(placement)
                    .Take(MaxRememberedPlacements)
                    .ToList(),
            },
        });
    }

    private static void Apply(Window window, PixelBox box, IReadOnlyList<ScreenInfo> screens)
    {
        var scale = ScalingAt(screens, box.X, box.Y);
        window.Width = Math.Max(window.MinWidth, box.Width / scale);
        window.Height = Math.Max(window.MinHeight, box.Height / scale);
        window.Position = new PixelPoint(box.X, box.Y);
    }

    private static PixelBox CurrentBox(Window window)
    {
        var scale = window.RenderScaling;
        return new PixelBox(window.Position.X, window.Position.Y,
            (int)Math.Round(window.ClientSize.Width * scale), (int)Math.Round(window.ClientSize.Height * scale));
    }

    private static double ScalingAt(IReadOnlyList<ScreenInfo> screens, int x, int y)
    {
        var point = new PixelBox(x, y, 1, 1);
        var screen = screens.FirstOrDefault(s => s.Bounds.Intersect(point).Width > 0);
        return screen.Scaling > 0 ? screen.Scaling : PlacementPolicy.Primary(screens).Scaling;
    }

    private static List<ScreenInfo> Screens(Window window) =>
        window.Screens.All
            .Select(s => new ScreenInfo(
                new PixelBox(s.Bounds.X, s.Bounds.Y, s.Bounds.Width, s.Bounds.Height),
                new PixelBox(s.WorkingArea.X, s.WorkingArea.Y, s.WorkingArea.Width, s.WorkingArea.Height),
                s.Scaling > 0 ? s.Scaling : 1,
                s.IsPrimary))
            .ToList();
}
