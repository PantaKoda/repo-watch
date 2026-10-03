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
/// Where updates come from: the public GitHub releases of one repository. Repo Watch reads them without
/// signing in, shows what changed, and installs a release only when the user asks (see docs/updates.md).
/// </summary>
public sealed class UpdatesOptions
{
    public const string DefaultRepository = "PantaKoda/repo-watch";

    /// <summary>"owner/name" of the public repository whose GitHub releases are Repo Watch's updates. Empty turns updates off.</summary>
    public string? Repository { get; set; } = DefaultRepository;

    /// <summary>Hours between automatic checks. 0 checks only when asked (Settings › Check for updates).</summary>
    public int CheckIntervalHours { get; set; } = 24;

    /// <summary>
    /// The 0.1.0 setting (a page opened by Check for updates). Accepted and ignored, so a per-user file that
    /// still sets it doesn't stop the app; <see cref="Repository"/> replaces it.
    /// </summary>
    public string? ReleasesUrl { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Repository);

    /// <summary>The repository's releases page on the GitHub web host.</summary>
    public Uri? ReleasesPage(Uri gitHubWebBase)
    {
        ArgumentNullException.ThrowIfNull(gitHubWebBase);
        return IsConfigured ? new Uri(gitHubWebBase, $"{Repository!.Trim()}/releases") : null;
    }
}
