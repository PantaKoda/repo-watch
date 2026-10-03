using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using RepoWatch.Core.Identity;
using RepoWatch.Core.Settings;

namespace RepoWatch.Desktop.Storage;

/// <summary>
/// Stores each settings document as versioned JSON in the <c>settings</c> table.
/// Unreadable documents are copied to <c>settings_backup</c> before anything can replace them;
/// documents from a newer schema are never overwritten.
/// </summary>
public sealed class SqliteSettingsStore : ISettingsStore
{
    private const string AppScope = "app";

    private readonly LocalDatabase _database;
    private readonly ILogger<SqliteSettingsStore> _logger;
    private readonly TimeProvider _time;
    private readonly HashSet<string> _protectedScopes = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public SqliteSettingsStore(LocalDatabase database, ILogger<SqliteSettingsStore> logger, TimeProvider? time = null)
    {
        _database = database;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public SettingsLoadResult<AppSettings> LoadAppSettings() => Load(AppScope, SettingsCodecs.App);

    public bool SaveAppSettings(AppSettings settings) => Save(AppScope, SettingsCodecs.App, settings);

    public SettingsLoadResult<AccountSettings> LoadAccountSettings(AccountKey account) =>
        Load(AccountScope(account), SettingsCodecs.Account);

    public bool SaveAccountSettings(AccountKey account, AccountSettings settings) =>
        Save(AccountScope(account), SettingsCodecs.Account, settings);

    private static string AccountScope(AccountKey account)
    {
        ArgumentNullException.ThrowIfNull(account);
        return "account:" + account.StorageKey;
    }

    private SettingsLoadResult<T> Load<T>(string scope, SettingsCodec<T> codec) where T : class
    {
        lock (_gate)
        {
            using var connection = _database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT json FROM settings WHERE scope = $scope;";
            command.Parameters.AddWithValue("$scope", scope);
            var json = command.ExecuteScalar() as string;

            var result = codec.Deserialize(json);
            switch (result.Status)
            {
                case SettingsLoadStatus.Corrupt:
                    Backup(connection, scope, json!, result.Problem ?? "unreadable");
                    _logger.LogWarning("Settings '{Scope}' were unreadable and have been backed up; using defaults. {Problem}", scope, result.Problem);
                    break;
                case SettingsLoadStatus.NewerVersion:
                    _protectedScopes.Add(scope);
                    _logger.LogWarning("Settings '{Scope}' come from a newer version; using defaults without saving. {Problem}", scope, result.Problem);
                    break;
                case SettingsLoadStatus.Migrated:
                    _logger.LogInformation("Settings '{Scope}' migrated from schema {From} to {To}", scope, result.StoredVersion, codec.CurrentVersion);
                    break;
            }

            return result;
        }
    }

    private bool Save<T>(string scope, SettingsCodec<T> codec, T settings) where T : class
    {
        lock (_gate)
        {
            using var connection = _database.Open();
            if (_protectedScopes.Contains(scope) || StoredVersion(connection, scope) > codec.CurrentVersion)
            {
                _protectedScopes.Add(scope);
                _logger.LogWarning("Not saving settings '{Scope}': the stored document is from a newer version", scope);
                return false;
            }

            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO settings (scope, schema_version, json, updated_at)
                VALUES ($scope, $version, $json, $now)
                ON CONFLICT(scope) DO UPDATE SET
                    schema_version = excluded.schema_version,
                    json = excluded.json,
                    updated_at = excluded.updated_at;
                """;
            command.Parameters.AddWithValue("$scope", scope);
            command.Parameters.AddWithValue("$version", codec.CurrentVersion);
            command.Parameters.AddWithValue("$json", codec.Serialize(settings));
            command.Parameters.AddWithValue("$now", Now());
            command.ExecuteNonQuery();
            return true;
        }
    }

    private static int StoredVersion(SqliteConnection connection, string scope)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT schema_version FROM settings WHERE scope = $scope;";
        command.Parameters.AddWithValue("$scope", scope);
        return command.ExecuteScalar() is long version ? (int)version : 0;
    }

    private void Backup(SqliteConnection connection, string scope, string json, string reason)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO settings_backup (scope, json, reason, backed_up_at) VALUES ($scope, $json, $reason, $now);
            DELETE FROM settings WHERE scope = $scope;
            """;
        command.Parameters.AddWithValue("$scope", scope);
        command.Parameters.AddWithValue("$json", json);
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$now", Now());
        command.ExecuteNonQuery();
    }

    private string Now() => _time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
}
