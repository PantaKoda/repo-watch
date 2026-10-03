namespace RepoWatch.Core.State;

public enum ResourceErrorKind
{
    Unknown = 0,
    Network,
    Timeout,
    RateLimited,
    /// <summary>401: the credential is missing, expired or revoked.</summary>
    Unauthorized,
    /// <summary>403 not caused by rate limiting or SSO: permission or policy.</summary>
    Forbidden,
    /// <summary>403: the organization requires an active SAML SSO session for this account.</summary>
    SsoRequired,
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

    /// <summary>A GitHub page that resolves the problem (e.g. the SSO authorization URL), if GitHub supplied one.</summary>
    public Uri? ActionUrl { get; init; }
}
