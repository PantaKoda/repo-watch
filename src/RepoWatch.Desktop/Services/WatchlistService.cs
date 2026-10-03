using RepoWatch.Core.Access;
using RepoWatch.Core.Accounts;
using RepoWatch.Core.Identity;
using RepoWatch.Core.Settings;

namespace RepoWatch.Desktop.Services;

public sealed class WatchlistChangedEventArgs(AccountSettings previous, AccountSettings current) : EventArgs
{
    public AccountSettings Previous { get; } = previous;

    public AccountSettings Current { get; } = current;

    public IReadOnlyList<long> RemovedRepositoryIds =>
        Previous.Watchlist.Select(w => w.RepositoryId).Except(Current.Watchlist.Select(w => w.RepositoryId)).ToList();
}

/// <summary>
/// The signed-in account's watchlist and ordering. Changes are saved immediately and raise
/// <see cref="Changed"/>, so monitoring stops for a removed repository right away. Choosing or
/// removing repositories here never changes what the GitHub App may access.
/// </summary>
public sealed class WatchlistService : IDisposable
{
    private readonly SettingsService _settings;
    private readonly AccountService _accounts;

    public WatchlistService(SettingsService settings, AccountService accounts)
    {
        _settings = settings;
        _accounts = accounts;
        _accounts.Changed += OnAccountChanged;
        Reload();
    }

    /// <summary>The account whose watchlist is shown; null when signed out.</summary>
    public AccountKey? Account { get; private set; }

    public AccountSettings Current { get; private set; } = SettingsCodecs.Account.CreateDefault();

    public IReadOnlyList<WatchedRepository> Repositories => Current.Watchlist;

    public bool IsWatched(long repositoryId) => Current.Watchlist.Any(w => w.RepositoryId == repositoryId);

    /// <summary>Raised on the UI thread after the watchlist or ordering changed, or the account changed.</summary>
    public event EventHandler<WatchlistChangedEventArgs>? Changed;

    public void Add(IEnumerable<AccessibleRepository> repositories) => Change(s => s with { Watchlist = Watchlist.Add(s.Watchlist, repositories) });

    public void Remove(IEnumerable<long> repositoryIds) => Change(s => s with { Watchlist = Watchlist.Remove(s.Watchlist, repositoryIds) });

    public void Move(long repositoryId, int offset) => Change(s => s with { Watchlist = Watchlist.Move(s.Watchlist, repositoryId, offset) });

    public void UpdateRepository(long repositoryId, Func<WatchedRepository, WatchedRepository> change) =>
        Change(s => s with { Watchlist = Watchlist.Update(s.Watchlist, repositoryId, change) });

    public void SetOrdering(RepositoryOrdering ordering) => Change(s => s with { Ordering = ordering });

    /// <summary>Updates last-known names after renames or transfers.</summary>
    public void ApplyCatalog(AccessCatalog catalog)
    {
        var refreshed = Watchlist.RefreshNames(Current.Watchlist, catalog);
        if (!refreshed.SequenceEqual(Current.Watchlist))
        {
            Change(s => s with { Watchlist = Watchlist.RefreshNames(s.Watchlist, catalog) });
        }
    }

    public void Dispose() => _accounts.Changed -= OnAccountChanged;

    private void Change(Func<AccountSettings, AccountSettings> change)
    {
        if (Account is not { } account)
        {
            return;
        }

        var previous = Current;
        _settings.UpdateAccount(account, change);
        Current = _settings.GetAccount(account);
        Changed?.Invoke(this, new WatchlistChangedEventArgs(previous, Current));
    }

    private void OnAccountChanged(object? sender, EventArgs e)
    {
        var account = _accounts.State is AccountState.SignedIn or AccountState.Offline or AccountState.Restoring or AccountState.ReconnectRequired
            ? _accounts.Identity?.Account
            : null;
        if (account != Account)
        {
            Reload();
        }
    }

    private void Reload()
    {
        var previous = Current;
        Account = _accounts.State == AccountState.SignedOut ? null : _accounts.Identity?.Account;
        Current = Account is { } account ? _settings.GetAccount(account) : SettingsCodecs.Account.CreateDefault();
        Changed?.Invoke(this, new WatchlistChangedEventArgs(previous, Current));
    }
}
