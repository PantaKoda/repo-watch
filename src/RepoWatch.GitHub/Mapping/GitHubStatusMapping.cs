using RepoWatch.Core.PullRequests;
using RepoWatch.Core.Status;

namespace RepoWatch.GitHub.Mapping;

/// <summary>
/// Maps GitHub's status/conclusion strings to domain values. Unrecognized values map to
/// <see cref="CheckOutcome.Unknown"/> rather than being guessed.
/// </summary>
public static class GitHubStatusMapping
{
    /// <summary>For workflow runs, workflow jobs and check runs.</summary>
    public static CheckOutcome ToOutcome(string? status, string? conclusion)
    {
        return status switch
        {
            "queued" or "requested" or "pending" => CheckOutcome.Queued,
            "waiting" => CheckOutcome.Waiting,
            "in_progress" => CheckOutcome.Running,
            "completed" => conclusion switch
            {
                "success" => CheckOutcome.Success,
                "failure" or "startup_failure" => CheckOutcome.Failure,
                "timed_out" => CheckOutcome.TimedOut,
                "cancelled" => CheckOutcome.Cancelled,
                "skipped" => CheckOutcome.Skipped,
                "neutral" => CheckOutcome.Neutral,
                "action_required" => CheckOutcome.ActionRequired,
                "stale" => CheckOutcome.Stale,
                _ => CheckOutcome.Unknown,
            },
            _ => CheckOutcome.Unknown,
        };
    }

    /// <summary>
    /// For legacy commit statuses. "error" means the status could not be produced; GitHub treats it as failing.
    /// "expected" (GraphQL) is a required status that has not reported yet.
    /// </summary>
    public static CheckOutcome FromCommitStatusState(string? state) => state switch
    {
        "success" => CheckOutcome.Success,
        "pending" or "expected" => CheckOutcome.Queued,
        "failure" or "error" => CheckOutcome.Failure,
        _ => CheckOutcome.Unknown,
    };

    public static ReviewState? ToReviewState(string? state) => state switch
    {
        "APPROVED" => ReviewState.Approved,
        "CHANGES_REQUESTED" => ReviewState.ChangesRequested,
        "COMMENTED" => ReviewState.Commented,
        "DISMISSED" => ReviewState.Dismissed,
        "PENDING" => ReviewState.Pending,
        _ => null,
    };

    public static MergeState ToMergeState(string? mergeableState) => mergeableState switch
    {
        "clean" => MergeState.Clean,
        "dirty" => MergeState.Conflicting,
        "blocked" => MergeState.Blocked,
        "behind" => MergeState.Behind,
        "unstable" => MergeState.Unstable,
        "has_hooks" => MergeState.HasHooks,
        "draft" => MergeState.Draft,
        _ => MergeState.Unknown,
    };
}
