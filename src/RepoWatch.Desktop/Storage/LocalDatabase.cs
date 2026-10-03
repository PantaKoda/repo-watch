using Microsoft.Data.Sqlite;

namespace RepoWatch.Desktop.Storage;

/// <summary>
/// The local SQLite database. Holds settings now and cached snapshots in later stages.
/// Never stores tokens or other secrets.
/// <para>
/// Schema migrations are ordered SQL scripts; <c>PRAGMA user_version</c> records how many have
/// been applied. Each runs in its own transaction. A database from a newer app version is not
/// modified.
/// </para>
/// </summary>
public sealed class LocalDatabase
{
    private static readonly string[] Migrations =
    [
        // 1: settings documents, keyed by scope ("app" or "account:<host>/<userId>").
        """
        CREATE TABLE settings (
            scope          TEXT    NOT NULL PRIMARY KEY,
            schema_version INTEGER NOT NULL,
            json           TEXT    NOT NULL,
            updated_at     TEXT    NOT NULL
        );
        CREATE TABLE settings_backup (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            scope       TEXT    NOT NULL,
            json        TEXT    NOT NULL,
            reason      TEXT    NOT NULL,
            backed_up_at TEXT   NOT NULL
        );
        """,
    ];

    private readonly string _connectionString;

    public LocalDatabase(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = System.IO.Path.GetFullPath(path);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
        }.ToString();
    }

    public string Path { get; }

    public static int LatestSchemaVersion => Migrations.Length;

    /// <summary>
    /// Creates the file if needed and applies pending migrations. Safe when several processes
    /// initialize the same new database at once: each step takes the write lock (BEGIN IMMEDIATE)
    /// and re-reads the version inside it.
    /// </summary>
    /// <exception cref="DatabaseVersionException">The database was created by a newer version of the app.</exception>
    public void Initialize()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        using var connection = Open();

        while (true)
        {
            using var transaction = connection.BeginTransaction(deferred: false);
            var version = GetUserVersion(connection, transaction);
            if (version > Migrations.Length)
            {
                throw new DatabaseVersionException(Path, version, Migrations.Length);
            }

            if (version == Migrations.Length)
            {
                transaction.Commit();
                break;
            }

            Execute(connection, Migrations[version], transaction);
            Execute(connection, $"PRAGMA user_version = {version + 1};", transaction);
            transaction.Commit();
        }

        // Persistent header change: only after confirming this version owns the schema.
        Execute(connection, "PRAGMA journal_mode = WAL;");
    }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        Execute(connection, "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;");
        return connection;
    }

    public static int GetUserVersion(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void Execute(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}

public sealed class DatabaseVersionException(string path, int found, int supported)
    : Exception($"The database '{path}' uses schema version {found}, but this version of Repo Watch supports up to {supported}. " +
                "Update Repo Watch, or move the file aside to start with empty local data.")
{
    public int FoundVersion { get; } = found;

    public int SupportedVersion { get; } = supported;
}
