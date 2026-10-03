using RepoWatch.Core.Settings;
using RepoWatch.Core.State;
using RepoWatch.Core.Status;

namespace RepoWatch.Core.Monitoring;

/// <summary>How much a repository needs the user's attention, lowest to highest.</summary>
public enum AttentionLevel
{
    Quiet = 0,
    /// <summary>Workflows are running or queued on a tracked branch.</summary>
    Active,
    /// <summary>A section could not be loaded, access was lost, or an open pull request has failing checks.</summary>
    Warning,
    /// <summary>A tracked branch's current commit is failing or needs action.</summary>
    Failure,
}

public static class AttentionPolicy
{
    public static AttentionLevel Evaluate(RepositorySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var branches = snapshot.Actions.Value?.TrackedBranches.Select(b => b.Rollup.State).ToList() ?? [];
        if (branches.Any(s => s is RollupState.Failing or RollupState.ActionRequired))
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

        return branches.Contains(RollupState.Pending) ? AttentionLevel.Active : AttentionLevel.Quiet;
    }

    /// <summary>
    /// Orders repositories for display. Attention-first sorts by level and recent activity sorts by
    /// <see cref="LastActivity"/> (unknown last); both keep manual order for ties.
    /// </summary>
    public static IReadOnlyList<MonitoredRepository> Order(IEnumerable<MonitoredRepository> repositories, RepositoryOrdering ordering)
    {
        ArgumentNullException.ThrowIfNull(repositories);
        var byManual = repositories.OrderBy(r => r.ManualIndex);
        return ordering switch
        {
            RepositoryOrdering.AttentionFirst => byManual.OrderByDescending(r => Evaluate(r.Snapshot)).ToList(),
            RepositoryOrdering.RecentActivity => byManual.OrderByDescending(r => LastActivity(r.Snapshot) ?? DateTimeOffset.MinValue).ToList(),
            _ => byManual.ToList(),
        };
    }

    /// <summary>
    /// The latest known activity: the last push, or a newer workflow run, pull request or issue update.
    /// Uses whatever data is loaded (cached values included); null when nothing is known yet.
    /// </summary>
    public static DateTimeOffset? LastActivity(RepositorySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        DateTimeOffset?[] candidates =
        [
            snapshot.Metadata.Value?.PushedAt,
            snapshot.Actions.Value?.RecentRuns.Select(r => (DateTimeOffset?)r.UpdatedAt).Max(),
            snapshot.PullRequests.Value?.Items.Select(p => (DateTimeOffset?)p.PullRequest.UpdatedAt).Max(),
            snapshot.Issues.Value?.Items.Select(i => (DateTimeOffset?)i.UpdatedAt).Max(),
        ];
        return candidates.Max();
    }

    /// <summary>
    /// Nothing to look at: no failures or problems, nothing running, no open pull requests (in the
    /// watched scope) and no open issues. A section that hasn't loaded yet is not idle, so a
    /// repository is never hidden for lack of data.
    /// </summary>
    public static bool IsIdle(RepositorySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (Evaluate(snapshot) != AttentionLevel.Quiet)
        {
            return false;
        }

        var actionsIdle = snapshot.Actions.Availability == ResourceAvailability.FeatureUnavailable
            || snapshot.Actions.Value is { } actions && !actions.RecentRuns.Any(r => r.Outcome is CheckOutcome.Queued or CheckOutcome.Waiting or CheckOutcome.Running);
        var pullRequestsIdle = snapshot.PullRequests.Availability == ResourceAvailability.FeatureUnavailable
            || snapshot.PullRequests.Value is { OpenCount.Value: 0 };
        var issuesIdle = snapshot.Issues.Availability == ResourceAvailability.FeatureUnavailable
            || snapshot.Issues.Value is { OpenCount.Value: 0 };
        return actionsIdle && pullRequestsIdle && issuesIdle;
    }

    private static bool HasProblem<T>(Resource<T> resource) where T : class =>
        resource.Availability == ResourceAvailability.AccessLost || (resource.Value is null && resource.LastError is not null);
}
