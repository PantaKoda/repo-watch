using RepoWatch.Core.Status;

namespace RepoWatch.Core.Actions;

public static class WorkflowRunSelection
{
    /// <summary>
    /// Selects the runs that describe <paramref name="headSha"/> right now:
    /// runs for other commits are ignored, each run keeps only its latest attempt,
    /// and for each workflow, trigger event and branch only the newest run counts.
    /// When <paramref name="branch"/> is given, only runs triggered for that branch count, so the
    /// same commit pushed to another branch cannot replace default-branch health.
    /// Inputs may contain duplicates and out-of-order observations.
    /// </summary>
    public static CommitWorkflowSummary ForCommit(IEnumerable<WorkflowRun> runs, string headSha, string? branch = null)
    {
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentException.ThrowIfNullOrWhiteSpace(headSha);

        var current = runs
            .Where(r => string.Equals(r.HeadSha, headSha, StringComparison.OrdinalIgnoreCase))
            .Where(r => branch is null || string.Equals(r.HeadBranch, branch, StringComparison.Ordinal))
            .GroupBy(r => r.Id)
            .Select(LatestObservation)
            .GroupBy(r => (r.WorkflowId, r.Event, r.HeadBranch))
            .Select(g => g.MaxBy(r => r.RunNumber)!)
            .OrderBy(r => r.WorkflowName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Event, StringComparer.Ordinal)
            .ThenBy(r => r.HeadBranch, StringComparer.Ordinal)
            .ToList();

        return new CommitWorkflowSummary(headSha, current, CheckRollup.From(current.Select(r => r.Outcome)));
    }

    /// <summary>Latest attempt of one run; among observations of the same attempt, the most recently updated.</summary>
    public static WorkflowRun LatestObservation(IEnumerable<WorkflowRun> observationsOfOneRun)
    {
        ArgumentNullException.ThrowIfNull(observationsOfOneRun);
        return observationsOfOneRun
            .OrderByDescending(r => r.RunAttempt)
            .ThenByDescending(r => r.UpdatedAt)
            .First();
    }
}
