using RepoWatch.Core.PullRequests;
using RepoWatch.Core.Status;
using RepoWatch.GitHub.Mapping;

namespace RepoWatch.Core.Tests.GitHub;

public sealed class GitHubStatusMappingTests
{
    [Theory]
    [InlineData("queued", null, CheckOutcome.Queued)]
    [InlineData("requested", null, CheckOutcome.Queued)]
    [InlineData("waiting", null, CheckOutcome.Waiting)]
    [InlineData("in_progress", null, CheckOutcome.Running)]
    [InlineData("completed", "success", CheckOutcome.Success)]
    [InlineData("completed", "failure", CheckOutcome.Failure)]
    [InlineData("completed", "startup_failure", CheckOutcome.Failure)]
    [InlineData("completed", "timed_out", CheckOutcome.TimedOut)]
    [InlineData("completed", "cancelled", CheckOutcome.Cancelled)]
    [InlineData("completed", "skipped", CheckOutcome.Skipped)]
    [InlineData("completed", "neutral", CheckOutcome.Neutral)]
    [InlineData("completed", "action_required", CheckOutcome.ActionRequired)]
    [InlineData("completed", "stale", CheckOutcome.Stale)]
    [InlineData("completed", null, CheckOutcome.Unknown)]
    [InlineData("completed", "something_new", CheckOutcome.Unknown)]
    [InlineData(null, "success", CheckOutcome.Unknown)]
    public void Maps_run_and_check_states(string? status, string? conclusion, CheckOutcome expected) =>
        Assert.Equal(expected, GitHubStatusMapping.ToOutcome(status, conclusion));

    [Theory]
    [InlineData("success", CheckOutcome.Success)]
    [InlineData("pending", CheckOutcome.Queued)]
    [InlineData("failure", CheckOutcome.Failure)]
    [InlineData("error", CheckOutcome.Failure)]
    [InlineData("bogus", CheckOutcome.Unknown)]
    public void Maps_commit_status_states(string state, CheckOutcome expected) =>
        Assert.Equal(expected, GitHubStatusMapping.FromCommitStatusState(state));

    [Fact]
    public void Unrecognized_merge_state_is_unknown_never_clean()
    {
        Assert.Equal(MergeState.Unknown, GitHubStatusMapping.ToMergeState(null));
        Assert.Equal(MergeState.Unknown, GitHubStatusMapping.ToMergeState("unknown"));
        Assert.Equal(MergeState.Conflicting, GitHubStatusMapping.ToMergeState("dirty"));
    }
}
