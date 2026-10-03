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

    public RelayOptions Relay { get; set; } = new();
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

/// <summary>Optional live-update relay (Stage 10). Polling works without it.</summary>
public sealed class RelayOptions
{
    public string? BaseUrl { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl);
}
