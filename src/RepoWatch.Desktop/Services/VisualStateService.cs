using RepoWatch.Core.Settings;
using RepoWatch.Desktop.Platform.Windowing;

namespace RepoWatch.Desktop.Services;

/// <summary>
/// What the widget actually got after applying appearance settings: the achieved material and
/// surface opacity, and whether motion is on. Settings show this instead of what was requested,
/// so the user sees when a material isn't available.
/// </summary>
public sealed class VisualStateService
{
    public AppliedMaterial? Material { get; private set; }

    public bool MotionEnabled { get; private set; } = true;

    /// <summary>Raised on the UI thread after visuals are re-applied.</summary>
    public event EventHandler? Changed;

    public static bool ResolveMotion(MotionPreference preference) => preference switch
    {
        MotionPreference.On => true,
        MotionPreference.Off => false,
        _ => SystemVisuals.AnimationsEnabled,
    };

    public void Publish(AppliedMaterial material, bool motionEnabled)
    {
        Material = material;
        MotionEnabled = motionEnabled;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>User-facing description of the achieved appearance.</summary>
    public string Describe()
    {
        if (Material is not { } applied)
        {
            return "";
        }

        var surface = applied.IsSeeThrough
            ? $"Showing {WindowMaterialService.Describe(applied.Achieved)} at {Math.Round(applied.SurfaceOpacity * 100)}% background opacity."
            : "Showing a solid background.";
        var motion = MotionEnabled ? "Activity animations are on." : "Activity animations are off.";
        return string.Join(" ", new[] { applied.Fallback, surface, motion }.Where(s => !string.IsNullOrEmpty(s)));
    }
}
