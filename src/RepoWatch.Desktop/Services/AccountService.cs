using Microsoft.Extensions.Logging;
using RepoWatch.Core.Accounts;
using RepoWatch.Core.Configuration;
using RepoWatch.Core.Identity;
using RepoWatch.Core.Monitoring;
using RepoWatch.Desktop.Platform;
using RepoWatch.Desktop.Presentation;
using RepoWatch.GitHub;
using RepoWatch.GitHub.Auth;
using RepoWatch.GitHub.Users;

namespace RepoWatch.Desktop.Services;

public enum SignInOutcome
{
    SignedIn,
    Denied,
    Expired,
    Cancelled,
    Failed,
}

public sealed record SignInResult(SignInOutcome Outcome, string? Message = null);

/// <summary>
/// The single active GitHub account: restoring it at startup, signing in with the device flow,
/// signing out, and turning a rejected session into a visible reconnect state. Settings and cached
/// data are keyed by host + user ID; tokens live only in the credential store.
/// </summary>
public sealed class AccountService : IDisposable
{
    private readonly RepoWatchOptions _options;
    private readonly GitHubEndpoints _endpoints;
    private readonly HttpClient _http;
    private readonly SettingsService _settings;
    private readonly MonitorHost _monitors;
    private readonly IUiDispatcher _dispatcher;
    private readonly TimeProvider _time;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger<AccountService> _logger;
    private readonly GitHubUserClient _users;
    private ICredentialStore _store;
    private AccountSession? _session;
    private int _generation;

    public AccountService(
        RepoWatchOptions options, GitHubEndpoints endpoints, HttpClient http, ICredentialStore store,
        SettingsService settings, MonitorHost monitors, IUiDispatcher dispatcher, TimeProvider time, ILoggerFactory loggers)
    {
        _options = options;
        _endpoints = endpoints;
        _http = http;
        _store = store;
        _settings = settings;
        _monitors = monitors;
        _dispatcher = dispatcher;
        _time = time;
        _loggers = loggers;
        _logger = loggers.CreateLogger<AccountService>();
        _users = new GitHubUserClient(http, endpoints);
    }

    public bool IsSignInConfigured => _options.GitHub.IsSignInConfigured;

    public AccountState State { get; private set; } = AccountState.SignedOut;

    public GitHubIdentity? Identity { get; private set; }

    /// <summary>Why the account is offline or needs reconnecting, for display.</summary>
    public string? Detail { get; private set; }

    public bool CredentialsArePersistent => _store.IsPersistent;

    public string CredentialStorage => _store.Description;

    /// <summary>The active session, for API clients. Null unless signed in or offline.</summary>
    public AccountSession? Session => _session;

    /// <summary>Raised on the UI thread when any property changes.</summary>
    public event EventHandler? Changed;

    public async Task RestoreAsync(CancellationToken cancellationToken = default)
    {
        var account = _settings.App.ActiveAccount;
        if (account is null)
        {
            Apply(AccountState.SignedOut, null, null);
            return;
        }

        var generation = _generation;
        var lastLogin = _settings.GetAccount(account).LastKnownLogin ?? account.StorageKey;
        var fallbackIdentity = new GitHubIdentity(account, lastLogin, null, null);
        Apply(AccountState.Restoring, fallbackIdentity, null);

        if (!IsSignInConfigured)
        {
            Apply(AccountState.ReconnectRequired, fallbackIdentity, "GitHub sign-in is not configured in this build.");
            return;
        }

        StoredCredential? credential;
        try
        {
            credential = await _store.ReadAsync(account, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reading stored credentials failed");
            Apply(AccountState.ReconnectRequired, fallbackIdentity, $"Stored credentials couldn't be read ({_store.Description}).");
            return;
        }

        if (credential is null)
        {
            Apply(AccountState.ReconnectRequired, fallbackIdentity, "No stored sign-in was found. Sign in again to continue.");
            return;
        }

        var session = StartSession(account, credential);
        await ConfirmIdentityAsync(session, fallbackIdentity, generation, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Starts the device flow. Throws <see cref="DeviceFlowException"/> or <see cref="GitHubTransientException"/>.</summary>
    public Task<DeviceAuthorization> BeginSignInAsync(CancellationToken cancellationToken) =>
        CreateDeviceFlowClient().RequestCodeAsync(cancellationToken);

    public async Task<SignInResult> CompleteSignInAsync(DeviceAuthorization authorization, IProgress<DeviceFlowProgress>? progress, CancellationToken cancellationToken)
    {
        var generation = _generation;
        var flow = await new DeviceFlowSignIn(CreateDeviceFlowClient(), _time)
            .WaitForAuthorizationAsync(authorization, progress, cancellationToken).ConfigureAwait(false);

        switch (flow.Outcome)
        {
            case DeviceFlowOutcome.Denied:
                return new SignInResult(SignInOutcome.Denied, "Sign-in was declined on GitHub.");
            case DeviceFlowOutcome.Expired:
                return new SignInResult(SignInOutcome.Expired, "The code expired before it was used. Start again to get a new code.");
            case DeviceFlowOutcome.Cancelled:
                return new SignInResult(SignInOutcome.Cancelled);
            case DeviceFlowOutcome.Rejected:
                return new SignInResult(SignInOutcome.Failed, DescribeOAuthError(flow.Error));
        }

        var credential = flow.Credential!;
        var lookup = await _users.GetAuthenticatedUserAsync(credential.AccessToken, cancellationToken).ConfigureAwait(false);
        if (lookup.Status != UserLookupStatus.Success)
        {
            return new SignInResult(SignInOutcome.Failed, lookup.Status == UserLookupStatus.Unauthorized
                ? "GitHub rejected the new sign-in. Try again."
                : $"Signed in, but your GitHub profile couldn't be loaded: {lookup.Detail}");
        }

        if (generation != _generation || cancellationToken.IsCancellationRequested)
        {
            return new SignInResult(SignInOutcome.Cancelled);
        }

        var identity = lookup.Identity!;
        await PersistCredentialAsync(identity.Account, credential).ConfigureAwait(false);

        // One active account: a different account replaces the previous session.
        var previous = _settings.App.ActiveAccount;
        if (previous is not null && previous != identity.Account)
        {
            await RemoveCredentialAsync(previous).ConfigureAwait(false);
        }

        _settings.UpdateApp(s => s with { ActiveAccount = identity.Account });
        _settings.UpdateAccount(identity.Account, a => a with { LastKnownLogin = identity.Login });
        _settings.Flush();

        StartSession(identity.Account, credential);
        Apply(AccountState.SignedIn, identity, null);
        _logger.LogInformation("Signed in as account {Account}", identity.Account);
        return new SignInResult(SignInOutcome.SignedIn);
    }

    /// <summary>
    /// Cancels activity for the account, removes its local credentials and ends the session.
    /// Watchlist preferences are kept for a later sign-in. This does not revoke the authorization on GitHub.
    /// </summary>
    public async Task SignOutAsync()
    {
        var account = _settings.App.ActiveAccount ?? Identity?.Account;
        Interlocked.Increment(ref _generation);
        CloseSession();

        if (account is not null)
        {
            await RemoveCredentialAsync(account).ConfigureAwait(false);
        }

        // No private repository content is cached locally yet; the snapshot cache (Stage 07) must be cleared here.
        _settings.UpdateApp(s => s with { ActiveAccount = null });
        _settings.Flush();
        Apply(AccountState.SignedOut, null, null);
        _logger.LogInformation("Signed out");
    }

    public void Dispose() => CloseSession();

    private AccountSession StartSession(AccountKey account, StoredCredential credential)
    {
        CloseSession();
        var session = new AccountSession(account, credential, _store, CreateDeviceFlowClient(), _time, _loggers.CreateLogger<AccountSession>());
        session.StatusChanged += OnSessionStatusChanged;
        _session = session;
        return session;
    }

    private void CloseSession()
    {
        var session = Interlocked.Exchange(ref _session, null);
        if (session is not null)
        {
            session.StatusChanged -= OnSessionStatusChanged;
            session.Dispose();
        }
    }

    private async Task ConfirmIdentityAsync(AccountSession session, GitHubIdentity fallback, int generation, CancellationToken cancellationToken)
    {
        try
        {
            var token = await session.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
            if (token is null)
            {
                return; // ReconnectRequired is applied by the status change handler
            }

            var lookup = await _users.GetAuthenticatedUserAsync(token, cancellationToken).ConfigureAwait(false);
            if (lookup.Status == UserLookupStatus.Unauthorized)
            {
                token = await session.HandleUnauthorizedAsync(token, cancellationToken).ConfigureAwait(false);
                lookup = token is null ? lookup : await _users.GetAuthenticatedUserAsync(token, cancellationToken).ConfigureAwait(false);
            }

            if (generation != _generation || session.Status != SessionStatus.Active)
            {
                return;
            }

            switch (lookup.Status)
            {
                case UserLookupStatus.Success when lookup.Identity!.Account == session.Account:
                    _settings.UpdateAccount(session.Account, a => a with { LastKnownLogin = lookup.Identity.Login });
                    Apply(AccountState.SignedIn, lookup.Identity, null);
                    break;
                case UserLookupStatus.Success:
                    await RemoveCredentialAsync(session.Account).ConfigureAwait(false);
                    CloseSession();
                    Apply(AccountState.ReconnectRequired, fallback, "The stored sign-in belongs to a different GitHub account. Sign in again.");
                    break;
                case UserLookupStatus.Unauthorized:
                    Apply(AccountState.ReconnectRequired, fallback, "GitHub no longer accepts this sign-in. It may have been revoked.");
                    break;
                default:
                    Apply(AccountState.Offline, fallback, lookup.Detail ?? "GitHub couldn't be reached.");
                    break;
            }
        }
        catch (TokenUnavailableException ex)
        {
            Apply(AccountState.Offline, fallback, ex.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || session.Lifetime.IsCancellationRequested)
        {
        }
    }

    private void OnSessionStatusChanged(object? sender, EventArgs e)
    {
        if (sender is AccountSession { Status: SessionStatus.ReconnectRequired } session && ReferenceEquals(session, _session))
        {
            Apply(AccountState.ReconnectRequired, Identity ?? new GitHubIdentity(session.Account, session.Account.StorageKey, null, null),
                "Your GitHub session expired or was revoked. Sign in again to continue.");
        }
    }

    private async Task PersistCredentialAsync(AccountKey account, StoredCredential credential)
    {
        try
        {
            await _store.WriteAsync(account, credential).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Never fall back to plaintext: keep the tokens in memory for this session only.
            _logger.LogError(ex, "Secure credential storage failed; using session-only mode");
            _store = new SessionCredentialStore();
            await _store.WriteAsync(account, credential).ConfigureAwait(false);
        }
    }

    private async Task RemoveCredentialAsync(AccountKey account)
    {
        try
        {
            await _store.DeleteAsync(account).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Removing stored credentials failed");
        }
    }

    private DeviceFlowClient CreateDeviceFlowClient() =>
        IsSignInConfigured
            ? new DeviceFlowClient(_http, _endpoints, _options.GitHub.ClientId!, _time)
            : throw new InvalidOperationException("GitHub sign-in is not configured (GitHub:ClientId).");

    private void Apply(AccountState state, GitHubIdentity? identity, string? detail)
    {
        _dispatcher.Post(() =>
        {
            State = state;
            Identity = identity;
            Detail = detail;
            _monitors.SetBase(new StatusOnlyMonitor(state switch
            {
                AccountState.SignedIn or AccountState.Restoring => ConnectionState.Polling,
                AccountState.Offline => ConnectionState.Offline,
                AccountState.ReconnectRequired => ConnectionState.ReconnectRequired,
                _ => ConnectionState.NotSignedIn,
            }));
            Changed?.Invoke(this, EventArgs.Empty);
        });
    }

    internal static string DescribeOAuthError(string? error) => error switch
    {
        "device_flow_disabled" => "Device flow is not enabled for this GitHub App. The app's maintainer must enable it in the app settings.",
        // GitHub answers an unknown client ID with 404 {"error":"Not Found"} (observed October 2026).
        "incorrect_client_credentials" or "Not Found" => "GitHub didn't recognise this app's client ID. Check GitHub:ClientId and that the GitHub App exists.",
        "unsupported_grant_type" or "incorrect_device_code" => "GitHub rejected the sign-in request. Start again.",
        _ => $"GitHub rejected the sign-in ({error ?? "unknown error"}).",
    };
}
