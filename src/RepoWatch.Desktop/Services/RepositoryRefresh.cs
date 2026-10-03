using RepoWatch.Core.Settings;
using RepoWatch.Core.State;
using RepoWatch.Core.Status;
using RepoWatch.GitHub.Repositories;

namespace RepoWatch.Desktop.Services;

/// <summary>
/// One refresh of one repository. Metadata is loaded first (by ID, so renames are followed);
/// losing access to it withholds every section's cached content. Otherwise each section is loaded
/// and recorded independently: a failure keeps that section's last good value and never touches
/// the others.
/// </summary>
public static class RepositoryRefresh
{
    public static async Task<RepositorySnapshot> RunAsync(IRepositoryDataSource source, RepositorySnapshot snapshot, WatchedRepository watch, string login,
        TimeProvider time, CancellationToken cancellationToken, Action<RepositorySnapshot>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(watch);
        ArgumentNullException.ThrowIfNull(time);

        var info = await source.GetRepositoryAsync(snapshot.Key, cancellationToken).ConfigureAwait(false);
        if (!info.IsSuccess)
        {
            var error = info.Error!;
            return LosesAccess(error.Kind) ? snapshot.WithAccessLost(error) : FailedAll(snapshot, error);
        }

        var metadata = info.Value!.Metadata;
        // Access is back: clear "access lost" so a section's next failure shows its own error, not "No access".
        snapshot = snapshot with
        {
            Metadata = snapshot.Metadata.Succeeded(metadata, time.GetUtcNow()),
            Actions = Restored(snapshot.Actions),
            PullRequests = Restored(snapshot.PullRequests),
            Issues = Restored(snapshot.Issues),
        };
        progress?.Invoke(snapshot);

        var branches = watch.Branches.Count > 0 ? watch.Branches : [metadata.DefaultBranch];
        var actions = await source.GetActionsAsync(new ActionsRequest(metadata.Owner, metadata.Name, branches, watch.WorkflowIds), cancellationToken).ConfigureAwait(false);
        snapshot = snapshot with { Actions = Apply(snapshot.Actions, actions, time) };
        progress?.Invoke(snapshot);
        if (actions.Error?.Kind == ResourceErrorKind.RateLimited)
        {
            return snapshot; // further requests would hit the same limit
        }

        var pullRequests = watch.PullRequests == PullRequestScope.None
            ? SectionResult<Core.PullRequests.PullRequestsState>.Unavailable()
            : await source.GetPullRequestsAsync(metadata.Owner, metadata.Name, watch.PullRequests == PullRequestScope.Mine, login, time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        snapshot = snapshot with { PullRequests = Apply(snapshot.PullRequests, pullRequests, time) };
        progress?.Invoke(snapshot);
        if (pullRequests.Error?.Kind == ResourceErrorKind.RateLimited)
        {
            return snapshot;
        }

        var issues = !watch.ShowIssues || !info.Value.HasIssues
            ? SectionResult<Core.Issues.IssuesState>.Unavailable()
            : await source.GetIssuesAsync(metadata.Owner, metadata.Name, cancellationToken).ConfigureAwait(false);
        return snapshot with { Issues = Apply(snapshot.Issues, issues, time) };
    }

    /// <summary>Records one failure on every section, keeping their values (e.g. offline, or an unexpected error).</summary>
    public static RepositorySnapshot FailedAll(RepositorySnapshot snapshot, ResourceError error)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(error);
        return snapshot with
        {
            Metadata = snapshot.Metadata.Failed(error),
            Actions = snapshot.Actions.Failed(error),
            PullRequests = snapshot.PullRequests.Failed(error),
            Issues = snapshot.Issues.Failed(error),
        };
    }

    /// <summary>The error that should drive the next refresh time: rate limits first, then connectivity.</summary>
    public static ResourceError? WorstError(RepositorySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var errors = new[] { snapshot.Metadata.LastError, snapshot.Actions.LastError, snapshot.PullRequests.LastError, snapshot.Issues.LastError }
            .OfType<ResourceError>().ToList();
        return errors.FirstOrDefault(e => e.Kind == ResourceErrorKind.RateLimited)
            ?? errors.FirstOrDefault(e => e.Kind is ResourceErrorKind.Network or ResourceErrorKind.Timeout)
            ?? errors.FirstOrDefault();
    }

    /// <summary>Something is queued or running, so the repository is worth refreshing sooner.</summary>
    public static bool IsActive(RepositorySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var actions = snapshot.Actions.Value;
        return actions is not null
            && (actions.TrackedBranches.Any(b => b.Rollup.State == RollupState.Pending)
                || actions.RecentRuns.Any(r => r.Outcome is CheckOutcome.Queued or CheckOutcome.Waiting or CheckOutcome.Running));
    }

    /// <summary>404, 403 and SSO on the repository itself mean Repo Watch can no longer see it.</summary>
    private static bool LosesAccess(ResourceErrorKind kind) => kind is ResourceErrorKind.NotFound or ResourceErrorKind.Forbidden or ResourceErrorKind.SsoRequired;

    private static Resource<T> Restored<T>(Resource<T> resource) where T : class =>
        resource.Availability == ResourceAvailability.AccessLost ? Resource<T>.NotLoaded : resource;

    private static Resource<T> Apply<T>(Resource<T> resource, SectionResult<T> result, TimeProvider time) where T : class => result switch
    {
        { Value: { } value } => resource.Succeeded(value, time.GetUtcNow()),
        { FeatureUnavailable: true } => resource.FeatureUnavailable(time.GetUtcNow()),
        { Error: { } error } => resource.Failed(error),
        _ => resource,
    };
}
