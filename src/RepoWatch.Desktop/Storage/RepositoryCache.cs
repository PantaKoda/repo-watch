using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using RepoWatch.Core.Actions;
using RepoWatch.Core.Identity;
using RepoWatch.Core.Issues;
using RepoWatch.Core.PullRequests;
using RepoWatch.Core.Settings;
using RepoWatch.Core.State;
using RepoWatch.GitHub.Api;

namespace RepoWatch.Desktop.Storage;

/// <summary>
/// Per-account caches of private repository content in SQLite: the last good snapshot of each watched
/// repository (shown as "Cached" after a restart until the first refresh confirms it) and REST ETags
/// with their bodies (conditional requests). Best effort: a storage failure is logged and the app
/// continues without the cache. Never stores tokens.
/// <para>
/// Writes go through an <see cref="AccountCache"/>, which belongs to one sign-in. Clearing an account
/// (sign-out) invalidates every handle taken before it, under the same lock as the writes, so a refresh
/// that finishes during sign-out can never write content back after the clear.
/// </para>
/// <para>
/// The snapshot format is versioned; an unreadable or older-format row is discarded, because a cache
/// can always be rebuilt from GitHub.
/// </para>
/// </summary>
public sealed class RepositoryCache(LocalDatabase database, TimeProvider time, ILogger<RepositoryCache> logger)
{
    public const int SnapshotFormat = 1;

    internal static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _writes = new();
    private readonly Dictionary<AccountKey, int> _epochs = [];

    internal TimeProvider Time => time;

    internal ILogger Logger => logger;

    /// <summary>A handle for one sign-in of the account. It stops writing once the account is cleared.</summary>
    public AccountCache ForAccount(AccountKey account, int memoryEntries = AccountCache.DefaultMemoryEntries)
    {
        ArgumentNullException.ThrowIfNull(account);
        lock (_writes)
        {
            return new AccountCache(this, account, _epochs.GetValueOrDefault(account), memoryEntries);
        }
    }

    /// <summary>Removes every cached snapshot and ETag body of the account (sign-out) and invalidates its handles.</summary>
    public void ClearAccount(AccountKey account)
    {
        ArgumentNullException.ThrowIfNull(account);
        lock (_writes)
        {
            _epochs[account] = _epochs.GetValueOrDefault(account) + 1;
            Run("clearing the account's cached data", connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    DELETE FROM repository_snapshots WHERE account = $account;
                    DELETE FROM http_cache WHERE account = $account;
                    """;
                command.Parameters.AddWithValue("$account", account.StorageKey);
                command.ExecuteNonQuery();
            });
        }
    }

    /// <summary>Runs a write if the handle's sign-in is still current; the check and the write are atomic with clearing.</summary>
    internal void Write(AccountKey account, int epoch, string what, Action<SqliteConnection> action)
    {
        lock (_writes)
        {
            if (_epochs.GetValueOrDefault(account) == epoch)
            {
                Run(what, action);
            }
        }
    }

    internal bool IsCurrent(AccountKey account, int epoch)
    {
        lock (_writes)
        {
            return _epochs.GetValueOrDefault(account) == epoch;
        }
    }

    internal string Now() => time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);

    internal void Run(string what, Action<SqliteConnection> action)
    {
        try
        {
            using var connection = database.Open();
            action(connection);
        }
        catch (SqliteException ex)
        {
            logger.LogWarning("Local cache unavailable while {What} ({Code}); continuing without it", what, ex.SqliteErrorCode);
        }
        catch (IOException ex)
        {
            logger.LogWarning("Local cache unavailable while {What} ({Error}); continuing without it", what, ex.GetType().Name);
        }
    }
}

/// <summary>
/// One account's cache, for one sign-in: snapshots for the monitor and ETags for the API client.
/// The in-memory front holds at most <see cref="DefaultMemoryEntries"/> responses (least recently used
/// first out), because commit-specific URLs keep appearing; SQLite keeps the rest until retention.
/// </summary>
public sealed class AccountCache : IConditionalCache
{
    public const int DefaultMemoryEntries = 200;

    private readonly RepositoryCache _owner;
    private readonly AccountKey _account;
    private readonly int _epoch;
    private readonly int _memoryEntries;
    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedListNode<(string Url, CachedResponse? Response)>> _index = new(StringComparer.Ordinal);
    private readonly LinkedList<(string Url, CachedResponse? Response)> _recent = new();

    internal AccountCache(RepositoryCache owner, AccountKey account, int epoch, int memoryEntries)
    {
        _owner = owner;
        _account = account;
        _epoch = epoch;
        _memoryEntries = Math.Max(1, memoryEntries);
    }

    public AccountKey Account => _account;

    /// <summary>False once the account was cleared (signed out) after this handle was taken.</summary>
    public bool IsCurrent => _owner.IsCurrent(_account, _epoch);

    /// <summary>Responses currently held in memory (bounded).</summary>
    public int MemoryCount
    {
        get
        {
            lock (_gate)
            {
                return _index.Count;
            }
        }
    }

    public CachedResponse? Get(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        var url = uri.AbsoluteUri;
        lock (_gate)
        {
            if (_index.TryGetValue(url, out var node))
            {
                _recent.Remove(node);
                _recent.AddFirst(node);
                return node.Value.Response;
            }
        }

        CachedResponse? stored = null;
        _owner.Run("reading a cached response", connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT etag, body FROM http_cache WHERE account = $account AND url = $url;";
            command.Parameters.AddWithValue("$account", _account.StorageKey);
            command.Parameters.AddWithValue("$url", url);
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                stored = new CachedResponse(reader.GetString(0), reader.GetString(1));
            }
        });
        Remember(url, stored);
        return stored;
    }

    public void Put(Uri uri, CachedResponse response)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentNullException.ThrowIfNull(response);
        if (!IsCurrent)
        {
            return;
        }

        Remember(uri.AbsoluteUri, response);
        _owner.Write(_account, _epoch, "saving a cached response", connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO http_cache (account, url, etag, body, saved_at) VALUES ($account, $url, $etag, $body, $now)
                ON CONFLICT (account, url) DO UPDATE SET etag = excluded.etag, body = excluded.body, saved_at = excluded.saved_at;
                """;
            command.Parameters.AddWithValue("$account", _account.StorageKey);
            command.Parameters.AddWithValue("$url", uri.AbsoluteUri);
            command.Parameters.AddWithValue("$etag", response.ETag);
            command.Parameters.AddWithValue("$body", response.Body);
            command.Parameters.AddWithValue("$now", _owner.Now());
            command.ExecuteNonQuery();
        });
    }

    /// <summary>Saves a repository's last good data. A repository whose access was lost is removed instead.</summary>
    public void Save(RepositorySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Key.Account != _account)
        {
            return;
        }

        if (new[] { snapshot.Metadata.Availability, snapshot.Actions.Availability, snapshot.PullRequests.Availability, snapshot.Issues.Availability }
            .Contains(ResourceAvailability.AccessLost))
        {
            Delete(snapshot.Key.RepositoryId);
            return;
        }

        var cached = new CachedSnapshot(Section(snapshot.Metadata), Section(snapshot.Actions), Section(snapshot.PullRequests), Section(snapshot.Issues));
        var json = JsonSerializer.Serialize(cached, RepositoryCache.Json);
        _owner.Write(_account, _epoch, "saving a repository snapshot", connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO repository_snapshots (account, repository_id, format, json, saved_at)
                VALUES ($account, $id, $format, $json, $now)
                ON CONFLICT (account, repository_id) DO UPDATE SET format = excluded.format, json = excluded.json, saved_at = excluded.saved_at;
                """;
            command.Parameters.AddWithValue("$account", _account.StorageKey);
            command.Parameters.AddWithValue("$id", snapshot.Key.RepositoryId);
            command.Parameters.AddWithValue("$format", RepositoryCache.SnapshotFormat);
            command.Parameters.AddWithValue("$json", json);
            command.Parameters.AddWithValue("$now", _owner.Now());
            command.ExecuteNonQuery();
        });
    }

    /// <summary>Cached snapshots of the account's repositories, as <see cref="Resource{T}.IsFromCache"/> data.</summary>
    public IReadOnlyDictionary<long, RepositorySnapshot> Load()
    {
        var result = new Dictionary<long, RepositorySnapshot>();
        var unreadable = new List<long>();
        _owner.Run("loading cached repository snapshots", connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT repository_id, format, json FROM repository_snapshots WHERE account = $account;";
            command.Parameters.AddWithValue("$account", _account.StorageKey);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetInt64(0);
                if (reader.GetInt32(1) == RepositoryCache.SnapshotFormat && Restore(new RepositoryKey(_account, id), reader.GetString(2)) is { } snapshot)
                {
                    result[id] = snapshot;
                }
                else
                {
                    unreadable.Add(id);
                }
            }
        });

        foreach (var id in unreadable)
        {
            Delete(id);
        }

        return result;
    }

    /// <summary>Deletes a repository's snapshot.</summary>
    public void Delete(long repositoryId) => _owner.Write(_account, _epoch, "deleting a repository snapshot", connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM repository_snapshots WHERE account = $account AND repository_id = $id;";
        command.Parameters.AddWithValue("$account", _account.StorageKey);
        command.Parameters.AddWithValue("$id", repositoryId);
        command.ExecuteNonQuery();
    });

    /// <summary>A repository left the watchlist: its snapshot and every cached response about it go.</summary>
    public void Forget(long repositoryId, string? owner, string? name)
    {
        Delete(repositoryId);
        var patterns = Patterns(repositoryId, owner, name);
        _owner.Write(_account, _epoch, "deleting a repository's cached responses", connection =>
        {
            foreach (var url in Urls(connection).Where(url => patterns.Any(p => url.Contains(p, StringComparison.OrdinalIgnoreCase))))
            {
                DeleteUrl(connection, url);
            }
        });

        lock (_gate)
        {
            foreach (var node in _index.Values.Where(n => patterns.Any(p => n.Value.Url.Contains(p, StringComparison.OrdinalIgnoreCase))).ToList())
            {
                _recent.Remove(node);
                _index.Remove(node.Value.Url);
            }
        }
    }

    /// <summary>
    /// Retention: drops snapshots of repositories no longer watched and anything older than
    /// <paramref name="maxAge"/>, cached responses that belong to no watched repository, and the oldest
    /// responses beyond <paramref name="maxResponses"/>. Called at start and periodically.
    /// </summary>
    public void Prune(IReadOnlyCollection<WatchedRepository> watched, TimeSpan maxAge, int maxResponses)
    {
        ArgumentNullException.ThrowIfNull(watched);
        var cutoff = (_owner.Time.GetUtcNow() - maxAge).ToString("O", CultureInfo.InvariantCulture);
        var ids = watched.Select(w => w.RepositoryId).ToHashSet();
        var patterns = watched.SelectMany(w => Patterns(w.RepositoryId, w.Owner, w.Name)).ToList();
        _owner.Write(_account, _epoch, "pruning cached data", connection =>
        {
            using var transaction = connection.BeginTransaction();
            Execute(connection, transaction, """
                DELETE FROM repository_snapshots WHERE account = $account AND saved_at < $cutoff;
                DELETE FROM http_cache WHERE account = $account AND saved_at < $cutoff;
                """, ("$cutoff", cutoff));

            foreach (var id in Ids(connection, transaction).Where(id => !ids.Contains(id)))
            {
                Execute(connection, transaction, "DELETE FROM repository_snapshots WHERE account = $account AND repository_id = $id;", ("$id", id));
            }

            foreach (var url in Urls(connection, transaction).Where(url => !patterns.Any(p => url.Contains(p, StringComparison.OrdinalIgnoreCase))))
            {
                DeleteUrl(connection, url, transaction);
            }

            Execute(connection, transaction, """
                DELETE FROM http_cache WHERE account = $account AND url NOT IN
                    (SELECT url FROM http_cache WHERE account = $account ORDER BY saved_at DESC LIMIT $max);
                """, ("$max", maxResponses));
            transaction.Commit();
        });

        lock (_gate)
        {
            _index.Clear();
            _recent.Clear();
        }
    }

    /// <summary>Cached responses on disk (for tests and diagnostics).</summary>
    public int StoredResponseCount()
    {
        var count = 0;
        _owner.Run("counting cached responses", connection => count = Urls(connection).Count);
        return count;
    }

    private void Remember(string url, CachedResponse? response)
    {
        lock (_gate)
        {
            if (_index.TryGetValue(url, out var existing))
            {
                _recent.Remove(existing);
            }

            _index[url] = _recent.AddFirst((url, response));
            while (_index.Count > _memoryEntries && _recent.Last is { } oldest)
            {
                _recent.RemoveLast();
                _index.Remove(oldest.Value.Url);
            }
        }
    }

    /// <summary>URL fragments that identify a repository: by ID and by its last known owner/name.</summary>
    private static List<string> Patterns(long repositoryId, string? owner, string? name)
    {
        var patterns = new List<string> { string.Create(CultureInfo.InvariantCulture, $"/repositories/{repositoryId}") };
        if (!string.IsNullOrEmpty(owner) && !string.IsNullOrEmpty(name))
        {
            patterns.Add($"/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/");
        }

        return patterns;
    }

    private List<string> Urls(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT url FROM http_cache WHERE account = $account;";
        command.Parameters.AddWithValue("$account", _account.StorageKey);
        using var reader = command.ExecuteReader();
        var urls = new List<string>();
        while (reader.Read())
        {
            urls.Add(reader.GetString(0));
        }

        return urls;
    }

    private List<long> Ids(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT repository_id FROM repository_snapshots WHERE account = $account;";
        command.Parameters.AddWithValue("$account", _account.StorageKey);
        using var reader = command.ExecuteReader();
        var ids = new List<long>();
        while (reader.Read())
        {
            ids.Add(reader.GetInt64(0));
        }

        return ids;
    }

    private void DeleteUrl(SqliteConnection connection, string url, SqliteTransaction? transaction = null) =>
        Execute(connection, transaction, "DELETE FROM http_cache WHERE account = $account AND url = $url;", ("$url", url));

    private void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$account", _account.StorageKey);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        command.ExecuteNonQuery();
    }

    private RepositorySnapshot? Restore(RepositoryKey key, string json)
    {
        try
        {
            var cached = JsonSerializer.Deserialize<CachedSnapshot>(json, RepositoryCache.Json);
            if (cached is null || (cached.Metadata?.Value is { } metadata && metadata.Key != key))
            {
                return null;
            }

            return new RepositorySnapshot(key)
            {
                Metadata = Restore(cached.Metadata),
                Actions = Restore(cached.Actions),
                PullRequests = Restore(cached.PullRequests),
                Issues = Restore(cached.Issues),
            };
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            _owner.Logger.LogWarning("A cached snapshot couldn't be read and was discarded ({Error})", ex.GetType().Name);
            return null;
        }
    }

    private static CachedSection<T>? Section<T>(Resource<T> resource) where T : class => resource switch
    {
        { LastSuccessAt: { } at, Value: { } value } => new CachedSection<T>(value, false, at),
        { LastSuccessAt: { } at, Availability: ResourceAvailability.FeatureUnavailable } => new CachedSection<T>(null, true, at),
        _ => null,
    };

    private static Resource<T> Restore<T>(CachedSection<T>? section) where T : class => section switch
    {
        { Value: { } value } => Resource<T>.FromCache(value, section.LastSuccessAt),
        { FeatureUnavailable: true } => Resource<T>.NotLoaded.FeatureUnavailable(section.LastSuccessAt) with { IsFromCache = true },
        _ => Resource<T>.NotLoaded,
    };

    private sealed record CachedSnapshot(
        CachedSection<RepositoryMetadata>? Metadata,
        CachedSection<ActionsState>? Actions,
        CachedSection<PullRequestsState>? PullRequests,
        CachedSection<IssuesState>? Issues);

    private sealed record CachedSection<T>(T? Value, bool FeatureUnavailable, DateTimeOffset LastSuccessAt) where T : class;
}
