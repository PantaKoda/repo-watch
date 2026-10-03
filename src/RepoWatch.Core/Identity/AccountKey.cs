namespace RepoWatch.Core.Identity;

/// <summary>
/// Stable identity of a signed-in account: the GitHub host plus the numeric user ID.
/// Logins can be renamed, so they are display data only and never part of a key.
/// </summary>
public sealed record AccountKey
{
    public AccountKey(string host, long userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userId);
        Host = host.Trim().TrimEnd('/').ToLowerInvariant();
        UserId = userId;
    }

    /// <summary>Normalized host name, e.g. "github.com".</summary>
    public string Host { get; }

    public long UserId { get; }

    /// <summary>Key used to isolate per-account settings and cached data.</summary>
    public string StorageKey => $"{Host}/{UserId}";

    public static AccountKey ForWebBase(Uri webBase, long userId)
    {
        ArgumentNullException.ThrowIfNull(webBase);
        return new AccountKey(webBase.IsDefaultPort ? webBase.Host : $"{webBase.Host}:{webBase.Port}", userId);
    }

    public override string ToString() => StorageKey;
}
