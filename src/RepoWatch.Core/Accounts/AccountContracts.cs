using RepoWatch.Core.Identity;

namespace RepoWatch.Core.Accounts;

/// <summary>
/// GitHub user tokens for one account. Secret: store only through <see cref="ICredentialStore"/>,
/// never in SQLite, settings, logs, URLs or the clipboard. <see cref="ToString"/> is redacted.
/// </summary>
public sealed record StoredCredential(
    string AccessToken,
    DateTimeOffset? AccessTokenExpiresAt,
    string? RefreshToken,
    DateTimeOffset? RefreshTokenExpiresAt)
{
    public bool CanRefresh(DateTimeOffset now) =>
        !string.IsNullOrEmpty(RefreshToken) && (RefreshTokenExpiresAt is null || RefreshTokenExpiresAt > now);

    public override string ToString() =>
        $"StoredCredential {{ AccessTokenExpiresAt = {AccessTokenExpiresAt:O}, HasRefreshToken = {RefreshToken is not null}, RefreshTokenExpiresAt = {RefreshTokenExpiresAt:O} }}";
}

/// <summary>Secure per-account token storage (OS credential store, or memory for session-only mode).</summary>
public interface ICredentialStore
{
    /// <summary>False for session-only storage: tokens are lost when the app exits.</summary>
    bool IsPersistent { get; }

    /// <summary>User-facing name of where tokens are kept, e.g. "Windows Credential Manager".</summary>
    string Description { get; }

    Task<StoredCredential?> ReadAsync(AccountKey account, CancellationToken cancellationToken = default);

    Task WriteAsync(AccountKey account, StoredCredential credential, CancellationToken cancellationToken = default);

    Task DeleteAsync(AccountKey account, CancellationToken cancellationToken = default);
}

/// <summary>The signed-in GitHub user, as resolved from the API after token acquisition.</summary>
public sealed record GitHubIdentity(AccountKey Account, string Login, string? Name, Uri? AvatarUrl);

/// <summary>Account lifecycle as shown to the user.</summary>
public enum AccountState
{
    SignedOut,
    /// <summary>Loading stored credentials and confirming the identity at startup.</summary>
    Restoring,
    SignedIn,
    /// <summary>Signed in with stored credentials, but GitHub could not be reached to confirm them.</summary>
    Offline,
    /// <summary>The session expired or was revoked; the user must sign in again. No automatic retries.</summary>
    ReconnectRequired,
}
