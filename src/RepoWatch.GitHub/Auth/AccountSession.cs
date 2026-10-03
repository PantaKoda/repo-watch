using Microsoft.Extensions.Logging;
using RepoWatch.Core.Accounts;
using RepoWatch.Core.Identity;

namespace RepoWatch.GitHub.Auth;

public enum SessionStatus
{
    Active,
    /// <summary>The tokens were rejected; only a new sign-in can recover. Nothing retries automatically.</summary>
    ReconnectRequired,
    /// <summary>Signed out or replaced. In-flight work for this session must be discarded.</summary>
    Closed,
}

/// <summary>A token could not be obtained right now (network or server problem). The session is still valid.</summary>
public sealed class TokenUnavailableException(string message) : Exception(message);

/// <summary>
/// Owns one account's tokens. Renews the access token shortly before it expires, with at most one
/// refresh in flight. Refresh tokens are single-use, so each rotated pair is persisted before use.
/// A rejected refresh moves the session to <see cref="SessionStatus.ReconnectRequired"/> instead of
/// retrying forever.
/// </summary>
public sealed class AccountSession : IDisposable
{
    /// <summary>Renew this long before the access token expires.</summary>
    public static readonly TimeSpan RenewalMargin = TimeSpan.FromMinutes(5);

    private readonly ICredentialStore _store;
    private readonly DeviceFlowClient _client;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private StoredCredential _credential;

    public AccountSession(AccountKey account, StoredCredential credential, ICredentialStore store, DeviceFlowClient client, TimeProvider time, ILogger logger)
    {
        Account = account;
        _credential = credential;
        _store = store;
        _client = client;
        _time = time;
        _logger = logger;
        Lifetime = _lifetime.Token;
    }

    public AccountKey Account { get; }

    public SessionStatus Status { get; private set; } = SessionStatus.Active;

    /// <summary>
    /// Cancelled when the session is closed (sign-out or account change). Captured once so it stays
    /// readable after the session is disposed.
    /// </summary>
    public CancellationToken Lifetime { get; }

    /// <summary>Raised when <see cref="Status"/> changes. May be raised on any thread.</summary>
    public event EventHandler? StatusChanged;

    /// <summary>Returns a usable access token, renewing it if it is about to expire.</summary>
    /// <returns>Null when the session is closed or needs reconnecting.</returns>
    /// <exception cref="TokenUnavailableException">Renewal is needed but GitHub could not be reached.</exception>
    public async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        if (Status != SessionStatus.Active)
        {
            return null;
        }

        var credential = _credential;
        if (!NeedsRenewal(credential))
        {
            return credential.AccessToken;
        }

        return await RenewAsync(credential, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Call when the API answered 401 for <paramref name="rejectedAccessToken"/>. Tries one refresh;
    /// if that is refused too, access was revoked and the session requires reconnecting.
    /// </summary>
    public async Task<string?> HandleUnauthorizedAsync(string rejectedAccessToken, CancellationToken cancellationToken = default)
    {
        if (Status != SessionStatus.Active)
        {
            return null;
        }

        var credential = _credential;
        if (credential.AccessToken != rejectedAccessToken)
        {
            return credential.AccessToken; // already renewed by someone else
        }

        if (!credential.CanRefresh(_time.GetUtcNow()))
        {
            await RequireReconnectAsync("GitHub rejected the access token and there is no refresh token.").ConfigureAwait(false);
            return null;
        }

        return await RenewAsync(credential, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Ends the session: cancels in-flight work. Removing stored credentials is the caller's decision.</summary>
    public void Close() => SetStatus(SessionStatus.Closed);

    public void Dispose()
    {
        Close();
        _lifetime.Dispose();
        _refreshGate.Dispose();
    }

    private bool NeedsRenewal(StoredCredential credential) =>
        credential.AccessTokenExpiresAt is { } expires && expires - _time.GetUtcNow() <= RenewalMargin;

    private async Task<string?> RenewAsync(StoredCredential seen, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _refreshGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if (Status != SessionStatus.Active)
            {
                return null;
            }

            // Another caller may have renewed while we waited.
            if (!ReferenceEquals(_credential, seen))
            {
                return _credential.AccessToken;
            }

            var now = _time.GetUtcNow();
            if (!seen.CanRefresh(now))
            {
                await RequireReconnectAsync("The session expired and cannot be renewed.").ConfigureAwait(false);
                return null;
            }

            var result = await _client.RefreshAsync(seen.RefreshToken!, linked.Token).ConfigureAwait(false);
            switch (result.Status)
            {
                case RefreshStatus.Success:
                    // Persist first: the old refresh token is now invalid on GitHub's side.
                    await _store.WriteAsync(Account, result.Credential!, linked.Token).ConfigureAwait(false);
                    if (Status != SessionStatus.Active)
                    {
                        return null;
                    }

                    _credential = result.Credential!;
                    _logger.LogInformation("Renewed the GitHub access token for {Account}", Account);
                    return _credential.AccessToken;

                case RefreshStatus.Rejected:
                    await RequireReconnectAsync($"GitHub refused to renew the session ({result.Error}).").ConfigureAwait(false);
                    return null;

                default:
                    // Keep using the current token while it is still valid.
                    if (seen.AccessTokenExpiresAt is { } expires && expires > now)
                    {
                        return seen.AccessToken;
                    }

                    throw new TokenUnavailableException(result.Error ?? "Couldn't reach GitHub to renew the session.");
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task RequireReconnectAsync(string reason)
    {
        _logger.LogWarning("Session for {Account} requires reconnecting: {Reason}", Account, reason);
        try
        {
            // The stored tokens are dead; do not keep them.
            await _store.DeleteAsync(Account, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete rejected credentials for {Account}", Account);
        }

        SetStatus(SessionStatus.ReconnectRequired);
    }

    private void SetStatus(SessionStatus status)
    {
        if (Status == status || Status == SessionStatus.Closed)
        {
            return;
        }

        Status = status;
        if (status != SessionStatus.Active)
        {
            _lifetime.Cancel();
        }

        StatusChanged?.Invoke(this, EventArgs.Empty);
    }
}
