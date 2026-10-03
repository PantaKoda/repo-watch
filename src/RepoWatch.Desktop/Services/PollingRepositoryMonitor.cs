using Microsoft.Extensions.Logging;
using RepoWatch.Core.Identity;
using RepoWatch.Core.Monitoring;
using RepoWatch.Core.Settings;
using RepoWatch.Core.State;
using RepoWatch.GitHub.Api;
using RepoWatch.GitHub.Repositories;

namespace RepoWatch.Desktop.Services;

/// <summary>Creates the monitor for a signed-in account; null when no GitHub session is available.</summary>
public interface IRepositoryMonitorFactory
{
    IWatchlistAwareMonitor? Create(AccountKey account, AccountSettings settings);
}

/// <summary>What one refresh of one repository needs from GitHub. Implemented by <see cref="RepositoryDataClient"/>.</summary>
public interface IRepositoryDataSource
{
    Task<ApiResult<RepositoryInfo>> GetRepositoryAsync(RepositoryKey key, CancellationToken cancellationToken);

    Task<SectionResult<Core.Actions.ActionsState>> GetActionsAsync(ActionsRequest request, CancellationToken cancellationToken);

    Task<SectionResult<Core.PullRequests.PullRequestsState>> GetPullRequestsAsync(string owner, string name, bool mineOnly, string login, DateTimeOffset now, CancellationToken cancellationToken);

    Task<SectionResult<Core.Issues.IssuesState>> GetIssuesAsync(string owner, string name, CancellationToken cancellationToken);
}

internal sealed class RepositoryDataSource(RepositoryDataClient client) : IRepositoryDataSource
{
    public Task<ApiResult<RepositoryInfo>> GetRepositoryAsync(RepositoryKey key, CancellationToken cancellationToken) => client.GetRepositoryAsync(key, cancellationToken);

    public Task<SectionResult<Core.Actions.ActionsState>> GetActionsAsync(ActionsRequest request, CancellationToken cancellationToken) => client.GetActionsAsync(request, cancellationToken);

    public Task<SectionResult<Core.PullRequests.PullRequestsState>> GetPullRequestsAsync(string owner, string name, bool mineOnly, string login, DateTimeOffset now, CancellationToken cancellationToken) =>
        client.GetPullRequestsAsync(owner, name, mineOnly, login, now, cancellationToken);

    public Task<SectionResult<Core.Issues.IssuesState>> GetIssuesAsync(string owner, string name, CancellationToken cancellationToken) => client.GetIssuesAsync(owner, name, cancellationToken);
}

/// <summary>Refresh intervals. Targets only: a rate limit or Retry-After always wins.</summary>
public sealed record PollingIntervals
{
    /// <summary>A tracked branch or a listed run is queued or running.</summary>
    public TimeSpan Active { get; init; } = TimeSpan.FromSeconds(20);

    public TimeSpan Normal { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>After a failed refresh (network, server error).</summary>
    public TimeSpan Failed { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Longest wait for a rate-limit reset before trying again.</summary>
    public TimeSpan MaxRateLimitWait { get; init; } = TimeSpan.FromHours(1);
}

/// <summary>
/// Polls GitHub for the watched repositories of one account. One background loop refreshes one
/// repository at a time (no request storms), each section independently: a failing endpoint keeps
/// the other sections' data and its own last good value. Only watched repositories are requested;
/// a removed repository is never refreshed again because the coordinator replaces the monitor.
/// Changes are published through <see cref="Changed"/> from the polling thread.
/// </summary>
public sealed class PollingRepositoryMonitor : IWatchlistAwareMonitor, IDisposable
{
    private readonly IRepositoryDataSource _source;
    private readonly string _login;
    private readonly TimeProvider _time;
    private readonly PollingIntervals _intervals;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stop;
    private readonly object _gate = new();
    private readonly Dictionary<long, Entry> _entries = [];
    private readonly SemaphoreSlim _wake = new(0);
    private List<long> _order = [];
    private RepositoryOrdering _ordering;
    private DateTimeOffset? _pausedUntil;
    private bool _refreshing;
    private bool _disposed;

    public PollingRepositoryMonitor(AccountKey account, AccountSettings settings, IRepositoryDataSource source, string login,
        TimeProvider time, ILogger logger, CancellationToken lifetime, PollingIntervals? intervals = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Account = account;
        _source = source;
        _login = login;
        _time = time;
        _logger = logger;
        _intervals = intervals ?? new PollingIntervals();
        _stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        ApplyWatchlistCore(settings);
        _ = Task.Run(() => RunAsync(_stop.Token));
    }

    public AccountKey Account { get; }

    public ConnectionState State { get; private set; } = ConnectionState.Polling;

    public RepositoryOrdering Ordering
    {
        get
        {
            lock (_gate)
            {
                return _ordering;
            }
        }
    }

    public IReadOnlyList<MonitoredRepository> Repositories
    {
        get
        {
            lock (_gate)
            {
                return _order.Select((id, index) => new MonitoredRepository(_entries[id].Watch, _entries[id].Snapshot, index)).ToList();
            }
        }
    }

    public bool IsRefreshing
    {
        get
        {
            lock (_gate)
            {
                return _refreshing;
            }
        }
    }

    public event EventHandler? Changed;

    public void ApplyWatchlist(AccountSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
        {
            ApplyWatchlistCore(settings);
        }

        _wake.Release();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Refreshes now (one repository, or all). Completes when that refresh has finished.</summary>
    public Task RefreshAsync(RepositoryKey? repository = null, CancellationToken cancellationToken = default)
    {
        var waits = new List<Task>();
        lock (_gate)
        {
            // While rate limited, requesting now would only hit the limit again: answer at once (the
            // sections already say when polling resumes) instead of leaving the command waiting.
            if (_pausedUntil is { } until && until > _time.GetUtcNow())
            {
                return Task.CompletedTask;
            }

            foreach (var entry in _entries.Values.Where(e => repository is null || e.Key == repository))
            {
                entry.DueAt = DateTimeOffset.MinValue;
                entry.Waiters ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                waits.Add(entry.Waiters.Task);
            }
        }

        _wake.Release();
        return Task.WhenAll(waits).WaitAsync(cancellationToken);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var entry in _entries.Values)
            {
                entry.Waiters?.TrySetResult();
            }
        }

        _stop.Cancel();
        _stop.Dispose();
    }

    private void ApplyWatchlistCore(AccountSettings settings)
    {
        _ordering = settings.Ordering;
        var ids = new List<long>();
        foreach (var watch in settings.Watchlist)
        {
            ids.Add(watch.RepositoryId);
            if (!_entries.TryGetValue(watch.RepositoryId, out var entry))
            {
                var key = new RepositoryKey(Account, watch.RepositoryId);
                _entries[watch.RepositoryId] = new Entry(key, watch) { DueAt = DateTimeOffset.MinValue };
            }
            else if (entry.Watch != watch)
            {
                // Options changed (scope, branches, workflows, issues): reload with the new options.
                entry.Watch = watch;
                entry.DueAt = DateTimeOffset.MinValue;
            }
        }

        // Removals normally rebuild the monitor; drop them here too so they are never refreshed.
        foreach (var removed in _entries.Keys.Except(ids).ToList())
        {
            _entries[removed].Waiters?.TrySetResult();
            _entries.Remove(removed);
        }

        _order = ids;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var now = _time.GetUtcNow();
                Entry? next;
                TimeSpan wait;
                lock (_gate)
                {
                    var paused = _pausedUntil is { } until && until > now;
                    next = paused ? null : _order.Select(id => _entries[id]).Where(e => e.DueAt <= now).MinBy(e => e.DueAt);
                    var nextDue = paused ? _pausedUntil!.Value : _entries.Values.Select(e => e.DueAt).DefaultIfEmpty(now + _intervals.Normal).Min();
                    wait = next is null ? Clamp(nextDue - now) : TimeSpan.Zero;
                }

                if (next is null)
                {
                    SetRefreshing(false);
                    await WaitAsync(wait, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                SetRefreshing(true);
                await RefreshEntryAsync(next, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Disposed: signed out, account changed, or the watchlist lost a repository.
        }
    }

    private async Task WaitAsync(TimeSpan wait, CancellationToken cancellationToken)
    {
        // Drain extra wake signals so one burst of edits causes one pass.
        while (_wake.CurrentCount > 0 && _wake.Wait(0))
        {
        }

        await _wake.WaitAsync(wait, cancellationToken).ConfigureAwait(false);
    }

    private async Task RefreshEntryAsync(Entry entry, CancellationToken cancellationToken)
    {
        WatchedRepository watch;
        RepositorySnapshot snapshot;
        TaskCompletionSource? waiters;
        lock (_gate)
        {
            watch = entry.Watch;
            snapshot = entry.Snapshot;
            waiters = entry.Waiters;
            entry.Waiters = null;
            entry.DueAt = DateTimeOffset.MaxValue; // not picked again while in flight
        }

        try
        {
            RepositorySnapshot refreshed;
            try
            {
                refreshed = await RepositoryRefresh.RunAsync(_source, snapshot, watch, _login, _time, cancellationToken, update =>
                {
                    Publish(entry, watch, update); // partial progress: each section appears as it loads
                }).ConfigureAwait(false);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // Never let one unexpected failure end polling: record it, keep cached values, retry later.
                _logger.LogError(ex, "Refreshing repository {RepositoryId} failed unexpectedly", entry.Key.RepositoryId);
                refreshed = RepositoryRefresh.FailedAll(snapshot, new ResourceError(ResourceErrorKind.Unknown,
                    $"Repo Watch hit an unexpected problem ({ex.GetType().Name}); it will try again.", _time.GetUtcNow()));
            }

            Publish(entry, watch, refreshed);
            var now = _time.GetUtcNow();
            var error = RepositoryRefresh.WorstError(refreshed);
            lock (_gate)
            {
                // A refresh requested (or options changed) while this one ran stays due now.
                if (entry.DueAt == DateTimeOffset.MaxValue)
                {
                    entry.DueAt = now + (RepositoryRefresh.IsActive(refreshed) ? _intervals.Active
                        : error is { Kind: not (ResourceErrorKind.NotFound or ResourceErrorKind.Forbidden) } ? _intervals.Failed
                        : _intervals.Normal);
                }
                if (error is { Kind: ResourceErrorKind.RateLimited } limited)
                {
                    var until = limited.RetryAt is { } retry && retry > now ? retry : now + _intervals.Failed;
                    _pausedUntil = until - now > _intervals.MaxRateLimitWait ? now + _intervals.MaxRateLimitWait : until;
                    _logger.LogWarning("GitHub rate limit reached; polling paused until {Until:u}", _pausedUntil);
                    foreach (var other in _entries.Values)
                    {
                        other.Waiters?.TrySetResult(); // nothing more will happen before the reset
                        other.Waiters = null;
                    }
                }

                entry.LastOffline = error?.Kind is ResourceErrorKind.Network or ResourceErrorKind.Timeout;
            }

            UpdateState();
        }
        finally
        {
            waiters?.TrySetResult();
        }
    }

    private void Publish(Entry entry, WatchedRepository watch, RepositorySnapshot snapshot)
    {
        lock (_gate)
        {
            // Ignore results for a repository that was removed, or whose options changed mid-refresh.
            if (_disposed || !_entries.TryGetValue(entry.Key.RepositoryId, out var current) || !ReferenceEquals(current, entry) || current.Watch != watch)
            {
                return;
            }

            entry.Snapshot = snapshot;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateState()
    {
        ConnectionState state;
        lock (_gate)
        {
            state = _entries.Count > 0 && _entries.Values.All(e => e.LastOffline) ? ConnectionState.Offline : ConnectionState.Polling;
        }

        if (state != State)
        {
            State = state;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void SetRefreshing(bool value)
    {
        lock (_gate)
        {
            if (_refreshing == value)
            {
                return;
            }

            _refreshing = value;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static TimeSpan Clamp(TimeSpan wait) =>
        wait < TimeSpan.Zero ? TimeSpan.Zero : wait > TimeSpan.FromHours(1) ? TimeSpan.FromHours(1) : wait;

    private sealed class Entry(RepositoryKey key, WatchedRepository watch)
    {
        public RepositoryKey Key { get; } = key;

        public WatchedRepository Watch { get; set; } = watch;

        public RepositorySnapshot Snapshot { get; set; } = new(key);

        public DateTimeOffset DueAt { get; set; }

        public TaskCompletionSource? Waiters { get; set; }

        public bool LastOffline { get; set; }
    }
}
