using System.Text.Json;
using Microsoft.Extensions.Logging;
using RepoWatch.Core.Relay;
using RepoWatch.GitHub.Api;
using RepoWatch.GitHub.Relay;

namespace RepoWatch.Desktop.Services;

/// <summary>
/// Connects the polling monitor to the relay (Stage 10). It creates a short-lived session for the watched
/// repositories and reads its event stream. Each invalidation makes the monitor refresh just that
/// repository's affected parts from GitHub. A reconnect, a replay gap or a session renewal reconciles
/// everything. While connected the monitor reports "Live" and polls only to reconcile. When the relay is
/// unreachable the monitor polls as before; this link retries with backoff in the background.
/// </summary>
public sealed class RelayLink : IDisposable
{
    private static readonly TimeSpan RenewBefore = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(2);

    private readonly PollingRepositoryMonitor _monitor;
    private readonly RelayClient _client;
    private readonly IAccessTokenSource _tokens;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stop;
    private readonly SemaphoreSlim _watchlistChanged = new(0);
    private HashSet<long> _subscribed = [];

    public RelayLink(PollingRepositoryMonitor monitor, RelayClient client, IAccessTokenSource tokens, TimeProvider time, ILogger logger, CancellationToken lifetime)
    {
        _monitor = monitor;
        _client = client;
        _tokens = tokens;
        _time = time;
        _logger = logger;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        _monitor.Changed += OnMonitorChanged;
        _ = Task.Run(() => RunAsync(_stop.Token));
    }

    /// <summary>When the last event arrived (for diagnostics and latency measurements).</summary>
    public DateTimeOffset? LastEventAt { get; private set; }

    public void Dispose()
    {
        _monitor.Changed -= OnMonitorChanged;
        _stop.Cancel();
        _stop.Dispose();
    }

    private void OnMonitorChanged(object? sender, EventArgs e)
    {
        var ids = _monitor.Repositories.Select(r => r.Key.RepositoryId).ToHashSet();
        if (!ids.SetEquals(Volatile.Read(ref _subscribed)))
        {
            _watchlistChanged.Release(); // the session must cover exactly the watched repositories
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var failures = 0;
        long? lastEventId = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            var connected = false;
            try
            {
                connected = await ConnectOnceAsync(lastEventId, id => lastEventId = id, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or JsonException)
            {
                _logger.LogInformation("Relay unavailable ({Error}); polling continues", ex.GetType().Name);
            }
            finally
            {
                _monitor.SetLive(false);
            }

            failures = connected ? 0 : failures + 1;
            var delay = failures == 0 ? TimeSpan.Zero
                : TimeSpan.FromSeconds(Math.Min(MaxBackoff.TotalSeconds, Math.Pow(2, Math.Min(failures, 7)) * (0.8 + (0.4 * Random.Shared.NextDouble()))));
            try
            {
                await _watchlistChanged.WaitAsync(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>One session and stream. Returns true if the stream was established (so backoff resets).</summary>
    private async Task<bool> ConnectOnceAsync(long? lastEventId, Action<long> seen, CancellationToken cancellationToken)
    {
        var ids = _monitor.Repositories.Select(r => r.Key.RepositoryId).ToHashSet();
        Volatile.Write(ref _subscribed, ids);
        if (ids.Count == 0 || await _tokens.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false) is not { } token)
        {
            await _watchlistChanged.WaitAsync(TimeSpan.FromMinutes(5), cancellationToken).ConfigureAwait(false);
            return false;
        }

        var (session, failure) = await _client.CreateSessionAsync(token, ids, cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            _logger.LogInformation("The relay refused a session ({Status}); polling continues", failure);
            return false;
        }

        // End this stream to renew the session shortly before it expires, or when the watchlist changes.
        using var stream = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var renewIn = session.ExpiresAt - _time.GetUtcNow() - RenewBefore;
        stream.CancelAfter(renewIn > TimeSpan.FromSeconds(30) ? renewIn : TimeSpan.FromSeconds(30));
        _ = _watchlistChanged.WaitAsync(stream.Token).ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully)
            {
                stream.Cancel();
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        var established = false;
        try
        {
            await foreach (var message in _client.StreamAsync(session.SessionToken, lastEventId, () =>
            {
                established = true;
                _monitor.SetLive(true);
                _monitor.ReconcileAll(); // events may have been missed while disconnected
            }, stream.Token).ConfigureAwait(false))
            {
                LastEventAt = _time.GetUtcNow();
                if (message.Id is { } id)
                {
                    seen(id);
                    lastEventId = id;
                }

                if (!Handle(message))
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Renewal or watchlist change: reconnect at once with a new session.
            return true;
        }

        return established;
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
                // Access to one repository ended: refreshing its metadata shows "access lost" from GitHub itself.
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
