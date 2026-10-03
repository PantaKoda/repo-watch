using RepoWatch.Core.Accounts;
using RepoWatch.Core.Identity;
using RepoWatch.Core.Monitoring;
using RepoWatch.Core.Settings;
using RepoWatch.Core.State;

namespace RepoWatch.Desktop.Services;

/// <summary>A monitor that can take a changed watchlist (options, order, names, additions) without being rebuilt.</summary>
public interface IWatchlistAwareMonitor : IRepositoryMonitor
{
    AccountKey Account { get; }

    void ApplyWatchlist(AccountSettings settings);
}

/// <summary>
/// Decides what the widget observes from the account state and the watchlist. Only watched
/// repositories are ever handed to a monitor.
/// <list type="bullet">
/// <item>Account or state changes, and removals, replace the monitor (disposing the old one), so
/// work for a removed repository stops immediately.</item>
/// <item>Other watchlist edits (options, order, names, additions) are applied to the current
/// monitor in place, so toggling an option never restarts monitoring.</item>
/// </list>
/// </summary>
public sealed class MonitorCoordinator : IDisposable
{
    private readonly AccountService _accounts;
    private readonly WatchlistService _watchlist;
    private readonly MonitorHost _monitors;
    private (AccountState State, AccountKey? Account, AccountSettings Settings)? _applied;
    private IWatchlistAwareMonitor? _current;

    public MonitorCoordinator(AccountService accounts, WatchlistService watchlist, MonitorHost monitors)
    {
        _accounts = accounts;
        _watchlist = watchlist;
        _monitors = monitors;
        _accounts.Changed += OnAccountChanged;
        _watchlist.Changed += OnWatchlistChanged;
        Apply(removed: false);
    }

    public void Dispose()
    {
        _accounts.Changed -= OnAccountChanged;
        _watchlist.Changed -= OnWatchlistChanged;
    }

    private void OnAccountChanged(object? sender, EventArgs e) => Apply(removed: false);

    private void OnWatchlistChanged(object? sender, WatchlistChangedEventArgs e) => Apply(removed: e.RemovedRepositoryIds.Count > 0);

    private void Apply(bool removed)
    {
        // Account and watchlist events often arrive together; act only when an input changed.
        var inputs = (_accounts.State, _watchlist.Account, _watchlist.Current);
        if (_applied is { } previous && previous.State == inputs.State && previous.Account == inputs.Account
            && ReferenceEquals(previous.Settings, inputs.Current))
        {
            return;
        }

        var sameAccountAndState = _applied is { } last && last.State == inputs.State && last.Account == inputs.Account;
        _applied = inputs;

        if (sameAccountAndState && !removed && _current is not null && _current.Account == inputs.Account)
        {
            _current.ApplyWatchlist(inputs.Current);
            return;
        }

        var watchlistMonitor = _accounts.State switch
        {
            AccountState.SignedIn when _watchlist.Account is { } account => new WatchlistMonitor(ConnectionState.SignedInIdle, account, _watchlist.Current),
            AccountState.Offline when _watchlist.Account is { } account => new WatchlistMonitor(ConnectionState.Offline, account, _watchlist.Current),
            _ => null,
        };

        _current = watchlistMonitor;
        _monitors.SetBase(watchlistMonitor as IRepositoryMonitor ?? _accounts.State switch
        {
            AccountState.Restoring => new StatusOnlyMonitor(ConnectionState.Connecting),
            AccountState.ReconnectRequired => new StatusOnlyMonitor(ConnectionState.ReconnectRequired),
            AccountState.SignedIn => new StatusOnlyMonitor(ConnectionState.SignedInIdle),
            AccountState.Offline => new StatusOnlyMonitor(ConnectionState.Offline),
            _ => new StatusOnlyMonitor(ConnectionState.NotSignedIn),
        });
    }
}

/// <summary>
/// Shows the watched repositories in the user's order without loading anything; every section is
/// "not loaded". Replaced by the polling monitor in Stage 06. Makes no requests.
/// </summary>
public sealed class WatchlistMonitor : IWatchlistAwareMonitor
{
    public WatchlistMonitor(ConnectionState state, AccountKey account, AccountSettings settings)
    {
        State = state;
        Account = account;
        Repositories = Build(settings);
        Ordering = settings.Ordering;
    }

    public ConnectionState State { get; }

    public AccountKey Account { get; }

    public IReadOnlyList<MonitoredRepository> Repositories { get; private set; }

    public RepositoryOrdering Ordering { get; private set; }

    public bool IsRefreshing => false;

    public event EventHandler? Changed;

    public void ApplyWatchlist(AccountSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Repositories = Build(settings);
        Ordering = settings.Ordering;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Task RefreshAsync(RepositoryKey? repository = null, CancellationToken cancellationToken = default) => Task.CompletedTask;

    private List<MonitoredRepository> Build(AccountSettings settings) => settings.Watchlist
        .Select((w, index) => new MonitoredRepository(w, new RepositorySnapshot(new RepositoryKey(Account, w.RepositoryId)), index))
        .ToList();
}
