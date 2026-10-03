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

/// <summary>A device-flow sign-in in progress. Contains only the user code, never the device code.</summary>
public sealed record SignInFlow(string? UserCode, Uri? VerificationUri, DateTimeOffset ExpiresAt, string Status);

/// <summary>
/// The single active GitHub account: restoring it at startup, signing in with the device flow,
/// signing out, and turning a rejected session into a visible reconnect state. Settings and cached
/// data are keyed by host + user ID; tokens live only in the credential store.
/// The sign-in flow belongs to this service, not to a window, so closing settings does not lose it.
/// </summary>
public sealed class AccountService : IDisposable
{
    private readonly RepoWatchOptions _options;
    private readonly GitHubEndpoints _endpoints;
    private readonly HttpClient _http;
    private readonly ICredentialStore _secureStore;
    private readonly SettingsService _settings;
    private readonly MonitorHost _monitors;
    private readonly IUiDispatcher _dispatcher;
    private readonly TimeProvider _time;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger<AccountService> _logger;
    private readonly GitHubUserClient _users;
    private ICredentialStore _store;
    private AccountSession? _session;
    private CancellationTokenSource? _flowCancel;
    private int _generation;

    public AccountService(
        RepoWatchOptions options, GitHubEndpoints endpoints, HttpClient http, ICredentialStore store,
        SettingsService settings, MonitorHost monitors, IUiDispatcher dispatcher, TimeProvider time, ILoggerFactory loggers)
    {
        _options = options;
        _endpoints = endpoints;
        _http = http;
        _secureStore = store;
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

    /// <summary>Set when tokens are in use but could not be saved securely (they last for this session only).</summary>
    public string? StorageWarning { get; private set; }

    public bool CredentialsArePersistent => _store.IsPersistent && StorageWarning is null;

    public string CredentialStorage => _store.Description;

    /// <summary>The sign-in in progress, or null.</summary>
    public SignInFlow? Flow { get; private set; }

    /// <summary>Outcome of the last sign-in attempt that did not succeed (declined, expired, failed, cancelled).</summary>
    public string? LastSignInMessage { get; private set; }

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

        var session = await StartSessionAsync(account, credential).ConfigureAwait(false);
        await ConfirmIdentityAsync(session, fallbackIdentity, generation, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs a complete sign-in owned by this service: requests a code, publishes it in <see cref="Flow"/>,
    /// calls <paramref name="onCode"/> (e.g. to open the browser) and waits for approval. Closing the
    /// settings window does not cancel it; <see cref="CancelSignIn"/>, sign-out and quitting do.
    /// </summary>
    public async Task StartSignInAsync(Func<Uri, Task>? onCode = null)
    {
        if (Flow is not null || !IsSignInConfigured)
        {
            return;
        }

        using var cancel = new CancellationTokenSource();
        _flowCancel = cancel;
        LastSignInMessage = null;
        SetFlow(new SignInFlow(null, null, default, "Requesting a sign-in code from GitHub…"));

        string? message;
        try
        {
            var authorization = await BeginSignInAsync(cancel.Token).ConfigureAwait(false);
            SetFlow(new SignInFlow(authorization.UserCode, authorization.VerificationUri, authorization.ExpiresAt,
                "Enter this code on GitHub, then approve Repo Watch."));
            if (onCode is not null)
            {
                await onCode(authorization.VerificationUri).ConfigureAwait(false);
            }

            var progress = new InlineProgress(p => SetFlow(Flow! with
            {
                Status = p.Kind switch
                {
                    DeviceFlowProgressKind.SlowedDown => "GitHub asked Repo Watch to check less often. Still waiting for approval…",
                    DeviceFlowProgressKind.RetryingAfterError => $"Couldn't reach GitHub ({p.Detail}). Retrying…",
                    _ => "Waiting for you to approve Repo Watch on GitHub…",
                },
            }));
            var result = await CompleteSignInAsync(authorization, progress, cancel.Token).ConfigureAwait(false);
            message = result.Outcome switch
            {
                SignInOutcome.SignedIn => null,
                SignInOutcome.Cancelled => "Sign-in cancelled.",
                _ => result.Message,
            };
        }
        catch (DeviceFlowException ex)
        {
            message = DescribeOAuthError(ex.Error);
        }
        catch (GitHubTransientException ex)
        {
            message = $"Couldn't start sign-in: {ex.Message}";
        }
        catch (OperationCanceledException)
        {
            message = "Sign-in cancelled.";
        }
        finally
        {
            _flowCancel = null;
        }

        _dispatcher.Post(() =>
        {
            Flow = null;
            LastSignInMessage = message;
            Changed?.Invoke(this, EventArgs.Empty);
        });
    }

    public void CancelSignIn() => _flowCancel?.Cancel();

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
        var previous = _settings.App.ActiveAccount;
        await CloseSessionAsync().ConfigureAwait(false);

        // One active account: a different account's credentials are removed (from every store).
        if (previous is not null && previous != identity.Account)
        {
            await RemoveCredentialAsync(previous).ConfigureAwait(false);
        }

        await PersistCredentialAsync(identity.Account, credential).ConfigureAwait(false);
        _settings.UpdateApp(s => s with { ActiveAccount = identity.Account });
        _settings.UpdateAccount(identity.Account, a => a with { LastKnownLogin = identity.Login });
        _settings.Flush();

        await StartSessionAsync(identity.Account, credential).ConfigureAwait(false);
        Apply(AccountState.SignedIn, identity, null);
        _logger.LogInformation("Signed in as account {Account}", identity.Account);
        return new SignInResult(SignInOutcome.SignedIn);
    }

    /// <summary>
    /// Cancels activity for the account, waits for any token renewal in flight (so nothing re-creates
    /// credentials), removes its local credentials and ends the session. Watchlist preferences are kept.
    /// This does not revoke the authorization on GitHub.
    /// </summary>
    public async Task SignOutAsync()
    {
        var account = _settings.App.ActiveAccount ?? Identity?.Account;
        Interlocked.Increment(ref _generation);
        CancelSignIn();
        await CloseSessionAsync().ConfigureAwait(false);

        if (account is not null)
        {
            await RemoveCredentialAsync(account).ConfigureAwait(false);
        }

        // No private repository content is cached locally yet; the snapshot cache (Stage 07) must be cleared here.
        _settings.UpdateApp(s => s with { ActiveAccount = null });
        _settings.Flush();
        StorageWarning = null;
        Apply(AccountState.SignedOut, null, null);
        _logger.LogInformation("Signed out");
    }

    public void Dispose()
    {
        CancelSignIn();
        _session?.Dispose();
    }

    private async Task<AccountSession> StartSessionAsync(AccountKey account, StoredCredential credential)
    {
        await CloseSessionAsync().ConfigureAwait(false);
        var session = new AccountSession(account, credential, _store, CreateDeviceFlowClient(), _time, _loggers.CreateLogger<AccountSession>());
        session.StatusChanged += OnSessionStatusChanged;
        session.PersistenceFailed += OnPersistenceFailed;
        _session = session;
        return session;
    }

    private async Task CloseSessionAsync()
    {
        var session = Interlocked.Exchange(ref _session, null);
        if (session is not null)
        {
            session.StatusChanged -= OnSessionStatusChanged;
            session.PersistenceFailed -= OnPersistenceFailed;
            await session.CloseAsync().ConfigureAwait(false);
        }
    }

    private async Task ConfirmIdentityAsync(AccountSession session, GitHubIdentity fallback, int generation, CancellationToken cancellationToken)
    {
        try
        {
            var token = await session.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
            if (token is null)
            {
                ApplyIfCurrent(session, generation, AccountState.ReconnectRequired, fallback, "Your GitHub session expired or was revoked. Sign in again to continue.");
                return;
            }

            var lookup = await _users.GetAuthenticatedUserAsync(token, cancellationToken).ConfigureAwait(false);
            if (lookup.Status == UserLookupStatus.Unauthorized)
            {
                token = await session.HandleUnauthorizedAsync(token, cancellationToken).ConfigureAwait(false);
                lookup = token is null ? lookup : await _users.GetAuthenticatedUserAsync(token, cancellationToken).ConfigureAwait(false);
            }

            if (generation != _generation)
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
                    await session.RequireReconnectAsync("The stored sign-in belongs to a different GitHub account.").ConfigureAwait(false);
                    Apply(AccountState.ReconnectRequired, fallback, "The stored sign-in belongs to a different GitHub account. Sign in again.");
                    break;
                case UserLookupStatus.Unauthorized:
                    // Rejected even after renewal: end the session properly (tokens deleted, work cancelled).
                    await session.RequireReconnectAsync("GitHub rejected the account after renewal.").ConfigureAwait(false);
                    Apply(AccountState.ReconnectRequired, fallback, "GitHub no longer accepts this sign-in. It may have been revoked.");
                    break;
                default:
                    Apply(AccountState.Offline, fallback, lookup.Detail ?? "GitHub couldn't be reached.");
                    break;
            }
        }
        catch (TokenUnavailableException ex)
        {
            ApplyIfCurrent(session, generation, AccountState.Offline, fallback, ex.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || session.Lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // Never leave the account stuck in "Connecting".
            _logger.LogError(ex, "Confirming the GitHub account failed");
            ApplyIfCurrent(session, generation, AccountState.Offline, fallback, $"Couldn't confirm your GitHub sign-in: {ex.Message}");
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

    private void OnPersistenceFailed(object? sender, Exception e) => _dispatcher.Post(() =>
    {
        StorageWarning = $"Your renewed sign-in couldn't be saved to {_store.Description}, so you will need to sign in again after restarting.";
        Changed?.Invoke(this, EventArgs.Empty);
    });

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

    // Removes from the secure store as well as any session-only fallback, so a fallback can never
    // leave live tokens behind in Credential Manager.
    private async Task RemoveCredentialAsync(AccountKey account)
    {
        foreach (var store in new[] { _secureStore, _store }.Distinct())
        {
            try
            {
                await store.DeleteAsync(account).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Removing stored credentials from {Store} failed", store.Description);
            }
        }
    }

    private DeviceFlowClient CreateDeviceFlowClient() =>
        IsSignInConfigured
            ? new DeviceFlowClient(_http, _endpoints, _options.GitHub.ClientId!, _time)
            : throw new InvalidOperationException("GitHub sign-in is not configured (GitHub:ClientId).");

    private void SetFlow(SignInFlow flow) => _dispatcher.Post(() =>
    {
        Flow = flow;
        Changed?.Invoke(this, EventArgs.Empty);
    });

    private void ApplyIfCurrent(AccountSession session, int generation, AccountState state, GitHubIdentity identity, string detail)
    {
        if (generation == _generation && ReferenceEquals(session, _session))
        {
            Apply(state, identity, detail);
        }
    }

    private void Apply(AccountState state, GitHubIdentity? identity, string? detail)
    {
        _dispatcher.Post(() =>
        {
            State = state;
            Identity = identity;
            Detail = detail;
            // Until real monitoring exists (Stage 06), a signed-in account monitors nothing: say so.
            _monitors.SetBase(new StatusOnlyMonitor(state switch
            {
                AccountState.Restoring => ConnectionState.Connecting,
                AccountState.SignedIn => ConnectionState.SignedInIdle,
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

    // Progress<T> posts to a captured context; this reports inline so ordering is preserved.
    private sealed class InlineProgress(Action<DeviceFlowProgress> report) : IProgress<DeviceFlowProgress>
    {
        public void Report(DeviceFlowProgress value) => report(value);
    }
}
