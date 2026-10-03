namespace RepoWatch.Desktop.Infrastructure;

/// <summary>Per-user locations for configuration overrides, logs and (later) the local database.</summary>
public sealed record AppPaths(string DataDirectory, string DefaultsFile, string UserConfigFile, string LogDirectory)
{
    public string DatabaseFile => Path.Combine(DataDirectory, "repowatch.db");

    public const string DataDirectoryVariable = "REPOWATCH_DATA_DIR";

    /// <summary>
    /// Uses %LOCALAPPDATA%\RepoWatch on Windows (the platform equivalent elsewhere).
    /// REPOWATCH_DATA_DIR overrides it for portable use and isolated testing.
    /// </summary>
    public static AppPaths Resolve()
    {
        var root = Environment.GetEnvironmentVariable(DataDirectoryVariable);
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RepoWatch");
        }

        root = Path.GetFullPath(root);
        return new AppPaths(
            DataDirectory: root,
            DefaultsFile: Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
            UserConfigFile: Path.Combine(root, "repowatch.config.json"),
            LogDirectory: Path.Combine(root, "logs"));
    }
}
