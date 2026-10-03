namespace RepoWatch.Core.Settings;

/// <summary>
/// Per-account preferences, stored under the account's <see cref="Identity.AccountKey"/>.
/// Survives sign-out so the watchlist is still there on the next sign-in. Contains no secrets
/// and no cached private repository content.
/// </summary>
public sealed record AccountSettings : SettingsRecord
{
    /// <summary>Last known login, for display before the identity is re-resolved.</summary>
    public string? LastKnownLogin { get; init; }

    public RepositoryOrdering Ordering { get; init; } = RepositoryOrdering.AttentionFirst;

    /// <summary>Watched repositories in manual order. Being granted access does not add a repository here.</summary>
    public IReadOnlyList<WatchedRepository> Watchlist { get; init; } = [];
}

public enum RepositoryOrdering
{
    AttentionFirst,
    Manual,
    /// <summary>Most recent activity first: pushes, runs, pull requests and issues.</summary>
    RecentActivity,
}

public enum PullRequestScope
{
    /// <summary>Pull requests authored by me or requesting my review.</summary>
    Mine,
    All,
    None,
}

public sealed record WatchedRepository : SettingsRecord
{
    /// <summary>Stable GitHub repository ID; the identity of this entry.</summary>
    public required long RepositoryId { get; init; }

    /// <summary>Last known owner, for display while metadata loads. Not an identifier; may be empty.</summary>
    public string Owner { get; init; } = "";

    /// <summary>Last known name, for display while metadata loads. Not an identifier; may be empty.</summary>
    public string Name { get; init; } = "";

    /// <summary>Branches whose workflow health is shown; empty means the default branch.</summary>
    public IReadOnlyList<string> Branches { get; init; } = [];

    /// <summary>Workflow IDs to show; empty means all workflows.</summary>
    public IReadOnlyList<long> WorkflowIds { get; init; } = [];

    public PullRequestScope PullRequests { get; init; } = PullRequestScope.Mine;

    public bool ShowIssues { get; init; } = true;

    public bool NotificationsEnabled { get; init; } = true;
}
