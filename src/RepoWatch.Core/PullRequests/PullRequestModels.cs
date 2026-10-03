using RepoWatch.Core.State;
using RepoWatch.Core.Status;

namespace RepoWatch.Core.PullRequests;

public enum PullRequestState
{
    Open,
    Closed,
    Merged,
}

/// <summary>
/// GitHub's computed merge state. <see cref="Unknown"/> is common (GitHub computes it lazily)
/// and must never be shown as ready to merge.
/// </summary>
public enum MergeState
{
    Unknown = 0,
    Clean,
    Conflicting,
    Blocked,
    Behind,
    Unstable,
    HasHooks,
    Draft,
}

public enum ReviewerKind
{
    User,
    Team,
}

/// <summary>A pending review request. For teams, <see cref="Login"/> is "org/team-slug".</summary>
public sealed record ReviewRequest(ReviewerKind Kind, string Login);

public enum ReviewState
{
    Commented,
    Approved,
    ChangesRequested,
    Dismissed,
    /// <summary>Started but not submitted; not visible to others and ignored in summaries.</summary>
    Pending,
}

public sealed record PullRequestReview
{
    public required long Id { get; init; }

    public required string AuthorLogin { get; init; }

    public required ReviewState State { get; init; }

    /// <summary>Commit the review was submitted against.</summary>
    public required string CommitSha { get; init; }

    public required DateTimeOffset SubmittedAt { get; init; }

    public required Uri HtmlUrl { get; init; }
}

public sealed record PullRequest
{
    public required long Id { get; init; }

    public required int Number { get; init; }

    /// <summary>Untrusted repository content; display as plain text only.</summary>
    public required string Title { get; init; }

    public required string AuthorLogin { get; init; }

    public required PullRequestState State { get; init; }

    public required bool IsDraft { get; init; }

    public required string HeadSha { get; init; }

    public required string HeadRef { get; init; }

    public required string BaseRef { get; init; }

    public IReadOnlyList<ReviewRequest> RequestedReviewers { get; init; } = [];

    public MergeState MergeState { get; init; } = MergeState.Unknown;

    public required Uri HtmlUrl { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    public DateTimeOffset? MergedAt { get; init; }
}

/// <summary>
/// A pull request with its separately fetched checks and reviews. Each part keeps its own
/// freshness so a failed review fetch does not hide check results, or vice versa.
/// </summary>
public sealed record PullRequestEntry
{
    public required PullRequest PullRequest { get; init; }

    public Resource<CommitChecksSummary> Checks { get; init; } = Resource<CommitChecksSummary>.NotLoaded;

    public Resource<ReviewSummary> Reviews { get; init; } = Resource<ReviewSummary>.NotLoaded;
}

public sealed record PullRequestsState
{
    public IReadOnlyList<PullRequestEntry> Items { get; init; } = [];

    public required ItemCount OpenCount { get; init; }
}
