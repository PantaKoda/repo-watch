using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using RepoWatch.Core.Actions;
using RepoWatch.Core.Identity;
using RepoWatch.Core.Issues;
using RepoWatch.Core.PullRequests;
using RepoWatch.Core.State;
using RepoWatch.GitHub.Api;

namespace RepoWatch.Desktop.Storage;

/// <summary>
/// Per-account caches of private repository content in SQLite: the last good snapshot of each watched
/// repository (shown as "Cached" after a restart until the first refresh confirms it) and REST ETags
/// with their bodies (conditional requests). Best effort: a storage failure is logged and the app
/// continues without the cache. Never stores tokens. Cleared when the account signs out.
/// <para>
/// The snapshot format is versioned; an unreadable or older-format row is discarded, because a cache
/// can always be rebuilt from GitHub.
/// </para>
/// </summary>
public sealed class RepositoryCache(LocalDatabase database, TimeProvider time, ILogger<RepositoryCache> logger)
{
    public const int SnapshotFormat = 1;

    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Saves a repository's last good data. A repository whose access was lost is removed instead.</summary>
    public void Save(RepositorySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (new[] { snapshot.Metadata.Availability, snapshot.Actions.Availability, snapshot.PullRequests.Availability, snapshot.Issues.Availability }
            .Contains(ResourceAvailability.AccessLost))
        {
            Delete(snapshot.Key);
            return;
        }

        var cached = new CachedSnapshot(Section(snapshot.Metadata), Section(snapshot.Actions), Section(snapshot.PullRequests), Section(snapshot.Issues));
        Run("saving a repository snapshot", connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO repository_snapshots (account, repository_id, format, json, saved_at)
                VALUES ($account, $id, $format, $json, $now)
                ON CONFLICT (account, repository_id) DO UPDATE SET format = excluded.format, json = excluded.json, saved_at = excluded.saved_at;
                """;
            command.Parameters.AddWithValue("$account", snapshot.Key.Account.StorageKey);
            command.Parameters.AddWithValue("$id", snapshot.Key.RepositoryId);
            command.Parameters.AddWithValue("$format", SnapshotFormat);
            command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(cached, Json));
            command.Parameters.AddWithValue("$now", Now());
            command.ExecuteNonQuery();
        });
    }

    /// <summary>Cached snapshots of the account's repositories, as <see cref="Resource{T}.IsFromCache"/> data.</summary>
    public IReadOnlyDictionary<long, RepositorySnapshot> Load(AccountKey account)
    {
        ArgumentNullException.ThrowIfNull(account);
        var result = new Dictionary<long, RepositorySnapshot>();
        var unreadable = new List<long>();
        Run("loading cached repository snapshots", connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT repository_id, format, json FROM repository_snapshots WHERE account = $account;";
            command.Parameters.AddWithValue("$account", account.StorageKey);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetInt64(0);
                var key = new RepositoryKey(account, id);
                if (reader.GetInt32(1) == SnapshotFormat && Restore(key, reader.GetString(2)) is { } snapshot)
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
            Delete(new RepositoryKey(account, id));
        }

        return result;
    }

    public void Delete(RepositoryKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        Run("deleting a repository snapshot", connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM repository_snapshots WHERE account = $account AND repository_id = $id;";
            command.Parameters.AddWithValue("$account", key.Account.StorageKey);
            command.Parameters.AddWithValue("$id", key.RepositoryId);
            command.ExecuteNonQuery();
        });
    }

    /// <summary>
    /// Retention: drops snapshots of repositories no longer watched and cache entries older than
    /// <paramref name="maxAge"/>.
    /// </summary>
    public void Prune(AccountKey account, IReadOnlyCollection<long> watched, TimeSpan maxAge)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(watched);
        var cutoff = (time.GetUtcNow() - maxAge).ToString("O", CultureInfo.InvariantCulture);
        Run("pruning cached data", connection =>
        {
            using var transaction = connection.BeginTransaction();
            using (var stale = connection.CreateCommand())
            {
                stale.Transaction = transaction;
                stale.CommandText = """
                    DELETE FROM repository_snapshots WHERE account = $account AND saved_at < $cutoff;
                    DELETE FROM http_cache WHERE account = $account AND saved_at < $cutoff;
                    """;
                stale.Parameters.AddWithValue("$account", account.StorageKey);
                stale.Parameters.AddWithValue("$cutoff", cutoff);
                stale.ExecuteNonQuery();
            }

            using (var ids = connection.CreateCommand())
            {
                ids.Transaction = transaction;
                ids.CommandText = "SELECT repository_id FROM repository_snapshots WHERE account = $account;";
                ids.Parameters.AddWithValue("$account", account.StorageKey);
                var unwatched = new List<long>();
                using (var reader = ids.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var id = reader.GetInt64(0);
                        if (!watched.Contains(id))
                        {
                            unwatched.Add(id);
                        }
                    }
                }

                foreach (var id in unwatched)
                {
                    using var delete = connection.CreateCommand();
                    delete.Transaction = transaction;
                    delete.CommandText = "DELETE FROM repository_snapshots WHERE account = $account AND repository_id = $id;";
                    delete.Parameters.AddWithValue("$account", account.StorageKey);
                    delete.Parameters.AddWithValue("$id", id);
                    delete.ExecuteNonQuery();
                }
            }

            transaction.Commit();
        });
    }

    /// <summary>Removes every cached snapshot and ETag body of the account (sign-out).</summary>
    public void ClearAccount(AccountKey account)
    {
        ArgumentNullException.ThrowIfNull(account);
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

    /// <summary>The ETag cache of one account, for the API client.</summary>
    public IConditionalCache ForAccount(AccountKey account) => new AccountConditionalCache(this, account);

    internal CachedResponse? GetResponse(AccountKey account, Uri uri)
    {
        CachedResponse? response = null;
        Run("reading a cached response", connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT etag, body FROM http_cache WHERE account = $account AND url = $url;";
            command.Parameters.AddWithValue("$account", account.StorageKey);
            command.Parameters.AddWithValue("$url", uri.AbsoluteUri);
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                response = new CachedResponse(reader.GetString(0), reader.GetString(1));
            }
        });
        return response;
    }

    internal void PutResponse(AccountKey account, Uri uri, CachedResponse response)
    {
        Run("saving a cached response", connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO http_cache (account, url, etag, body, saved_at) VALUES ($account, $url, $etag, $body, $now)
                ON CONFLICT (account, url) DO UPDATE SET etag = excluded.etag, body = excluded.body, saved_at = excluded.saved_at;
                """;
            command.Parameters.AddWithValue("$account", account.StorageKey);
            command.Parameters.AddWithValue("$url", uri.AbsoluteUri);
            command.Parameters.AddWithValue("$etag", response.ETag);
            command.Parameters.AddWithValue("$body", response.Body);
            command.Parameters.AddWithValue("$now", Now());
            command.ExecuteNonQuery();
        });
    }

    private RepositorySnapshot? Restore(RepositoryKey key, string json)
    {
        try
        {
            var cached = JsonSerializer.Deserialize<CachedSnapshot>(json, Json);
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
            logger.LogWarning("A cached snapshot couldn't be read and was discarded ({Error})", ex.GetType().Name);
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

    private string Now() => time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);

    private void Run(string what, Action<SqliteConnection> action)
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

    private sealed record CachedSnapshot(
        CachedSection<RepositoryMetadata>? Metadata,
        CachedSection<ActionsState>? Actions,
        CachedSection<PullRequestsState>? PullRequests,
        CachedSection<IssuesState>? Issues);

    private sealed record CachedSection<T>(T? Value, bool FeatureUnavailable, DateTimeOffset LastSuccessAt) where T : class;

    /// <summary>ETags for one account, with a small in-memory front so repeated polls don't re-read SQLite.</summary>
    private sealed class AccountConditionalCache(RepositoryCache cache, AccountKey account) : IConditionalCache
    {
        private readonly ConcurrentDictionary<string, CachedResponse?> _memory = new(StringComparer.Ordinal);

        public CachedResponse? Get(Uri uri) => _memory.GetOrAdd(uri.AbsoluteUri, _ => cache.GetResponse(account, uri));

        public void Put(Uri uri, CachedResponse response)
        {
            _memory[uri.AbsoluteUri] = response;
            cache.PutResponse(account, uri, response);
        }
    }
}
