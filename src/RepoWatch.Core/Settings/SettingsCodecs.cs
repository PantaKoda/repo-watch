using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace RepoWatch.Core.Settings;

/// <summary>Codecs for the persisted settings documents. Bump a version only together with a migration.</summary>
public static partial class SettingsCodecs
{
    public static SettingsCodec<AppSettings> App { get; } = new(
        currentVersion: 1,
        SettingsJsonContext.Default.AppSettings,
        () => new AppSettings(),
        Normalize,
        migrations: []);

    public static SettingsCodec<AccountSettings> Account { get; } = new(
        currentVersion: 1,
        SettingsJsonContext.Default.AccountSettings,
        () => new AccountSettings(),
        Normalize,
        migrations: []);

    /// <summary>Repairs values a hand-edited or older document could contain, so the UI never sees them.</summary>
    public static AppSettings Normalize(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var appearance = settings.Appearance ?? new AppearanceSettings();
        var opacity = double.IsFinite(appearance.BackgroundOpacity)
            ? Math.Clamp(appearance.BackgroundOpacity, AppearanceSettings.MinBackgroundOpacity, AppearanceSettings.MaxBackgroundOpacity)
            : new AppearanceSettings().BackgroundOpacity;

        var window = settings.Window ?? new WindowSettings();
        var notifications = settings.Notifications ?? new NotificationSettings();

        return settings with
        {
            Appearance = appearance with
            {
                Theme = Defined(appearance.Theme),
                Material = Defined(appearance.Material),
                Density = Defined(appearance.Density),
                Motion = Defined(appearance.Motion),
                BackgroundOpacity = opacity,
                AccentColor = appearance.AccentColor is { } accent && AccentPattern().IsMatch(accent) ? accent : null,
            },
            Window = window with
            {
                Placements = (window.Placements ?? [])
                    .Where(p => p is not null && !string.IsNullOrWhiteSpace(p.DisplayKey)
                        && double.IsFinite(p.X) && double.IsFinite(p.Y)
                        && double.IsFinite(p.Width) && double.IsFinite(p.Height) && p.Width > 0 && p.Height > 0)
                    .DistinctBy(p => p.DisplayKey)
                    .ToList(),
            },
            Startup = settings.Startup ?? new StartupSettings(),
            Notifications = notifications with { QuietHours = notifications.QuietHours ?? new QuietHours() },
        };
    }

    public static AccountSettings Normalize(AccountSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings with
        {
            Ordering = Defined(settings.Ordering),
            Watchlist = (settings.Watchlist ?? [])
                .Where(w => w is not null && w.RepositoryId > 0)
                .DistinctBy(w => w.RepositoryId)
                .Select(w => w with
                {
                    Owner = w.Owner ?? "",
                    Name = w.Name ?? "",
                    PullRequests = Defined(w.PullRequests),
                    Branches = (w.Branches ?? []).Where(b => !string.IsNullOrWhiteSpace(b)).Distinct(StringComparer.Ordinal).ToList(),
                    WorkflowIds = (w.WorkflowIds ?? []).Where(id => id > 0).Distinct().ToList(),
                })
                .ToList(),
        };
    }

    private static TEnum Defined<TEnum>(TEnum value) where TEnum : struct, Enum =>
        Enum.IsDefined(value) ? value : default;

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex AccentPattern();
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(AccountSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
