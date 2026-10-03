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

    public IReadOnlyList<int> PullRequestNumbers { get; init; } = [];

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
public sealed record CommitWorkflowSummary(string HeadSha, IReadOnlyList<WorkflowRun> Runs, CheckRollup Rollup);

/// <summary>Actions data for one repository.</summary>
public sealed record ActionsState
{
    /// <summary>Most recent runs across branches, newest first, for the Actions list.</summary>
    public IReadOnlyList<WorkflowRun> RecentRuns { get; init; } = [];

    /// <summary>Health of the default branch's head commit, kept distinct from PR failures.</summary>
    public CommitWorkflowSummary? DefaultBranch { get; init; }
}
