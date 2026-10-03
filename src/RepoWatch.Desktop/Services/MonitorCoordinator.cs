using RepoWatch.Core.Accounts;
using RepoWatch.Core.Identity;
using RepoWatch.Core.Monitoring;
using RepoWatch.Core.Settings;
using RepoWatch.Core.State;

namespace RepoWatch.Desktop.Services;

/// <summary>
/// Decides what the widget observes from the account state and the watchlist. Rebuilding the
/// monitor on every watchlist change disposes the previous one, so work for a removed repository
/// stops immediately. Only watched repositories are ever handed to a monitor.
/// </summary>
public sealed class MonitorCoordinator : IDisposable
{
    private readonly AccountService _accounts;
    private readonly WatchlistService _watchlist;
    private readonly MonitorHost _monitors;

    public MonitorCoordinator(AccountService accounts, WatchlistService watchlist, MonitorHost monitors)
    {
        _accounts = accounts;
        _watchlist = watchlist;
        _monitors = monitors;
        _accounts.Changed += OnChanged;
        _watchlist.Changed += OnChanged;
        Apply();
    }

    public void Dispose()
    {
        _accounts.Changed -= OnChanged;
        _watchlist.Changed -= OnChanged;
    }

    private void OnChanged(object? sender, EventArgs e) => Apply();

    private (AccountState State, AccountKey? Account, AccountSettings Settings)? _applied;

    private void Apply()
    {
        // Account and watchlist events often arrive together; rebuild only when an input changed.
        var inputs = (_accounts.State, _watchlist.Account, _watchlist.Current);
        if (_applied is { } previous && previous.State == inputs.State && previous.Account == inputs.Account
            && ReferenceEquals(previous.Settings, inputs.Current))
        {
            return;
        }

        _applied = inputs;
        IRepositoryMonitor monitor = _accounts.State switch
        {
            AccountState.Restoring => new StatusOnlyMonitor(ConnectionState.Connecting),
            AccountState.ReconnectRequired => new StatusOnlyMonitor(ConnectionState.ReconnectRequired),
            AccountState.SignedIn when _watchlist.Account is { } account => new WatchlistMonitor(ConnectionState.SignedInIdle, account, _watchlist.Current),
            AccountState.Offline when _watchlist.Account is { } account => new WatchlistMonitor(ConnectionState.Offline, account, _watchlist.Current),
            AccountState.SignedIn => new StatusOnlyMonitor(ConnectionState.SignedInIdle),
            AccountState.Offline => new StatusOnlyMonitor(ConnectionState.Offline),
            _ => new StatusOnlyMonitor(ConnectionState.NotSignedIn),
        };
        _monitors.SetBase(monitor);
    }
}

/// <summary>
/// Shows the watched repositories in the user's order without loading anything; every section is
/// "not loaded". Replaced by the polling monitor in Stage 06. Makes no requests.
/// </summary>
public sealed class WatchlistMonitor(ConnectionState state, AccountKey account, AccountSettings settings) : IRepositoryMonitor
{
    public ConnectionState State { get; } = state;

    public IReadOnlyList<MonitoredRepository> Repositories { get; } = settings.Watchlist
        .Select((w, index) => new MonitoredRepository(w, new RepositorySnapshot(new RepositoryKey(account, w.RepositoryId)), index))
        .ToList();

    public RepositoryOrdering Ordering { get; } = settings.Ordering;

    public bool IsRefreshing => false;

    public event EventHandler? Changed
    {
        add { }
        remove { }
    }

    public Task RefreshAsync(RepositoryKey? repository = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
