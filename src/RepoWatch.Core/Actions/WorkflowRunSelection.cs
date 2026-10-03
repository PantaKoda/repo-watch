using RepoWatch.Core.Status;

namespace RepoWatch.Core.Actions;

public static class WorkflowRunSelection
{
    /// <summary>
    /// Selects the runs that describe <paramref name="headSha"/> right now:
    /// runs for other commits are ignored, each run keeps only its latest attempt,
    /// and for each workflow and trigger event only the newest run counts.
    /// Inputs may contain duplicates and out-of-order observations.
    /// </summary>
    public static CommitWorkflowSummary ForCommit(IEnumerable<WorkflowRun> runs, string headSha)
    {
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentException.ThrowIfNullOrWhiteSpace(headSha);

        var current = runs
            .Where(r => string.Equals(r.HeadSha, headSha, StringComparison.OrdinalIgnoreCase))
            .GroupBy(r => r.Id)
            .Select(LatestObservation)
            .GroupBy(r => (r.WorkflowId, r.Event))
            .Select(g => g.MaxBy(r => r.RunNumber)!)
            .OrderBy(r => r.WorkflowName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Event, StringComparer.Ordinal)
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
