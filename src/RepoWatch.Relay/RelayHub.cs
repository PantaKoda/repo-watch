using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using RepoWatch.Core.Relay;

namespace RepoWatch.Relay;

/// <summary>One server-sent event. <see cref="Seq"/> is 0 for control events that carry no replay ID.</summary>
public sealed record RelayEventRecord(long Seq, long RepositoryId, string Name, string Data);

/// <summary>A desktop client's authorization: who it is and which repositories (with their installation) it may receive.</summary>
public sealed class RelaySession(string tokenHash, long userId, Dictionary<long, long> repositories, DateTimeOffset expiresAt)
{
    public string TokenHash { get; } = tokenHash;

    public long UserId { get; } = userId;

    /// <summary>Repository ID → installation ID, confirmed with GitHub when the session was created.</summary>
    public Dictionary<long, long> Repositories { get; } = repositories;

    public DateTimeOffset ExpiresAt { get; } = expiresAt;

    public RelayConnection? Connection { get; set; }
}

/// <summary>An open event stream. It ends when the session is revoked, replaced by a newer stream, or expires.</summary>
public sealed class RelayConnection
{
    public RelayConnection(int capacity) =>
        Outbox = Channel.CreateBounded<RelayEventRecord>(new BoundedChannelOptions(capacity) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true } /* TryWrite fails when full */);

    public Channel<RelayEventRecord> Outbox { get; }

    /// <summary>Set when the outbox was full and events were dropped: the client must refresh everything.</summary>
    public bool Overflowed { get; set; }

    public CancellationTokenSource Ended { get; } = new();

    /// <summary>Why the stream ended: <see cref="RelayProtocol.Revoked"/>, or null when replaced by a newer stream.</summary>
    public string? EndEvent { get; set; }
}

/// <summary>
/// Sessions, the replay buffer and fan-out. Session tokens are random and stored only as SHA-256 hashes.
/// Sequence numbers start from the start time, so an ID from a previous relay run is recognized as a gap
/// and answered with "reset" (refresh everything) instead of silently missing events.
/// </summary>
public sealed class RelayHub(RelayServerOptions options, TimeProvider time)
{
    private const int MaxSessionsPerUser = 10;
    private const int OutboxCapacity = 1000;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, RelaySession> _sessions = new(StringComparer.Ordinal);
    private readonly LinkedList<RelayEventRecord> _buffer = new();
    private long _lastSeq = time.GetUtcNow().ToUnixTimeMilliseconds() * 1000;

    public long LastSeq
    {
        get
        {
            lock (_gate)
            {
                return _lastSeq;
            }
        }
    }

    public int SessionCount
    {
        get
        {
            lock (_gate)
            {
                return _sessions.Count;
            }
        }
    }

    /// <returns>The session token (shown to the client once; only its hash is kept).</returns>
    public (string Token, RelaySession Session) CreateSession(long userId, Dictionary<long, long> repositories)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var session = new RelaySession(Hash(token), userId, repositories, time.GetUtcNow().AddMinutes(options.SessionMinutes));
        lock (_gate)
        {
            RemoveExpired();
            foreach (var old in _sessions.Values.Where(s => s.UserId == userId).OrderBy(s => s.ExpiresAt).SkipLast(MaxSessionsPerUser - 1).ToList())
            {
                End(old, RelayProtocol.Expired);
            }

            _sessions[session.TokenHash] = session;
        }

        return (token, session);
    }

    /// <summary>The live session for a token, or null if unknown, expired or revoked.</summary>
    public RelaySession? Find(string token)
    {
        lock (_gate)
        {
            return _sessions.TryGetValue(Hash(token), out var session) && session.ExpiresAt > time.GetUtcNow() ? session : null;
        }
    }

    /// <summary>Opens the session's stream; a previous stream of the same session ends.</summary>
    public RelayConnection Connect(RelaySession session)
    {
        var connection = new RelayConnection(OutboxCapacity);
        lock (_gate)
        {
            if (session.Connection is { } previous)
            {
                previous.EndEvent = null;
                previous.Ended.Cancel();
            }

            session.Connection = connection;
        }

        return connection;
    }

    public void Disconnect(RelaySession session, RelayConnection connection)
    {
        lock (_gate)
        {
            if (ReferenceEquals(session.Connection, connection))
            {
                session.Connection = null;
            }
        }
    }

    /// <summary>Numbers the event, keeps it for replay and sends it to every session allowed to see the repository.</summary>
    public long Publish(RelayInvalidation invalidation)
    {
        var data = JsonSerializer.Serialize(invalidation, RelayJsonContext.Default.RelayInvalidation);
        lock (_gate)
        {
            var record = new RelayEventRecord(++_lastSeq, invalidation.RepositoryId, RelayProtocol.Invalidate, data);
            _buffer.AddLast(record);
            while (_buffer.Count > options.ReplayCapacity)
            {
                _buffer.RemoveFirst();
            }

            foreach (var session in _sessions.Values.Where(s => s.Connection is not null && s.Repositories.ContainsKey(invalidation.RepositoryId)))
            {
                Send(session.Connection!, record);
            }

            return record.Seq;
        }
    }

    /// <summary>
    /// Events after <paramref name="afterSeq"/> for the session's repositories, or a gap when some of them are
    /// no longer buffered (or the ID comes from another relay run).
    /// </summary>
    public (bool Gap, IReadOnlyList<RelayEventRecord> Events) Replay(long afterSeq, RelaySession session)
    {
        lock (_gate)
        {
            var oldest = _buffer.First?.Value.Seq ?? _lastSeq + 1;
            if (afterSeq > _lastSeq || afterSeq < oldest - 1)
            {
                return (true, []);
            }

            return (false, _buffer.Where(e => e.Seq > afterSeq && session.Repositories.ContainsKey(e.RepositoryId)).ToList());
        }
    }

    /// <summary>Access to these repositories ended (removed from the installation, or deleted): stop sending, tell clients.</summary>
    public void RemoveRepositories(IReadOnlyCollection<long> repositoryIds)
    {
        lock (_gate)
        {
            foreach (var session in _sessions.Values)
            {
                RemoveFrom(session, repositoryIds.Where(session.Repositories.ContainsKey).ToList());
            }
        }
    }

    /// <summary>The app was uninstalled or suspended for an account: its repositories leave every session.</summary>
    public void RevokeInstallation(long installationId)
    {
        lock (_gate)
        {
            foreach (var session in _sessions.Values)
            {
                RemoveFrom(session, session.Repositories.Where(r => r.Value == installationId).Select(r => r.Key).ToList());
            }
        }
    }

    /// <summary>The user revoked the app's authorization: all their sessions end now.</summary>
    public void RevokeUser(long userId)
    {
        lock (_gate)
        {
            foreach (var session in _sessions.Values.Where(s => s.UserId == userId).ToList())
            {
                End(session, RelayProtocol.Revoked);
            }
        }
    }

    private void RemoveFrom(RelaySession session, List<long> repositoryIds)
    {
        foreach (var id in repositoryIds)
        {
            session.Repositories.Remove(id);
            if (session.Connection is { } connection)
            {
                var data = JsonSerializer.Serialize(new RelayInvalidation(id, [RelayProtocol.Parts.Metadata]), RelayJsonContext.Default.RelayInvalidation);
                Send(connection, new RelayEventRecord(0, id, RelayProtocol.Revoked, data));
            }
        }

        if (repositoryIds.Count > 0 && session.Repositories.Count == 0)
        {
            End(session, RelayProtocol.Revoked);
        }
    }

    private void End(RelaySession session, string reason)
    {
        _sessions.Remove(session.TokenHash);
        if (session.Connection is { } connection)
        {
            connection.EndEvent = reason;
            connection.Ended.Cancel();
        }
    }

    private void RemoveExpired()
    {
        var now = time.GetUtcNow();
        foreach (var expired in _sessions.Values.Where(s => s.ExpiresAt <= now).ToList())
        {
            End(expired, RelayProtocol.Expired);
        }
    }

    private static void Send(RelayConnection connection, RelayEventRecord record)
    {
        if (!connection.Outbox.Writer.TryWrite(record))
        {
            connection.Overflowed = true;
        }
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
