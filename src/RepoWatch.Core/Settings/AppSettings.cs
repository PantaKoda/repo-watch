using System.Text.Json;
using System.Text.Json.Serialization;
using RepoWatch.Core.Identity;

namespace RepoWatch.Core.Settings;

/// <summary>
/// Base for persisted settings records. Properties this version does not know are kept in
/// <see cref="ExtensionData"/> and written back, so saving never erases a newer version's fields.
/// </summary>
public abstract record SettingsRecord
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Machine-wide user preferences, shared by all accounts. Contains no secrets.</summary>
public sealed record AppSettings : SettingsRecord
{
    public AppearanceSettings Appearance { get; init; } = new();

    public WindowSettings Window { get; init; } = new();

    public StartupSettings Startup { get; init; } = new();

    public NotificationSettings Notifications { get; init; } = new();

    /// <summary>The account the widget shows. Identity only; credentials live in the OS store.</summary>
    public AccountKey? ActiveAccount { get; init; }

    public bool MonitoringPaused { get; init; }

    /// <summary>Set when the first-run onboarding (sign in → grant access → choose → appearance) was finished.</summary>
    public bool OnboardingCompleted { get; init; }
}

public enum ThemePreference
{
    System,
    Light,
    Dark,
}

public enum WindowMaterial
{
    /// <summary>Frosted where supported and readable; otherwise solid.</summary>
    Auto,
    Solid,
    Transparent,
    Frosted,
    /// <summary>Windows 11 system backdrop; not a see-through effect.</summary>
    Mica,
}

public enum Density
{
    Comfortable,
    Compact,
}

/// <summary>Whether activity animations run. System follows the Windows "Show animations" setting.</summary>
public enum MotionPreference
{
    System,
    On,
    Off,
}

public sealed record AppearanceSettings : SettingsRecord
{
    public const double MinBackgroundOpacity = 0.2;
    public const double MaxBackgroundOpacity = 1.0;

    public ThemePreference Theme { get; init; } = ThemePreference.System;

    public WindowMaterial Material { get; init; } = WindowMaterial.Auto;

    /// <summary>Opacity of the background/material layer only; text and controls stay opaque.</summary>
    public double BackgroundOpacity { get; init; } = 0.85;

    public Density Density { get; init; } = Density.Comfortable;

    /// <summary>Optional accent as #RRGGBB; null uses the system accent.</summary>
    public string? AccentColor { get; init; }

    /// <summary>Activity animations (pulses, scan lines). Never run while nothing is active.</summary>
    public MotionPreference Motion { get; init; } = MotionPreference.System;
}

public sealed record WindowSettings : SettingsRecord
{
    public bool AlwaysOnTop { get; init; }

    public bool PositionLocked { get; init; }

    public bool Expanded { get; init; }

    /// <summary>Remembered placements, one per display configuration.</summary>
    public IReadOnlyList<WindowPlacement> Placements { get; init; } = [];
}

/// <summary>
/// Window placement for one display configuration. The position is in virtual-screen pixels, which
/// are stable for a given <see cref="DisplayKey"/>; the size is in device-independent pixels so it
/// survives scaling changes.
/// </summary>
public sealed record WindowPlacement : SettingsRecord
{
    /// <summary>Identifies the monitor layout (screen bounds and scaling); see <c>PlacementPolicy.DisplayKey</c>.</summary>
    public required string DisplayKey { get; init; }

    /// <summary>Left edge in virtual-screen pixels.</summary>
    public required double X { get; init; }

    /// <summary>Top edge in virtual-screen pixels.</summary>
    public required double Y { get; init; }

    /// <summary>Width in device-independent pixels.</summary>
    public required double Width { get; init; }

    /// <summary>Height in device-independent pixels.</summary>
    public required double Height { get; init; }
}

public sealed record StartupSettings : SettingsRecord
{
    /// <summary>Opt-in; off by default.</summary>
    public bool StartAtLogin { get; init; }

    public bool StartMinimized { get; init; }
}

public sealed record NotificationSettings : SettingsRecord
{
    public bool Enabled { get; init; } = true;

    public bool CiFailure { get; init; } = true;

    public bool CiRecovery { get; init; } = true;

    public bool ReviewRequested { get; init; } = true;

    public bool PullRequestMerged { get; init; } = true;

    /// <summary>Omit titles and other repository content from notification text.</summary>
    public bool HidePrivateDetails { get; init; }

    public QuietHours QuietHours { get; init; } = new();
}

public sealed record QuietHours : SettingsRecord
{
    public bool Enabled { get; init; }

    public TimeOnly Start { get; init; } = new(22, 0);

    public TimeOnly End { get; init; } = new(7, 0);

    /// <summary>True if <paramref name="localTime"/> falls in the window, which may span midnight.</summary>
    public bool Contains(TimeOnly localTime)
    {
        if (!Enabled || Start == End)
        {
            return false;
        }

        return Start < End
            ? localTime >= Start && localTime < End
            : localTime >= Start || localTime < End;
    }
}
