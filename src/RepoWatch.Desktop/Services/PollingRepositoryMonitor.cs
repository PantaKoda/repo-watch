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

/// <summary>
/// Polls GitHub for the watched repositories of one account: the single, bounded request scheduler.
/// <list type="bullet">
/// <item>One loop refreshes one repository at a time (no request storms). Duplicate requests coalesce
/// into one due time per part.</item>
/// <item>Each part (metadata, Actions, pull requests, issues) has its own interval; active work and the
/// repository whose details are open come first and refresh sooner.</item>
/// <item>Polling slows down when the widget is hidden, on battery, or when GitHub's request budget runs
/// low; failures back off exponentially with jitter; a rate limit pauses everything until its reset.</item>
/// <item>Pause monitoring stops all requests. Waking up or the network returning refreshes promptly.</item>
/// <item>The last good snapshots are restored from the local cache at start (labeled "Cached") and saved
/// after each refresh. A failure keeps values; access loss withholds them.</item>
/// </list>
/// Only watched repositories are requested; results for removed repositories or superseded options are
/// discarded. Changes are published through <see cref="Changed"/> from the polling thread.
/// </summary>
public sealed class PollingRepositoryMonitor : IWatchlistAwareMonitor, IDisposable
{
    private static readonly RefreshParts[] Parts = [RefreshParts.Metadata, RefreshParts.Actions, RefreshParts.PullRequests, RefreshParts.Issues];

    /// <summary>Parts due within this window are refreshed together, saving a separate pass.</summary>
    private static readonly TimeSpan BatchWindow = TimeSpan.FromSeconds(10);

    private readonly IRepositoryDataSource _source;
    private readonly string _login;
    private readonly TimeProvider _time;
    private readonly PollingIntervals _intervals;
    private readonly ILogger _logger;
    private readonly PollingConditions? _conditions;
    private readonly Storage.RepositoryCache? _cache;
    private readonly RateBudget? _budget;
    private readonly Func<double> _random;
    private readonly CancellationTokenSource _stop;
    private readonly object _gate = new();
    private readonly Dictionary<long, Entry> _entries = [];
    private readonly SemaphoreSlim _wake = new(0);
    private int _wakeVersion;
    private List<long> _order = [];
    private RepositoryOrdering _ordering;
    private RepositoryKey? _focus;
    private DateTimeOffset? _pausedUntil;
    private bool _refreshing;
    private bool _disposed;

    public PollingRepositoryMonitor(AccountKey account, AccountSettings settings, IRepositoryDataSource source, string login,
        TimeProvider time, ILogger logger, CancellationToken lifetime, PollingIntervals? intervals = null,
        PollingConditions? conditions = null, Storage.RepositoryCache? cache = null, RateBudget? budget = null, Func<double>? random = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Account = account;
        _source = source;
        _login = login;
        _time = time;
        _logger = logger;
        _intervals = intervals ?? new PollingIntervals();
        _conditions = conditions;
        _cache = cache;
        _budget = budget;
        _random = random ?? Random.Shared.NextDouble;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        ApplyWatchlistCore(settings);
        RestoreFromCache(settings);
        if (_conditions is not null)
        {
            _conditions.Changed += OnConditionsChanged;
            _conditions.Resumed += OnResumed;
        }

        State = CurrentState();
        _ = Task.Run(() => RunAsync(_stop.Token));
    }

    public AccountKey Account { get; }

    public ConnectionState State { get; private set; }

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

        Signal();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The repository whose details are open refreshes first and more often.</summary>
    public void SetFocus(RepositoryKey? repository)
    {
        lock (_gate)
        {
            if (_focus == repository)
            {
                return;
            }

            _focus = repository;
            if (repository is not null && _entries.TryGetValue(repository.RepositoryId, out var entry))
            {
                // Opening details shows the newest Actions and pull requests promptly.
                entry.Due[RefreshParts.Actions] = Min(entry.Due[RefreshParts.Actions], _time.GetUtcNow());
                entry.Due[RefreshParts.PullRequests] = Min(entry.Due[RefreshParts.PullRequests], _time.GetUtcNow());
            }
        }

        Signal();
    }

    /// <summary>Refreshes now (one repository, or all). Completes when that refresh has finished.</summary>
    public Task RefreshAsync(RepositoryKey? repository = null, CancellationToken cancellationToken = default)
    {
        var waits = new List<Task>();
        lock (_gate)
        {
            // While paused or rate limited, requesting now would do nothing (or hit the limit again):
            // answer at once; the widget already shows why.
            if (IsPausedByUser || (_pausedUntil is { } until && until > _time.GetUtcNow()))
            {
                return Task.CompletedTask;
            }

            foreach (var entry in _entries.Values.Where(e => repository is null || e.Key == repository))
            {
                entry.MarkAllDue();
                entry.Failures = 0;
                entry.Waiters ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                waits.Add(entry.Waiters.Task);
            }
        }

        Signal();
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

        if (_conditions is not null)
        {
            _conditions.Changed -= OnConditionsChanged;
            _conditions.Resumed -= OnResumed;
        }

        _stop.Cancel();
        _stop.Dispose();
    }

    private bool IsPausedByUser => _conditions?.IsPaused == true;

    private void ApplyWatchlistCore(AccountSettings settings)
    {
        _ordering = settings.Ordering;
        var ids = new List<long>();
        foreach (var watch in settings.Watchlist)
        {
            ids.Add(watch.RepositoryId);
            if (!_entries.TryGetValue(watch.RepositoryId, out var entry))
            {
                _entries[watch.RepositoryId] = new Entry(new RepositoryKey(Account, watch.RepositoryId), watch);
            }
            else if (entry.Watch != watch)
            {
                // Options changed (scope, branches, workflows, issues): reload with the new options.
                entry.Watch = watch;
                entry.MarkAllDue();
            }
        }

        // Removals normally rebuild the monitor; drop them here too so they are never refreshed.
        foreach (var removed in _entries.Keys.Except(ids).ToList())
        {
            _entries[removed].Waiters?.TrySetResult();
            _entries.Remove(removed);
            _cache?.Delete(new RepositoryKey(Account, removed));
        }

        _order = ids;
    }

    private void RestoreFromCache(AccountSettings settings)
    {
        if (_cache is null)
        {
            return;
        }

        _cache.Prune(Account, settings.Watchlist.Select(w => w.RepositoryId).ToList(), TimeSpan.FromDays(30));
        var cached = _cache.Load(Account);
        lock (_gate)
        {
            foreach (var (id, snapshot) in cached)
            {
                if (_entries.TryGetValue(id, out var entry))
                {
                    entry.Snapshot = snapshot; // shown as "Cached" until the first refresh confirms it
                }
            }
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // Read before deciding, so a wake that arrives while deciding is never lost.
                var version = Volatile.Read(ref _wakeVersion);
                var now = _time.GetUtcNow();
                Entry? next = null;
                var parts = RefreshParts.None;
                TimeSpan wait;
                lock (_gate)
                {
                    if (IsPausedByUser)
                    {
                        wait = TimeSpan.FromHours(1); // until resumed (Changed wakes the loop)
                    }
                    else if (_pausedUntil is { } until && until > now)
                    {
                        wait = until - now;
                    }
                    else
                    {
                        next = _order.Select(id => _entries[id])
                            .Where(e => !e.InFlight && e.NextDue <= now)
                            .OrderByDescending(e => e.Key == _focus)
                            .ThenByDescending(e => RepositoryRefresh.IsActive(e.Snapshot))
                            .ThenBy(e => e.NextDue)
                            .FirstOrDefault();
                        if (next is not null)
                        {
                            parts = next.DueParts(now + BatchWindow);
                            next.Begin();
                        }

                        var nextDue = _entries.Values.Where(e => !e.InFlight).Select(e => e.NextDue).DefaultIfEmpty(now + _intervals.Quiet).Min();
                        wait = nextDue - now;
                    }
                }

                UpdateState();
                if (next is null)
                {
                    SetRefreshing(false);
                    await WaitAsync(Clamp(wait), version, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                SetRefreshing(true);
                await RefreshEntryAsync(next, parts, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Disposed: signed out, account changed, or the watchlist lost a repository.
        }
    }

    /// <summary>Wakes the loop: something became due, or conditions changed.</summary>
    private void Signal()
    {
        Interlocked.Increment(ref _wakeVersion);
        _wake.Release();
    }

    private async Task WaitAsync(TimeSpan wait, int version, CancellationToken cancellationToken)
    {
        // Drain extra wake signals so one burst of edits causes one pass...
        while (_wake.CurrentCount > 0 && _wake.Wait(0))
        {
        }

        // ...but a signal since the decision means there may be new work: decide again now.
        if (Volatile.Read(ref _wakeVersion) != version)
        {
            return;
        }

        await _wake.WaitAsync(wait, cancellationToken).ConfigureAwait(false);
    }

    private async Task RefreshEntryAsync(Entry entry, RefreshParts parts, CancellationToken cancellationToken)
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
        }

        try
        {
            RepositorySnapshot refreshed;
            try
            {
                refreshed = await RepositoryRefresh.RunAsync(_source, snapshot, watch, _login, _time, cancellationToken,
                    update => Publish(entry, watch, update), parts).ConfigureAwait(false); // each section appears as it loads
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // Never let one unexpected failure end polling: record it, keep cached values, retry later.
                _logger.LogError(ex, "Refreshing repository {RepositoryId} failed unexpectedly", entry.Key.RepositoryId);
                refreshed = RepositoryRefresh.FailedAll(snapshot, new ResourceError(ResourceErrorKind.Unknown,
                    $"Repo Watch hit an unexpected problem ({ex.GetType().Name}); it will try again.", _time.GetUtcNow()));
            }

            if (Publish(entry, watch, refreshed))
            {
                _cache?.Save(refreshed);
            }

            Schedule(entry, parts, refreshed);
            UpdateState();
        }
        finally
        {
            waiters?.TrySetResult();
        }
    }

    private void Schedule(Entry entry, RefreshParts parts, RepositorySnapshot refreshed)
    {
        var now = _time.GetUtcNow();
        var error = ErrorOf(refreshed, parts);
        lock (_gate)
        {
            entry.InFlight = false;
            var transient = error is { Kind: not (ResourceErrorKind.NotFound or ResourceErrorKind.Forbidden or ResourceErrorKind.SsoRequired) };
            entry.Failures = transient ? entry.Failures + 1 : 0;
            entry.LastOffline = error?.Kind is ResourceErrorKind.Network or ResourceErrorKind.Timeout;

            var slowdown = PollingPolicy.Slowdown(_conditions?.IsWidgetVisible ?? true, _conditions?.IsOnBattery ?? false, _budget?.IsLow ?? false);
            var active = RepositoryRefresh.IsActive(refreshed);
            var focused = entry.Key == _focus;
            foreach (var part in Parts.Where(p => parts.HasFlag(p)))
            {
                if (entry.Requeued.HasFlag(part))
                {
                    continue; // requested again (or options changed) while this refresh ran: stays due now
                }

                entry.Due[part] = now + (transient
                    ? PollingPolicy.Backoff(entry.Failures, _intervals, _random())
                    : PollingPolicy.Scale(PollingPolicy.Interval(part, active, focused, _intervals), slowdown));
            }

            entry.Requeued = RefreshParts.None;

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
        }
    }

    /// <summary>The error from the parts just refreshed that should drive the next time: rate limits, then connectivity.</summary>
    private static ResourceError? ErrorOf(RepositorySnapshot snapshot, RefreshParts parts)
    {
        var errors = new List<ResourceError?> { snapshot.Metadata.LastError };
        if (parts.HasFlag(RefreshParts.Actions))
        {
            errors.Add(snapshot.Actions.LastError);
        }

        if (parts.HasFlag(RefreshParts.PullRequests))
        {
            errors.Add(snapshot.PullRequests.LastError);
        }

        if (parts.HasFlag(RefreshParts.Issues))
        {
            errors.Add(snapshot.Issues.LastError);
        }

        var known = errors.OfType<ResourceError>().ToList();
        return known.FirstOrDefault(e => e.Kind == ResourceErrorKind.RateLimited)
            ?? known.FirstOrDefault(e => e.Kind is ResourceErrorKind.Network or ResourceErrorKind.Timeout)
            ?? known.FirstOrDefault();
    }

    /// <summary>Applies a result unless the repository was removed, its options changed meanwhile, or the monitor was disposed.</summary>
    private bool Publish(Entry entry, WatchedRepository watch, RepositorySnapshot snapshot)
    {
        lock (_gate)
        {
            if (_disposed || !_entries.TryGetValue(entry.Key.RepositoryId, out var current) || !ReferenceEquals(current, entry) || current.Watch != watch)
            {
                return false;
            }

            entry.Snapshot = snapshot;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private ConnectionState CurrentState()
    {
        lock (_gate)
        {
            return IsPausedByUser ? ConnectionState.Paused
                : _entries.Count > 0 && _entries.Values.All(e => e.LastOffline) ? ConnectionState.Offline
                : ConnectionState.Polling;
        }
    }

    private void UpdateState()
    {
        var state = CurrentState();
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

    private void OnConditionsChanged(object? sender, EventArgs e)
    {
        if (IsPausedByUser)
        {
            lock (_gate)
            {
                foreach (var entry in _entries.Values)
                {
                    entry.Waiters?.TrySetResult();
                    entry.Waiters = null;
                }
            }
        }

        UpdateState();
        Signal();
    }

    /// <summary>Woke from sleep or the network returned: earlier failures no longer predict anything.</summary>
    private void OnResumed(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            foreach (var entry in _entries.Values)
            {
                entry.MarkAllDue();
                entry.Failures = 0;
            }
        }

        Signal();
    }

    private static TimeSpan Clamp(TimeSpan wait) =>
        wait < TimeSpan.Zero ? TimeSpan.Zero : wait > TimeSpan.FromHours(1) ? TimeSpan.FromHours(1) : wait;

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    private sealed class Entry(RepositoryKey key, WatchedRepository watch)
    {
        public RepositoryKey Key { get; } = key;

        public WatchedRepository Watch { get; set; } = watch;

        public RepositorySnapshot Snapshot { get; set; } = new(key);

        /// <summary>When each part is next due; everything is due at once for a new entry.</summary>
        public Dictionary<RefreshParts, DateTimeOffset> Due { get; } = Parts.ToDictionary(p => p, _ => DateTimeOffset.MinValue);

        public DateTimeOffset NextDue => Due.Values.Min();

        public bool InFlight { get; set; }

        /// <summary>Parts requested again while a refresh was in flight.</summary>
        public RefreshParts Requeued { get; set; }

        public int Failures { get; set; }

        public TaskCompletionSource? Waiters { get; set; }

        public bool LastOffline { get; set; }

        public RefreshParts DueParts(DateTimeOffset before) =>
            Parts.Where(p => Due[p] <= before).Aggregate(RefreshParts.None, (all, p) => all | p);

        public void Begin()
        {
            InFlight = true;
            Requeued = RefreshParts.None;
        }

        public void MarkAllDue()
        {
            foreach (var part in Parts)
            {
                Due[part] = DateTimeOffset.MinValue;
            }

            if (InFlight)
            {
                Requeued = RefreshParts.All;
            }
        }
    }
}