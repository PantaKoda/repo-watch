using RepoWatch.Desktop.Infrastructure;

namespace RepoWatch.Desktop.Tests.Infrastructure;

public sealed class ConfigurationLoaderTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("repowatch-config-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private AppPaths Paths => new(_root, Path.Combine(_root, "appsettings.json"), Path.Combine(_root, "repowatch.config.json"), Path.Combine(_root, "logs"));

    [Fact]
    public void Missing_files_fall_back_to_valid_defaults()
    {
        var result = ConfigurationLoader.Load(Paths, new Dictionary<string, string>());

        Assert.True(result.IsValid);
        Assert.Equal(20, result.Options.Polling.ActiveWorkflowSeconds);
    }

    [Fact]
    public void User_file_overrides_defaults_and_environment_overrides_user_file()
    {
        File.WriteAllText(Paths.DefaultsFile, """{ "Polling": { "QuietSeconds": 200 }, "GitHub": { "AppSlug": "shipped" } }""");
        File.WriteAllText(Paths.UserConfigFile, """{ "Polling": { "QuietSeconds": 300 } }""");
        var environment = new Dictionary<string, string>
        {
            ["REPOWATCH__GitHub__AppSlug"] = "from-env",
            ["UNRELATED_GitHub__AppSlug"] = "ignored",
            [AppPaths.DataDirectoryVariable] = @"C:\elsewhere", // not a configuration key
        };

        var result = ConfigurationLoader.Load(Paths, environment);

        Assert.True(result.IsValid);
        Assert.Equal(300, result.Options.Polling.QuietSeconds);
        Assert.Equal("from-env", result.Options.GitHub.AppSlug);
    }

    [Fact]
    public void The_retired_releases_page_setting_is_still_accepted()
    {
        File.WriteAllText(Paths.UserConfigFile, """{ "Updates": { "ReleasesUrl": "https://github.com/PantaKoda/repo-watch/releases" } }""");

        var result = ConfigurationLoader.Load(Paths, new Dictionary<string, string>());

        Assert.True(result.IsValid); // a 0.1.0 user file doesn't stop 0.2.0
        Assert.Equal("PantaKoda/repo-watch", result.Options.Updates.Repository);
    }

    [Fact]
    public void Malformed_json_is_reported_with_the_file_to_fix()
    {
        File.WriteAllText(Paths.UserConfigFile, "{ \"Polling\": ");

        var result = ConfigurationLoader.Load(Paths, new Dictionary<string, string>());

        var error = Assert.Single(result.Errors);
        Assert.Contains(Paths.UserConfigFile, error.Fix, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "Polling": { "QuietSeconds": "soon" } }""")]
    [InlineData("""{ "GitHub": { "ClientID_typo": "Iv23liabcdef" } }""")]
    public void Wrongly_typed_or_unknown_settings_are_errors(string json)
    {
        File.WriteAllText(Paths.UserConfigFile, json);

        var result = ConfigurationLoader.Load(Paths, new Dictionary<string, string>());

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validation_errors_surface_through_the_loader()
    {
        File.WriteAllText(Paths.UserConfigFile, """{ "GitHub": { "ApiBaseUrl": "http://api.github.com" } }""");

        var result = ConfigurationLoader.Load(Paths, new Dictionary<string, string>());

        Assert.Equal("GitHub:ApiBaseUrl", Assert.Single(result.Errors).Key);
    }
}
