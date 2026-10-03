using Microsoft.Data.Sqlite;

namespace RepoWatch.Desktop.Storage;

/// <summary>
/// The local SQLite database: settings, and per-account caches of repository snapshots and ETags.
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

        // 2: per-account caches of private repository content. Cleared on sign-out; never holds secrets.
        """
        CREATE TABLE repository_snapshots (
            account       TEXT    NOT NULL,
            repository_id INTEGER NOT NULL,
            format        INTEGER NOT NULL,
            json          TEXT    NOT NULL,
            saved_at      TEXT    NOT NULL,
            PRIMARY KEY (account, repository_id)
        );
        CREATE TABLE http_cache (
            account  TEXT NOT NULL,
            url      TEXT NOT NULL,
            etag     TEXT NOT NULL,
            body     TEXT NOT NULL,
            saved_at TEXT NOT NULL,
            PRIMARY KEY (account, url)
        );
        CREATE INDEX http_cache_saved ON http_cache (account, saved_at);
        """,

        // 3: notification history per account, so an event is announced once, also across restarts.
        // Keys hold repository IDs, branch names, commit SHAs and run attempts, never titles.
        """
        CREATE TABLE notification_history (
            account    TEXT NOT NULL,
            key        TEXT NOT NULL,
            outcome    TEXT NOT NULL,
            created_at TEXT NOT NULL,
            PRIMARY KEY (account, key)
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
    /// Creates the file if needed and applies pending migrations. Safe when several threads or
    /// processes initialize the same new database at once: they take turns through a named mutex
    /// for this file. Each step also takes the write lock (BEGIN IMMEDIATE) and re-reads the version.
    /// <para>
    /// The mutex is needed because a concurrent switch to WAL can make BEGIN IMMEDIATE fail with a
    /// plain "SQL logic error" (SQLITE_ERROR), which the busy timeout does not retry.
    /// </para>
    /// </summary>
    /// <exception cref="DatabaseVersionException">The database was created by a newer version of the app.</exception>
    public void Initialize()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        using var turn = InitializationLock.Acquire(Path);
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

/// <summary>One initializer at a time per database file, across threads and processes.</summary>
internal sealed class InitializationLock : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private readonly Mutex _mutex;

    private InitializationLock(Mutex mutex) => _mutex = mutex;

    public static InitializationLock Acquire(string path)
    {
        // Named after a hash of the normalized path: mutex names can't contain path separators.
        var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(path.ToUpperInvariant())))[..32];
        var mutex = new Mutex(false, $"RepoWatch-db-{key}");
        try
        {
            if (!mutex.WaitOne(Timeout))
            {
                throw new TimeoutException($"Another Repo Watch process kept the database '{path}' busy for {Timeout.TotalSeconds:0} seconds.");
            }
        }
        catch (AbandonedMutexException)
        {
            // A previous holder exited mid-initialization; migrations are transactional, so continue.
        }
        catch
        {
            mutex.Dispose();
            throw;
        }

        return new InitializationLock(mutex);
    }

    public void Dispose()
    {
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}

public sealed class DatabaseVersionException(string path, int found, int supported)
    : Exception($"The database '{path}' uses schema version {found}, but this version of Repo Watch supports up to {supported}. " +
                "Update Repo Watch, or move the file aside to start with empty local data.")
{
    public int FoundVersion { get; } = found;

    public int SupportedVersion { get; } = supported;
}
