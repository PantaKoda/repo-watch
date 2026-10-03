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
/// Owns one account's tokens. Renews the access token shortly before it expires.
/// <list type="bullet">
/// <item>At most one renewal runs at a time, and it belongs to the session rather than to any caller:
/// it runs on <see cref="Lifetime"/>, so a caller that stops waiting can never abort GitHub's
/// single-use refresh halfway and lose the rotated tokens.</item>
/// <item>The rotated pair is adopted even if persisting it fails (the failure is reported), because
/// GitHub has already invalidated the old refresh token.</item>
/// <item>Nothing is written after the session is closed, and <see cref="CloseAsync"/> waits for an
/// in-flight renewal, so sign-out can delete credentials knowing nothing will re-create them.</item>
/// <item>A rejected refresh moves to <see cref="SessionStatus.ReconnectRequired"/>; nothing retries.</item>
/// </list>
/// </summary>
public sealed class AccountSession : IDisposable
{
    /// <summary>Renew this long before the access token expires.</summary>
    public static readonly TimeSpan RenewalMargin = TimeSpan.FromMinutes(5);

    private readonly ICredentialStore _store;
    private readonly DeviceFlowClient _client;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private StoredCredential _credential;
    private Task<string?>? _renewal;
    private int _status = (int)SessionStatus.Active;

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

    public SessionStatus Status => (SessionStatus)Volatile.Read(ref _status);

    /// <summary>Cancelled when the session is closed or needs reconnecting. Stays readable after disposal.</summary>
    public CancellationToken Lifetime { get; }

    /// <summary>Raised when <see cref="Status"/> changes. May be raised on any thread.</summary>
    public event EventHandler? StatusChanged;

    /// <summary>Raised when renewed tokens are in use but could not be persisted. May be raised on any thread.</summary>
    public event EventHandler<Exception>? PersistenceFailed;

    /// <summary>Returns a usable access token, renewing it if it is about to expire.</summary>
    /// <returns>Null when the session is closed or needs reconnecting.</returns>
    /// <exception cref="TokenUnavailableException">Renewal is needed but GitHub could not be reached.</exception>
    public async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        if (Status != SessionStatus.Active)
        {
            return null;
        }

        var credential = Volatile.Read(ref _credential);
        return NeedsRenewal(credential)
            ? await Renewal(credential).WaitAsync(cancellationToken).ConfigureAwait(false)
            : credential.AccessToken;
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

        var credential = Volatile.Read(ref _credential);
        if (credential.AccessToken != rejectedAccessToken)
        {
            return credential.AccessToken; // already renewed
        }

        if (!credential.CanRefresh(_time.GetUtcNow()))
        {
            await RequireReconnectAsync("GitHub rejected the access token and there is no refresh token.").ConfigureAwait(false);
            return null;
        }

        return await Renewal(credential).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Marks the session as needing a new sign-in: deletes its stored tokens and cancels its work.
    /// Use when GitHub keeps rejecting the account even after a successful renewal.
    /// </summary>
    public async Task RequireReconnectAsync(string reason)
    {
        if (Status != SessionStatus.Active)
        {
            return;
        }

        _logger.LogWarning("Session for {Account} requires reconnecting: {Reason}", Account, reason);
        SetStatus(SessionStatus.ReconnectRequired);
        try
        {
            await _store.DeleteAsync(Account, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete rejected credentials for {Account}", Account);
        }
    }

    /// <summary>Ends the session and waits for any in-flight renewal to finish, so nothing writes afterwards.</summary>
    public async Task CloseAsync()
    {
        SetStatus(SessionStatus.Closed);
        Task? renewal;
        lock (_gate)
        {
            renewal = _renewal;
        }

        if (renewal is not null)
        {
            try
            {
                await renewal.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or TokenUnavailableException)
            {
            }
        }
    }

    /// <summary>Closes the session without waiting. In-flight work unwinds on its own; nothing is disposed under it.</summary>
    public void Dispose() => SetStatus(SessionStatus.Closed);

    private bool NeedsRenewal(StoredCredential credential) =>
        credential.AccessTokenExpiresAt is { } expires && expires - _time.GetUtcNow() <= RenewalMargin;

    // Joins the renewal in flight, or starts one for the credential the caller saw.
    private Task<string?> Renewal(StoredCredential seen)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_credential, seen))
            {
                return Task.FromResult(Status == SessionStatus.Active ? _credential.AccessToken : null);
            }

            return _renewal ??= RenewAsync(seen);
        }
    }

    private async Task<string?> RenewAsync(StoredCredential seen)
    {
        await Task.Yield(); // leave the lock before doing any work
        try
        {
            if (Status != SessionStatus.Active)
            {
                return null;
            }

            var now = _time.GetUtcNow();
            if (!seen.CanRefresh(now))
            {
                await RequireReconnectAsync("The session expired and cannot be renewed.").ConfigureAwait(false);
                return null;
            }

            var result = await _client.RefreshAsync(seen.RefreshToken!, Lifetime).ConfigureAwait(false);
            switch (result.Status)
            {
                case RefreshStatus.Success:
                    var renewed = result.Credential!;
                    if (Status != SessionStatus.Active)
                    {
                        return null; // signed out meanwhile: do not write tokens back
                    }

                    try
                    {
                        await _store.WriteAsync(Account, renewed, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        // The old refresh token is already invalid; keep the new pair in memory.
                        _logger.LogError(ex, "Renewed tokens for {Account} could not be saved; continuing for this session only", Account);
                        PersistenceFailed?.Invoke(this, ex);
                    }

                    lock (_gate)
                    {
                        _credential = renewed;
                    }

                    _logger.LogInformation("Renewed the GitHub access token for {Account}", Account);
                    return Status == SessionStatus.Active ? renewed.AccessToken : null;

                case RefreshStatus.Rejected:
                    await RequireReconnectAsync($"GitHub refused to renew the session ({result.Error}).").ConfigureAwait(false);
                    return null;

                default:
                    if (seen.AccessTokenExpiresAt is { } expires && expires > now)
                    {
                        return seen.AccessToken; // keep using the current token while it is valid
                    }

                    throw new TokenUnavailableException(result.Error ?? "Couldn't reach GitHub to renew the session.");
            }
        }
        catch (OperationCanceledException) when (Lifetime.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            lock (_gate)
            {
                _renewal = null;
            }
        }
    }

    private void SetStatus(SessionStatus status)
    {
        int previous;
        lock (_gate)
        {
            previous = _status;
            if (previous == (int)status || previous == (int)SessionStatus.Closed)
            {
                return;
            }

            _status = (int)status;
        }

        if (status != SessionStatus.Active)
        {
            _lifetime.Cancel();
        }

        StatusChanged?.Invoke(this, EventArgs.Empty);
    }
}
