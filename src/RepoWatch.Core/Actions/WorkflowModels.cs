using RepoWatch.Core.Status;

namespace RepoWatch.Core.Actions;

public sealed record Workflow
{
    public required long Id { get; init; }

    public required string Name { get; init; }

    /// <summary>Repository path, e.g. ".github/workflows/ci.yml".</summary>
    public required string Path { get; init; }

    public required Uri HtmlUrl { get; init; }
}

/// <summary>
/// One attempt of a workflow run. A re-run keeps <see cref="Id"/> and increments <see cref="RunAttempt"/>;
/// a new trigger creates a new run with a higher <see cref="RunNumber"/>.
/// </summary>
public sealed record WorkflowRun
{
    public required long Id { get; init; }

    public required long WorkflowId { get; init; }

    public required string WorkflowName { get; init; }

    public required int RunNumber { get; init; }

    public required int RunAttempt { get; init; }

    public required string HeadSha { get; init; }

    /// <summary>Branch the run was triggered for; null for tag or detached triggers.</summary>
    public string? HeadBranch { get; init; }

    /// <summary>Trigger event, e.g. "push", "pull_request", "schedule". Part of the run's scope.</summary>
    public required string Event { get; init; }

    /// <summary>This repository's pull requests GitHub lists for the run (open ones; none for forks).</summary>
    public IReadOnlyList<int> PullRequestNumbers { get; init; } = [];

    /// <summary>Owner of the repository the run's commit comes from (a fork's owner for pull requests from forks).</summary>
    public string? HeadOwner { get; init; }

    public required CheckOutcome Outcome { get; init; }

    public required Uri HtmlUrl { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}

public sealed record WorkflowJob
{
    public required long Id { get; init; }

    public required long RunId { get; init; }

    public required int RunAttempt { get; init; }

    public required string Name { get; init; }

    public required CheckOutcome Outcome { get; init; }

    public required Uri HtmlUrl { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }
}

/// <summary>Workflow health of one commit: the current run per workflow and trigger.</summary>
public sealed record CommitWorkflowSummary(string HeadSha, IReadOnlyList<WorkflowRun> Runs, CheckRollup Rollup)
{
    /// <summary>The branch whose head this commit is, when the summary was computed for a branch.</summary>
    public string? Branch { get; init; }
}

/// <summary>Actions data for one repository.</summary>
public sealed record ActionsState
{
    /// <summary>Most recent runs across branches, newest first, for the Actions list.</summary>
    public IReadOnlyList<WorkflowRun> RecentRuns { get; init; } = [];

    /// <summary>
    /// Health of the primary tracked branch's head commit (the default branch unless the user chose
    /// branches), kept distinct from PR failures.
    /// </summary>
    public CommitWorkflowSummary? DefaultBranch { get; init; }

    /// <summary>Health of every tracked branch, primary first. Empty when only <see cref="DefaultBranch"/> is known.</summary>
    public IReadOnlyList<CommitWorkflowSummary> Branches { get; init; } = [];

    /// <summary>Tracked branches GitHub did not find (deleted, renamed, or an empty repository). Shown, never silently dropped.</summary>
    public IReadOnlyList<string> MissingBranches { get; init; } = [];

    /// <summary>Every tracked branch's health, falling back to <see cref="DefaultBranch"/>.</summary>
    public IEnumerable<CommitWorkflowSummary> TrackedBranches => Branches.Count > 0 ? Branches : DefaultBranch is { } primary ? [primary] : [];
}
