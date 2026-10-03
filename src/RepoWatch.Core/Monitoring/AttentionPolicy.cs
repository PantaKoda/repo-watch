using RepoWatch.Core.Settings;
using RepoWatch.Core.State;
using RepoWatch.Core.Status;

namespace RepoWatch.Core.Monitoring;

/// <summary>How much a repository needs the user's attention, lowest to highest.</summary>
public enum AttentionLevel
{
    Quiet = 0,
    /// <summary>Workflows are running or queued on the default branch.</summary>
    Active,
    /// <summary>A section could not be loaded, access was lost, or an open pull request has failing checks.</summary>
    Warning,
    /// <summary>The default branch's current commit is failing or needs action.</summary>
    Failure,
}

public static class AttentionPolicy
{
    public static AttentionLevel Evaluate(RepositorySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var defaultBranch = snapshot.Actions.Value?.DefaultBranch?.Rollup.State;
        if (defaultBranch is RollupState.Failing or RollupState.ActionRequired)
        {
            return AttentionLevel.Failure;
        }

        var sectionProblem = HasProblem(snapshot.Metadata) || HasProblem(snapshot.Actions)
            || HasProblem(snapshot.PullRequests) || HasProblem(snapshot.Issues);
        var failingPullRequest = snapshot.PullRequests.Value?.Items.Any(p => p.Checks.Value?.Rollup.State == RollupState.Failing) == true;
        if (sectionProblem || failingPullRequest)
        {
            return AttentionLevel.Warning;
        }

        return defaultBranch == RollupState.Pending ? AttentionLevel.Active : AttentionLevel.Quiet;
    }

    /// <summary>
    /// Orders repositories for display. Attention-first sorts by level, keeping manual order within a level.
    /// </summary>
    public static IReadOnlyList<MonitoredRepository> Order(IEnumerable<MonitoredRepository> repositories, RepositoryOrdering ordering)
    {
        ArgumentNullException.ThrowIfNull(repositories);
        var byManual = repositories.OrderBy(r => r.ManualIndex);
        return ordering == RepositoryOrdering.AttentionFirst
            ? byManual.OrderByDescending(r => Evaluate(r.Snapshot)).ToList()
            : byManual.ToList();
    }

    private static bool HasProblem<T>(Resource<T> resource) where T : class =>
        resource.Availability == ResourceAvailability.AccessLost || (resource.Value is null && resource.LastError is not null);
}
