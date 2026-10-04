using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using RepoWatch.Core.Relay;
using RepoWatch.GitHub.Api;
using RepoWatch.GitHub.Relay;

namespace RepoWatch.Desktop.Services;

/// <summary>
/// Connects the polling monitor to the relay (Stage 10). It creates a short-lived session for the watched
/// repositories and reads its event stream. Each invalidation makes the monitor refresh just that
/// repository's affected parts from GitHub.
/// <list type="bullet">
/// <item>Live coverage is per repository: only the session's allowed repositories poll at the slower live
/// rate, and the widget shows "Live" only when every watched repository is covered.</item>
/// <item>A stream that goes silent (no keep-alive for <see cref="DefaultIdleTimeout"/>), waking from sleep and a
/// network change all end the stream, so a dead connection never keeps the widget "Live".</item>
/// <item>A first connect, or a replay gap ("reset"), reconciles everything; a resumed stream replays from
/// Last-Event-ID instead.</item>
/// <item>When the relay is unreachable the monitor polls as before; this link retries with backoff.</item>
/// </list>
/// </summary>
public sealed class RelayLink : IDisposable
{
    /// <summary>Three missed keep-alives at the relay's default 20-second interval.</summary>
    public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan RenewBefore = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(2);

    private readonly PollingRepositoryMonitor _monitor;
    private readonly RelayClient _client;
    private readonly IAccessTokenSource _tokens;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly PollingConditions? _conditions;
    private readonly TimeSpan _idleTimeout;
    private readonly CancellationTokenSource _stop;

    /// <summary>Coalescing "restart the stream" signal: at most one pending, so bursts cause one reconnect.</summary>
    private readonly Channel<bool> _restart = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Lock _subscription = new(); // checking the watchlist and recording/draining a session's set are atomic
    private HashSet<long> _subscribed = [];

    public RelayLink(PollingRepositoryMonitor monitor, RelayClient client, IAccessTokenSource tokens, TimeProvider time, ILogger logger, CancellationToken lifetime,
        PollingConditions? conditions = null, TimeSpan? idleTimeout = null)
    {
        _monitor = monitor;
        _client = client;
        _tokens = tokens;
        _time = time;
        _logger = logger;
        _conditions = conditions;
        _idleTimeout = idleTimeout ?? DefaultIdleTimeout;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        _monitor.Changed += OnMonitorChanged;
        if (_conditions is not null)
        {
            _conditions.Resumed += OnResumed;
        }

        _ = Task.Run(() => RunAsync(_stop.Token));
    }

    /// <summary>When the last event arrived (for diagnostics and latency measurements).</summary>
    public DateTimeOffset? LastEventAt { get; private set; }

    /// <summary>Sessions created so far (diagnostics and tests).</summary>
    public int SessionsCreated { get; private set; }

    public void Dispose()
    {
        _monitor.Changed -= OnMonitorChanged;
        if (_conditions is not null)
        {
            _conditions.Resumed -= OnResumed;
        }

        _stop.Cancel();
        _stop.Dispose();
    }

    private void OnMonitorChanged(object? sender, EventArgs e)
    {
        lock (_subscription)
        {
            // Under the lock: a signal can't land just after a new session drained it for the set it already covers.
            var ids = _monitor.Repositories.Select(r => r.Key.RepositoryId).ToHashSet();
            if (!ids.SetEquals(_subscribed))
            {
                _restart.Writer.TryWrite(true); // the session must cover exactly the watched repositories
            }
        }
    }

    /// <summary>Woke from sleep or the network came back: the old connection is probably dead.</summary>
    private void OnResumed(object? sender, EventArgs e) => _restart.Writer.TryWrite(true);

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var failures = 0;
        long? lastEventId = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            var outcome = Outcome.Failed;
            try
            {
                outcome = await ConnectOnceAsync(lastEventId, id => lastEventId = id, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or JsonException or TimeoutException)
            {
                _logger.LogInformation("Relay stream ended ({Error}); polling continues", ex.GetType().Name);
            }
            finally
            {
                _monitor.SetLive([]);
            }

            failures = outcome == Outcome.Failed ? failures + 1 : 0;
            var delay = outcome switch
            {
                Outcome.Restart => TimeSpan.Zero,
                Outcome.NothingToWatch => TimeSpan.FromMinutes(5),
                _ when failures == 0 => TimeSpan.Zero,
                _ => TimeSpan.FromSeconds(Math.Min(MaxBackoff.TotalSeconds, Math.Pow(2, Math.Min(failures, 7)) * (0.8 + (0.4 * Random.Shared.NextDouble())))),
            };
            if (delay > TimeSpan.Zero && !await WaitForRestartAsync(delay, cancellationToken).ConfigureAwait(false))
            {
                return;
            }
        }
    }

    private enum Outcome
    {
        Failed,
        /// <summary>Renewal, watchlist change, wake or network change: reconnect now.</summary>
        Restart,
        /// <summary>Nothing to subscribe to (or nothing the relay allows): wait for a watchlist change.</summary>
        NothingToWatch,
    }

    /// <summary>Waits for a restart signal or the delay. Returns false when shutting down.</summary>
    private async Task<bool> WaitForRestartAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(delay);
        try
        {
            await _restart.Reader.ReadAsync(wait.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Delay elapsed.
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        return true;
    }

    /// <summary>One session and stream.</summary>
    private async Task<Outcome> ConnectOnceAsync(long? lastEventId, Action<long> seen, CancellationToken cancellationToken)
    {
        // Record what this attempt subscribes to first, then drop the signals it already covers. Changes after
        // that compare against the new set, so a burst of watchlist events causes one session, not several.
        HashSet<long> ids;
        lock (_subscription)
        {
            ids = _monitor.Repositories.Select(r => r.Key.RepositoryId).ToHashSet();
            _subscribed = ids;
            _restart.Reader.TryRead(out _);
        }

        if (ids.Count == 0 || await _tokens.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false) is not { } token)
        {
            return Outcome.NothingToWatch;
        }

        var (session, failure) = await _client.CreateSessionAsync(token, ids, cancellationToken).ConfigureAwait(false);
        SessionsCreated++;
        if (session is null)
        {
            _logger.LogInformation("The relay refused a session ({Status}); polling continues", failure);
            return failure == HttpStatusCode.Forbidden ? Outcome.NothingToWatch : Outcome.Failed;
        }

        // End this stream to renew the session shortly before it expires, or on a restart signal.
        using var stream = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var renewIn = session.ExpiresAt - _time.GetUtcNow() - RenewBefore;
        stream.CancelAfter(renewIn > TimeSpan.FromSeconds(30) ? renewIn : TimeSpan.FromSeconds(30));
        var watcher = WatchForRestartAsync(stream);
        try
        {
            var resumed = lastEventId is not null;
            await foreach (var message in _client.StreamAsync(session.SessionToken, lastEventId, () =>
            {
                _monitor.SetLive(session.Allowed);
                if (!resumed)
                {
                    _monitor.ReconcileAll(); // first connect: nothing to replay from
                }
            }, _idleTimeout, stream.Token).ConfigureAwait(false))
            {
                LastEventAt = _time.GetUtcNow();
                if (message.Id is { } id)
                {
                    seen(id);
                }

                if (!Handle(message))
                {
                    return Outcome.Restart;
                }
            }

            return Outcome.Failed; // the relay closed the stream
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Outcome.Restart; // renewal or restart signal
        }
        finally
        {
            await stream.CancelAsync().ConfigureAwait(false); // releases the watcher, so nothing waits on a dead stream
            await watcher.ConfigureAwait(false);
        }
    }

    private async Task WatchForRestartAsync(CancellationTokenSource stream)
    {
        try
        {
            await _restart.Reader.ReadAsync(stream.Token).ConfigureAwait(false);
            await stream.CancelAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The stream ended first.
        }
    }

    /// <returns>False when the session ended and a new one is needed.</returns>
    private bool Handle(RelayMessage message)
    {
        switch (message.Name)
        {
            case RelayProtocol.Invalidate:
                if (Parse(message) is { } invalidation)
                {
                    _monitor.Invalidate(invalidation.RepositoryId, Parts(invalidation.Parts));
                }

                return true;
            case RelayProtocol.Revoked when Parse(message) is { RepositoryId: > 0 } revoked:
                // Access to one repository ended: refreshing it shows "access lost" from GitHub itself.
                _monitor.Invalidate(revoked.RepositoryId, RefreshParts.All);
                return true;
            case RelayProtocol.Reset:
                _monitor.ReconcileAll();
                return true;
            case RelayProtocol.Revoked or RelayProtocol.Expired:
                _monitor.ReconcileAll();
                return false;
            default:
                return true; // unknown events are ignored (forward compatibility)
        }
    }

    private static RelayInvalidation? Parse(RelayMessage message)
    {
        try
        {
            return JsonSerializer.Deserialize(message.Data, RelayJsonContext.Default.RelayInvalidation);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static RefreshParts Parts(IReadOnlyList<string> parts) => parts.Aggregate(RefreshParts.None, (all, part) => all | part switch
    {
        RelayProtocol.Parts.Metadata => RefreshParts.Metadata,
        RelayProtocol.Parts.Actions => RefreshParts.Actions,
        RelayProtocol.Parts.PullRequests => RefreshParts.PullRequests,
        RelayProtocol.Parts.Issues => RefreshParts.Issues,
        _ => RefreshParts.None,
    });
}
