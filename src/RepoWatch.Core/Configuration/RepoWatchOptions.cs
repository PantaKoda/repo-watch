namespace RepoWatch.Core.Configuration;

/// <summary>
/// Maintainer/deployment configuration. Bound from appsettings.json, an optional
/// per-user override file and REPOWATCH_-prefixed environment variables.
/// Contains only public values: never put secrets here.
/// </summary>
public sealed class RepoWatchOptions
{
    public GitHubOptions GitHub { get; set; } = new();

    public PollingOptions Polling { get; set; } = new();

    public CacheOptions Cache { get; set; } = new();

    public RelayOptions Relay { get; set; } = new();

    public UpdatesOptions Updates { get; set; } = new();
}

public sealed class GitHubOptions
{
    public const string DefaultWebBaseUrl = "https://github.com";
    public const string DefaultApiBaseUrl = "https://api.github.com";

    /// <summary>Base URL of the GitHub web UI (device verification, installation pages).</summary>
    public string WebBaseUrl { get; set; } = DefaultWebBaseUrl;

    /// <summary>Base URL of the GitHub REST API.</summary>
    public string ApiBaseUrl { get; set; } = DefaultApiBaseUrl;

    /// <summary>
    /// Public client ID of the Repo Watch GitHub App (device flow enabled).
    /// Not a secret. When empty, sign-in is unavailable and the app explains why.
    /// </summary>
    public string? ClientId { get; set; }

    /// <summary>URL slug of the GitHub App, used to build the installation URL.</summary>
    public string? AppSlug { get; set; }

    public bool IsSignInConfigured => !string.IsNullOrWhiteSpace(ClientId);
}

/// <summary>Target refresh intervals. Server headers and the request budget take precedence.</summary>
public sealed class PollingOptions
{
    public int ActiveWorkflowSeconds { get; set; } = 20;

    public int PullRequestSeconds { get; set; } = 90;

    public int IssueSeconds { get; set; } = 120;

    public int QuietSeconds { get; set; } = 180;
}

/// <summary>Retention of the local cache of repository data (snapshots and ETag bodies).</summary>
public sealed class CacheOptions
{
    /// <summary>Cached data older than this is deleted.</summary>
    public int RetentionDays { get; set; } = 30;

    /// <summary>Most cached REST responses kept per account (oldest go first).</summary>
    public int MaxCachedResponses { get; set; } = 2000;
}

/// <summary>Optional live-update relay (Stage 10). Polling works without it.</summary>
public sealed class RelayOptions
{
    public string? BaseUrl { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl);
}

/// <summary>
/// Where "Check for updates" points. The app only opens this page in the browser: it never downloads or runs
/// an update itself (that needs signed, authenticated update metadata, which is not designed yet).
/// </summary>
public sealed class UpdatesOptions
{
    public const string DefaultReleasesUrl = "https://github.com/PantaKoda/repo-watch/releases";

    /// <summary>HTTPS page on the GitHub web host listing releases. Empty hides the action.</summary>
    public string? ReleasesUrl { get; set; } = DefaultReleasesUrl;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ReleasesUrl);

    public bool IsDefault => string.Equals(ReleasesUrl, DefaultReleasesUrl, StringComparison.Ordinal);

    /// <summary>The page to open, or null when unset or not on the GitHub web host (only those links are opened).</summary>
    public Uri? ResolveFor(Uri gitHubWebBase) =>
        IsConfigured && Uri.TryCreate(ReleasesUrl, UriKind.Absolute, out var url) && Platform.ExternalLinkPolicy.IsAllowed(url, gitHubWebBase) ? url : null;
}
