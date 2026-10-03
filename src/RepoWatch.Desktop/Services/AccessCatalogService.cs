using Microsoft.Extensions.Logging;
using RepoWatch.Core.Access;
using RepoWatch.Core.Actions;
using RepoWatch.Core.Accounts;
using RepoWatch.Core.Identity;
using RepoWatch.Core.State;
using RepoWatch.Desktop.Presentation;
using RepoWatch.GitHub;
using RepoWatch.GitHub.Access;
using RepoWatch.GitHub.Api;

namespace RepoWatch.Desktop.Services;

public enum CatalogStatus
{
    NotLoaded,
    Loading,
    Loaded,
    Failed,
}

/// <summary>
/// What GitHub currently grants Repo Watch for the signed-in account. Loaded on request (opening
/// the picker, onboarding, Refresh list), never on a timer, and discarded when the account changes.
/// </summary>
public sealed class AccessCatalogService : IDisposable
{
    private readonly AccountService _accounts;
    private readonly WatchlistService _watchlist;
    private readonly HttpClient _http;
    private readonly GitHubEndpoints _endpoints;
    private readonly IUiDispatcher _dispatcher;
    private readonly TimeProvider _time;
    private readonly ILogger<AccessCatalogService> _logger;
    private AccountKey? _account;
    private Task? _loading;
    private int _generation;

    public AccessCatalogService(AccountService accounts, WatchlistService watchlist, HttpClient http, GitHubEndpoints endpoints,
        IUiDispatcher dispatcher, TimeProvider time, ILogger<AccessCatalogService> logger)
    {
        _accounts = accounts;
        _watchlist = watchlist;
        _http = http;
        _endpoints = endpoints;
        _dispatcher = dispatcher;
        _time = time;
        _logger = logger;
        _accounts.Changed += OnAccountChanged;
    }

    public CatalogStatus Status { get; private set; }

    public AccessCatalog? Catalog { get; private set; }

    public ResourceError? Error { get; private set; }

    /// <summary>Where users install the app or change which repositories it may access.</summary>
    public Uri? InstallationUrl => _endpoints.Installation;

    /// <summary>Raised on the UI thread.</summary>
    public event EventHandler? Changed;

    /// <summary>Loads (or reloads) the catalog. Concurrent requests share one load.</summary>
    public Task RefreshAsync()
    {
        if (_loading is { IsCompleted: false } loading)
        {
            return loading;
        }

        return _loading = LoadAsync();
    }

    /// <summary>Loads once if nothing is loaded yet.</summary>
    public Task EnsureLoadedAsync() => Status is CatalogStatus.NotLoaded or CatalogStatus.Failed ? RefreshAsync() : _loading ?? Task.CompletedTask;

    /// <summary>
    /// Workflows of one repository. Runs on the session's lifetime, so signing out or switching
    /// accounts cancels it; never throws for cancellation or network problems.
    /// </summary>
    public async Task<ApiResult<PagedList<Workflow>>> ListWorkflowsAsync(string owner, string name, CancellationToken cancellationToken)
    {
        var session = _accounts.Session;
        if (session is null || CreateClient() is not { } client)
        {
            return ApiResult<PagedList<Workflow>>.Fail(new ResourceError(ResourceErrorKind.Unauthorized, "Sign in to GitHub first.", _time.GetUtcNow()));
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.Lifetime);
        try
        {
            return await client.ListWorkflowsAsync(owner, name, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return ApiResult<PagedList<Workflow>>.Fail(new ResourceError(ResourceErrorKind.Unauthorized, "Cancelled because you signed out.", _time.GetUtcNow()));
        }
    }

    public void Dispose() => _accounts.Changed -= OnAccountChanged;

    private async Task LoadAsync()
    {
        var generation = _generation;
        var session = _accounts.Session;
        if (CreateClient() is not { } client || session is null)
        {
            Publish(generation, CatalogStatus.Failed, null, new ResourceError(ResourceErrorKind.Unauthorized, "Sign in to GitHub first.", _time.GetUtcNow()));
            return;
        }

        Publish(generation, CatalogStatus.Loading, Catalog, null);
        try
        {
            var result = await client.LoadAsync(session.Lifetime).ConfigureAwait(false);
            if (result.IsSuccess)
            {
                _dispatcher.Post(() =>
                {
                    if (generation == _generation)
                    {
                        _watchlist.ApplyCatalog(result.Value!);
                    }
                });
                Publish(generation, CatalogStatus.Loaded, result.Value, null);
            }
            else
            {
                Publish(generation, CatalogStatus.Failed, Catalog, result.Error);
            }
        }
        catch (OperationCanceledException)
        {
            // Signed out or account changed: results are discarded.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading accessible repositories failed");
            Publish(generation, CatalogStatus.Failed, Catalog, new ResourceError(ResourceErrorKind.Unknown, ex.Message, _time.GetUtcNow()));
        }
    }

    private AccessCatalogClient? CreateClient() => _accounts.Session is { } session
        ? new AccessCatalogClient(new GitHubApiClient(_http, _endpoints, session, _time), _time)
        : null;

    private void Publish(int generation, CatalogStatus status, AccessCatalog? catalog, ResourceError? error) => _dispatcher.Post(() =>
    {
        if (generation != _generation)
        {
            return; // a different account's result
        }

        Status = status;
        Catalog = catalog;
        Error = error;
        Changed?.Invoke(this, EventArgs.Empty);
    });

    private void OnAccountChanged(object? sender, EventArgs e)
    {
        var account = _accounts.State is AccountState.SignedIn or AccountState.Offline ? _accounts.Identity?.Account : null;
        if (account == _account)
        {
            return;
        }

        _account = account;
        Interlocked.Increment(ref _generation);
        _loading = null;
        Status = CatalogStatus.NotLoaded;
        Catalog = null;
        Error = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
