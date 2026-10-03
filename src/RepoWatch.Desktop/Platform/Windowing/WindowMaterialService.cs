using Avalonia.Controls;
using Avalonia.Media;
using RepoWatch.Core.Settings;

namespace RepoWatch.Desktop.Platform.Windowing;

/// <summary>What the window actually got, which can differ from what was asked for.</summary>
public sealed record AppliedMaterial(WindowMaterial Requested, WindowTransparencyLevel Achieved, double SurfaceOpacity, string? Fallback)
{
    public bool IsSeeThrough => SurfaceOpacity < 1;
}

/// <summary>
/// Applies the widget's material through Avalonia's transparency API and reports the level the
/// platform achieved. Only the background layer becomes see-through: callers apply
/// <see cref="AppliedMaterial.SurfaceOpacity"/> to the surface, never to the whole window, so text
/// and controls stay fully opaque. Falls back to a solid surface when transparency is unavailable,
/// high contrast is on, or the session is remote. The opacity floor keeps every pixel inside the
/// widget hit-testable, so transparency never turns into click-through.
/// </summary>
public static class WindowMaterialService
{
    public static AppliedMaterial Apply(Window window, AppearanceSettings appearance)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(appearance);

        var (effective, _) = Effective(appearance.Material, SystemVisuals.HighContrast, SystemVisuals.RemoteSession);

        // Rounded corners need a transparent window even when the surface itself is solid.
        IReadOnlyList<WindowTransparencyLevel> hint = effective switch
        {
            WindowMaterial.Frosted or WindowMaterial.Auto => [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur, WindowTransparencyLevel.Transparent],
            WindowMaterial.Mica => [WindowTransparencyLevel.Mica, WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Transparent],
            _ => [WindowTransparencyLevel.Transparent],
        };

        // Re-applying an equal hint still makes the platform rebuild the backdrop (flicker while
        // dragging the opacity slider), so only assign it when the material actually changes.
        if (!window.TransparencyLevelHint.SequenceEqual(hint))
        {
            window.TransparencyLevelHint = hint;
        }

        var applied = Resolve(appearance, window.ActualTransparencyLevel, SystemVisuals.HighContrast, SystemVisuals.RemoteSession);

        // Without any transparency the window's own background shows through the margin and
        // rounded corners (black on Windows), so paint it with the surface to stay truly solid.
        window.Background = WindowBackground(applied,
            window.TryFindResource("WidgetSurfaceBrush", window.ActualThemeVariant, out var surface) ? surface as IBrush : null);
        return applied;
    }

    /// <summary>The window's own background: transparent unless the platform granted no transparency.</summary>
    public static IBrush WindowBackground(AppliedMaterial applied, IBrush? solidSurface)
    {
        ArgumentNullException.ThrowIfNull(applied);
        return applied.Achieved == WindowTransparencyLevel.None && solidSurface is not null ? solidSurface : Brushes.Transparent;
    }
    /// <summary>Decides the surface opacity and any fallback message from what the platform achieved.</summary>
    public static AppliedMaterial Resolve(AppearanceSettings appearance, WindowTransparencyLevel achieved, bool highContrast, bool remoteSession)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        var (material, fallback) = Effective(appearance.Material, highContrast, remoteSession);
        var requestedOpacity = Math.Clamp(appearance.BackgroundOpacity, AppearanceSettings.MinBackgroundOpacity, 1);
        double opacity;
        if (material == WindowMaterial.Solid)
        {
            opacity = 1;
        }
        else if (achieved == WindowTransparencyLevel.None)
        {
            opacity = 1;
            fallback ??= "Windows didn't allow transparency for this window, so the widget is solid.";
        }
        else if (material == WindowMaterial.Auto)
        {
            // Auto keeps the surface readable: frosted when blur is available, otherwise nearly solid.
            opacity = achieved == WindowTransparencyLevel.AcrylicBlur || achieved == WindowTransparencyLevel.Blur ? Math.Max(requestedOpacity, 0.7) : 0.94;
        }
        else
        {
            if ((material == WindowMaterial.Frosted && achieved != WindowTransparencyLevel.AcrylicBlur && achieved != WindowTransparencyLevel.Blur)
                || (material == WindowMaterial.Mica && achieved != WindowTransparencyLevel.Mica))
            {
                fallback ??= $"{material} isn't available here; using {Describe(achieved)} instead.";
            }

            opacity = requestedOpacity;
        }

        return new AppliedMaterial(appearance.Material, achieved, opacity, fallback);
    }

    private static (WindowMaterial Material, string? Fallback) Effective(WindowMaterial requested, bool highContrast, bool remoteSession) =>
        highContrast ? (WindowMaterial.Solid, "High contrast is on, so the widget is solid.")
        : remoteSession && requested is WindowMaterial.Frosted or WindowMaterial.Mica or WindowMaterial.Auto
            ? (WindowMaterial.Solid, "Blur effects are off in remote sessions, so the widget is solid.")
            : (requested, null);
    public static string Describe(WindowTransparencyLevel level) =>
        level == WindowTransparencyLevel.AcrylicBlur ? "frosted glass (acrylic)"
        : level == WindowTransparencyLevel.Mica ? "Mica"
        : level == WindowTransparencyLevel.Blur ? "blur"
        : level == WindowTransparencyLevel.Transparent ? "plain transparency"
        : "solid";
}
