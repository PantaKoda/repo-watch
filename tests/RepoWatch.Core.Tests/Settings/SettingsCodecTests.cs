using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using RepoWatch.Core.Identity;
using RepoWatch.Core.Settings;

namespace RepoWatch.Core.Tests.Settings;

public sealed class SettingsCodecTests
{
    private static AppSettings PopulatedApp() => new()
    {
        Appearance = new()
        {
            Theme = ThemePreference.Dark,
            Material = WindowMaterial.Frosted,
            BackgroundOpacity = 0.4,
            Density = Density.Compact,
            AccentColor = "#3366FF",
        },
        Window = new()
        {
            AlwaysOnTop = true,
            PositionLocked = true,
            Expanded = true,
            Placements = [new() { DisplayKey = "2560x1440@1.5", X = 100.5, Y = 40, Width = 400, Height = 520 }],
        },
        Startup = new() { StartAtLogin = true, StartMinimized = true },
        Notifications = new()
        {
            CiRecovery = false,
            HidePrivateDetails = true,
            QuietHours = new() { Enabled = true, Start = new(21, 30), End = new(6, 45) },
        },
        ActiveAccount = new AccountKey("github.com", 1001),
        MonitoringPaused = true,
    };

    private static AccountSettings PopulatedAccount() => new()
    {
        LastKnownLogin = "octo",
        Ordering = RepositoryOrdering.Manual,
        Watchlist =
        [
            new() { RepositoryId = 2, Owner = "org", Name = "api", Branches = ["main", "release"], WorkflowIds = [7, 9], PullRequests = PullRequestScope.All },
            new() { RepositoryId = 1, Owner = "octo", Name = "dotfiles", ShowIssues = false, NotificationsEnabled = false },
        ],
    };

    [Fact]
    public void App_settings_round_trip_every_field()
    {
        var original = PopulatedApp();

        var json = SettingsCodecs.App.Serialize(original);
        var loaded = SettingsCodecs.App.Deserialize(json);

        Assert.Equal(SettingsLoadStatus.Loaded, loaded.Status);
        Assert.Equal(json, SettingsCodecs.App.Serialize(loaded.Value));
        Assert.Equal(original.ActiveAccount, loaded.Value.ActiveAccount);
        Assert.Equal(new TimeOnly(21, 30), loaded.Value.Notifications.QuietHours.Start);
        Assert.Equal(WindowMaterial.Frosted, loaded.Value.Appearance.Material);
    }

    [Fact]
    public void Account_settings_round_trip_and_preserve_manual_order()
    {
        var json = SettingsCodecs.Account.Serialize(PopulatedAccount());
        var loaded = SettingsCodecs.Account.Deserialize(json).Value;

        Assert.Equal([2L, 1L], loaded.Watchlist.Select(w => w.RepositoryId));
        Assert.Equal(["main", "release"], loaded.Watchlist[0].Branches);
        Assert.False(loaded.Watchlist[1].NotificationsEnabled);
        Assert.Equal(json, SettingsCodecs.Account.Serialize(loaded));
    }

    [Fact]
    public void Serialized_documents_carry_the_schema_version_and_readable_enums()
    {
        var json = JsonNode.Parse(SettingsCodecs.App.Serialize(PopulatedApp()))!;

        Assert.Equal(SettingsCodecs.App.CurrentVersion, json["schemaVersion"]!.GetValue<int>());
        Assert.Equal("Frosted", json["appearance"]!["material"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(null, SettingsLoadStatus.Missing)]
    [InlineData("", SettingsLoadStatus.Missing)]
    [InlineData("{ not json", SettingsLoadStatus.Corrupt)]
    [InlineData("[1, 2]", SettingsLoadStatus.Corrupt)]
    [InlineData("""{ "appearance": {} }""", SettingsLoadStatus.Corrupt)]
    [InlineData("""{ "schemaVersion": 1, "appearance": { "theme": "Sepia" } }""", SettingsLoadStatus.Corrupt)]
    [InlineData("""{ "schemaVersion": 99, "appearance": {} }""", SettingsLoadStatus.NewerVersion)]
    public void Unusable_documents_fall_back_to_defaults_with_a_reason(string? json, SettingsLoadStatus expected)
    {
        var result = SettingsCodecs.App.Deserialize(json);

        Assert.Equal(expected, result.Status);
        Assert.Equal(SettingsCodecs.App.Serialize(new AppSettings()), SettingsCodecs.App.Serialize(result.Value));
    }

    [Fact]
    public void Hand_edited_values_are_normalized()
    {
        const string json = """
            {
              "schemaVersion": 1,
              "appearance": { "backgroundOpacity": 0.01, "accentColor": "red", "theme": 42 },
              "window": { "placements": [ { "displayKey": "a", "x": 0, "y": 0, "width": -5, "height": 10 } ] }
            }
            """;

        var settings = SettingsCodecs.App.Deserialize(json).Value;

        Assert.Equal(AppearanceSettings.MinBackgroundOpacity, settings.Appearance.BackgroundOpacity);
        Assert.Null(settings.Appearance.AccentColor);
        Assert.Equal(ThemePreference.System, settings.Appearance.Theme);
        Assert.Empty(settings.Window.Placements);
    }

    [Fact]
    public void Duplicate_and_invalid_watchlist_entries_are_dropped_keeping_first_position()
    {
        const string json = """
            {
              "schemaVersion": 1,
              "watchlist": [
                { "repositoryId": 5, "owner": "o", "name": "first" },
                { "repositoryId": 0, "owner": "o", "name": "invalid" },
                { "repositoryId": 5, "owner": "o", "name": "duplicate" },
                { "repositoryId": 6, "owner": "o", "name": "second", "workflowIds": [3, 3, -1] }
              ]
            }
            """;

        var watchlist = SettingsCodecs.Account.Deserialize(json).Value.Watchlist;

        Assert.Equal(["first", "second"], watchlist.Select(w => w.Name));
        Assert.Equal([3L], watchlist[1].WorkflowIds);
    }

    [Fact]
    public void Older_documents_are_migrated_step_by_step()
    {
        var codec = new SettingsCodec<TestDocument>(3, TestJsonContext.Default.TestDocument, () => new TestDocument(), d => d,
        [
            new(1, doc => { doc["displayName"] = doc["name"]?.DeepClone(); doc.Remove("name"); return doc; }),
            new(2, doc => { doc["size"] = (doc["size"]?.GetValue<int>() ?? 0) * 2; return doc; }),
        ]);

        var result = codec.Deserialize("""{ "schemaVersion": 1, "name": "widget", "size": 4 }""");

        Assert.Equal(SettingsLoadStatus.Migrated, result.Status);
        Assert.Equal(1, result.StoredVersion);
        Assert.Equal("widget", result.Value.DisplayName);
        Assert.Equal(8, result.Value.Size);
    }

    [Fact]
    public void A_codec_without_a_contiguous_migration_chain_is_rejected()
    {
        Assert.Throws<ArgumentException>(() =>
            new SettingsCodec<TestDocument>(3, TestJsonContext.Default.TestDocument, () => new TestDocument(), d => d, [new(2, d => d)]));
    }

    [Theory]
    [InlineData(22, 0, true)]
    [InlineData(3, 0, true)]
    [InlineData(7, 0, false)]
    [InlineData(12, 0, false)]
    public void Quiet_hours_can_span_midnight(int hour, int minute, bool quiet)
    {
        var hours = new QuietHours { Enabled = true, Start = new(22, 0), End = new(7, 0) };

        Assert.Equal(quiet, hours.Contains(new TimeOnly(hour, minute)));
    }
}

public sealed record TestDocument
{
    public string? DisplayName { get; init; }

    public int Size { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(TestDocument))]
internal sealed partial class TestJsonContext : JsonSerializerContext;
