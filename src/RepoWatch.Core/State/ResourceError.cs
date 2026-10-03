namespace RepoWatch.Core.State;

public enum ResourceErrorKind
{
    Unknown = 0,
    Network,
    Timeout,
    RateLimited,
    /// <summary>401: the credential is missing, expired or revoked.</summary>
    Unauthorized,
    /// <summary>403 not caused by rate limiting: permission, SSO or policy.</summary>
    Forbidden,
    NotFound,
    ServerError,
    InvalidResponse,
}

/// <summary>
/// A failed refresh. <see cref="Message"/> is shown to the user and logged, so it must not contain
/// tokens, device codes or private repository content.
/// </summary>
public sealed record ResourceError(ResourceErrorKind Kind, string Message, DateTimeOffset OccurredAt)
{
    /// <summary>Earliest time the server allows a retry (rate-limit reset or Retry-After), if known.</summary>
    public DateTimeOffset? RetryAt { get; init; }
}
