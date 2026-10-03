using RepoWatch.Core.Status;

namespace RepoWatch.Core.PullRequests;

/// <summary>A check run (GitHub Checks API). Actions jobs also appear as check runs.</summary>
public sealed record CheckRun
{
    public required long Id { get; init; }

    public required string Name { get; init; }

    /// <summary>ID of the GitHub App that created the check.</summary>
    public required long AppId { get; init; }

    /// <summary>
    /// Check suite the run belongs to. Names are only unique within a suite: every Actions workflow
    /// is a separate suite from the same app, and re-runs stay in their suite.
    /// </summary>
    public required long CheckSuiteId { get; init; }

    public required string HeadSha { get; init; }

    public required CheckOutcome Outcome { get; init; }

    public required Uri HtmlUrl { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }
}

/// <summary>A legacy commit status (Statuses API), identified by its context.</summary>
public sealed record CommitStatus
{
    public required long Id { get; init; }

    public required string Context { get; init; }

    public required string Sha { get; init; }

    public required CheckOutcome Outcome { get; init; }

    public Uri? TargetUrl { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}

public sealed record CommitChecksSummary(string Sha, IReadOnlyList<CheckRun> CheckRuns, IReadOnlyList<CommitStatus> Statuses, CheckRollup Rollup)
{
    /// <summary>False when GitHub reported more checks than were loaded; the rollup then covers only those loaded.</summary>
    public bool IsComplete { get; init; } = true;
}

public static class CommitChecks
{
    /// <summary>
    /// Summarizes the checks of one commit. A re-run check supersedes the earlier run with the
    /// same name in the same check suite; same-named checks in different suites (e.g. a "build"
    /// job in two workflows) are counted separately. A newer status supersedes older statuses
    /// with the same context.
    /// </summary>
    public static CommitChecksSummary Summarize(string sha, IEnumerable<CheckRun> checkRuns, IEnumerable<CommitStatus> statuses)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sha);
        ArgumentNullException.ThrowIfNull(checkRuns);
        ArgumentNullException.ThrowIfNull(statuses);

        var currentRuns = checkRuns
            .Where(c => string.Equals(c.HeadSha, sha, StringComparison.OrdinalIgnoreCase))
            .GroupBy(c => (c.CheckSuiteId, c.Name))
            .Select(g => g.MaxBy(c => c.Id)!)
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var currentStatuses = statuses
            .Where(s => string.Equals(s.Sha, sha, StringComparison.OrdinalIgnoreCase))
            .GroupBy(s => s.Context)
            .Select(g => g.OrderByDescending(s => s.CreatedAt).ThenByDescending(s => s.Id).First())
            .OrderBy(s => s.Context, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var rollup = CheckRollup.From(currentRuns.Select(c => c.Outcome).Concat(currentStatuses.Select(s => s.Outcome)));
        return new CommitChecksSummary(sha, currentRuns, currentStatuses, rollup);
    }
}
