using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using RepoWatch.Core.Identity;
using RepoWatch.Core.Settings;
using RepoWatch.Desktop.Storage;

namespace RepoWatch.Desktop.Tests.Storage;

public sealed class SqliteSettingsStoreTests : IDisposable
{
    private static readonly AccountKey Alice = new("github.com", 1001);
    private static readonly AccountKey Bob = new("github.com", 2002);

    private readonly string _root = Directory.CreateTempSubdirectory("repowatch-db-").FullName;

    private string DatabasePath => Path.Combine(_root, "repowatch.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }

    // A new database object and store per call simulates an application restart.
    private SqliteSettingsStore OpenStore()
    {
        var database = new LocalDatabase(DatabasePath);
        database.Initialize();
        return new SqliteSettingsStore(database, NullLogger<SqliteSettingsStore>.Instance);
    }

    private void ExecuteSql(string sql)
    {
        using var connection = new LocalDatabase(DatabasePath).Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    [Fact]
    public void Settings_survive_a_restart()
    {
        var app = new AppSettings
        {
            Appearance = new() { Material = WindowMaterial.Transparent, BackgroundOpacity = 0.5 },
            ActiveAccount = Alice,
        };
        var account = new AccountSettings
        {
            Ordering = RepositoryOrdering.Manual,
            Watchlist = [new() { RepositoryId = 9, Owner = "o", Name = "b" }, new() { RepositoryId = 3, Owner = "o", Name = "a" }],
        };

        var store = OpenStore();
        Assert.True(store.SaveAppSettings(app));
        Assert.True(store.SaveAccountSettings(Alice, account));

        var reopened = OpenStore();
        var loadedApp = reopened.LoadAppSettings();
        var loadedAccount = reopened.LoadAccountSettings(Alice);

        Assert.Equal(SettingsLoadStatus.Loaded, loadedApp.Status);
        Assert.Equal(WindowMaterial.Transparent, loadedApp.Value.Appearance.Material);
        Assert.Equal(Alice, loadedApp.Value.ActiveAccount);
        Assert.Equal([9L, 3L], loadedAccount.Value.Watchlist.Select(w => w.RepositoryId));
    }

    [Fact]
    public void Accounts_are_isolated_by_host_and_user_id()
    {
        var store = OpenStore();
        store.SaveAccountSettings(Alice, new AccountSettings { Watchlist = [new() { RepositoryId = 1, Owner = "a", Name = "a" }] });

        Assert.Equal(SettingsLoadStatus.Missing, store.LoadAccountSettings(Bob).Status);
        Assert.Equal(SettingsLoadStatus.Missing, store.LoadAccountSettings(new AccountKey("ghe.example.com", 1001)).Status);
        Assert.Single(store.LoadAccountSettings(Alice).Value.Watchlist);
    }

    [Fact]
    public void Corrupt_settings_are_backed_up_before_defaults_can_replace_them()
    {
        OpenStore();
        ExecuteSql("INSERT INTO settings (scope, schema_version, json, updated_at) VALUES ('app', 1, '{ broken', 'x');");

        var store = OpenStore();
        Assert.Equal(SettingsLoadStatus.Corrupt, store.LoadAppSettings().Status);
        Assert.True(store.SaveAppSettings(new AppSettings()));

        using var connection = new LocalDatabase(DatabasePath).Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT json FROM settings_backup WHERE scope = 'app';";
        Assert.Equal("{ broken", command.ExecuteScalar());
        Assert.Equal(SettingsLoadStatus.Loaded, store.LoadAppSettings().Status);
    }

    [Fact]
    public void Settings_from_a_newer_version_are_never_overwritten()
    {
        OpenStore();
        const string newer = """{ "schemaVersion": 7, "futureField": true }""";
        ExecuteSql($"INSERT INTO settings (scope, schema_version, json, updated_at) VALUES ('app', 7, '{newer}', 'x');");

        var store = OpenStore();
        Assert.Equal(SettingsLoadStatus.NewerVersion, store.LoadAppSettings().Status);
        Assert.False(store.SaveAppSettings(new AppSettings()));

        // Even a fresh store that skipped the load must not overwrite it.
        Assert.False(OpenStore().SaveAppSettings(new AppSettings()));

        using var connection = new LocalDatabase(DatabasePath).Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT json FROM settings WHERE scope = 'app';";
        Assert.Equal(newer, command.ExecuteScalar());
    }

    [Fact]
    public void Database_migrations_are_idempotent_and_newer_databases_are_rejected()
    {
        var database = new LocalDatabase(DatabasePath);
        database.Initialize();
        database.Initialize();

        using (var connection = database.Open())
        {
            Assert.Equal(LocalDatabase.LatestSchemaVersion, LocalDatabase.GetUserVersion(connection));
        }

        ExecuteSql($"PRAGMA user_version = {LocalDatabase.LatestSchemaVersion + 1};");

        var ex = Assert.Throws<DatabaseVersionException>(() => new LocalDatabase(DatabasePath).Initialize());
        Assert.Contains(DatabasePath, ex.Message, StringComparison.Ordinal);
    }
}
