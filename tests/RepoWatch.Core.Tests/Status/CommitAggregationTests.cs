using RepoWatch.Core.Actions;
using RepoWatch.Core.PullRequests;
using RepoWatch.Core.Status;
using static RepoWatch.Core.Tests.Fixtures;

namespace RepoWatch.Core.Tests.Status;

public sealed class CommitAggregationTests
{
    [Fact]
    public void Failures_on_a_previous_commit_do_not_affect_the_current_commit()
    {
        var summary = WorkflowRunSelection.ForCommit(
        [
            Run(1, CheckOutcome.Failure, sha: PreviousSha),
            Run(2, CheckOutcome.Success, runNumber: 2),
        ], HeadSha);

        Assert.Equal(RollupState.Passing, summary.Rollup.State);
        Assert.Equal(2, Assert.Single(summary.Runs).Id);
    }

    [Fact]
    public void Commit_with_no_runs_reports_no_checks_rather_than_passing()
    {
        var summary = WorkflowRunSelection.ForCommit([Run(1, CheckOutcome.Success, sha: PreviousSha)], HeadSha);

        Assert.Equal(RollupState.NoChecks, summary.Rollup.State);
        Assert.Empty(summary.Runs);
    }

    [Fact]
    public void A_successful_rerun_supersedes_the_failed_attempt()
    {
        var summary = WorkflowRunSelection.ForCommit(
        [
            Run(1, CheckOutcome.Failure, attempt: 1),
            Run(1, CheckOutcome.Success, attempt: 2),
        ], HeadSha);

        Assert.Equal(RollupState.Passing, summary.Rollup.State);
        Assert.Equal(2, Assert.Single(summary.Runs).RunAttempt);
    }

    [Fact]
    public void An_old_attempt_observed_late_does_not_replace_the_newer_attempt()
    {
        // Attempt 2 is running; a delayed response still carries attempt 1's failure.
        var summary = WorkflowRunSelection.ForCommit(
        [
            Run(1, CheckOutcome.Running, attempt: 2, minutes: 5),
            Run(1, CheckOutcome.Failure, attempt: 1, minutes: 9),
        ], HeadSha);

        Assert.Equal(RollupState.Pending, summary.Rollup.State);
    }

    [Fact]
    public void The_most_recent_observation_of_the_same_attempt_wins()
    {
        var summary = WorkflowRunSelection.ForCommit(
        [
            Run(1, CheckOutcome.Success, minutes: 10),
            Run(1, CheckOutcome.Running, minutes: 2),
        ], HeadSha);

        Assert.Equal(CheckOutcome.Success, Assert.Single(summary.Runs).Outcome);
    }

    [Fact]
    public void Newer_run_of_a_workflow_supersedes_older_run_but_other_triggers_still_count()
    {
        var summary = WorkflowRunSelection.ForCommit(
        [
            Run(1, CheckOutcome.Failure, runNumber: 1, @event: "push"),
            Run(2, CheckOutcome.Success, runNumber: 2, @event: "push"),
            Run(3, CheckOutcome.Failure, runNumber: 3, @event: "pull_request"),
        ], HeadSha);

        Assert.Equal(RollupState.Failing, summary.Rollup.State);
        Assert.Equal([2L, 3L], summary.Runs.Select(r => r.Id).Order());
    }

    [Theory]
    [InlineData(RollupState.Failing, CheckOutcome.Running, CheckOutcome.Failure, CheckOutcome.Success)]
    [InlineData(RollupState.Failing, CheckOutcome.TimedOut, CheckOutcome.Success)]
    [InlineData(RollupState.Pending, CheckOutcome.Queued, CheckOutcome.Success)]
    [InlineData(RollupState.Cancelled, CheckOutcome.Cancelled, CheckOutcome.Success)]
    [InlineData(RollupState.ActionRequired, CheckOutcome.ActionRequired, CheckOutcome.Running)]
    [InlineData(RollupState.Unknown, CheckOutcome.Unknown, CheckOutcome.Success)]
    [InlineData(RollupState.Neutral, CheckOutcome.Skipped, CheckOutcome.Neutral)]
    [InlineData(RollupState.Passing, CheckOutcome.Skipped, CheckOutcome.Success)]
    public void Rollup_keeps_outcomes_distinct(RollupState expected, params CheckOutcome[] outcomes)
    {
        var rollup = CheckRollup.From(outcomes);

        Assert.Equal(expected, rollup.State);
        Assert.Equal(outcomes.Length, rollup.Total);
    }

    [Fact]
    public void Rerun_check_supersedes_earlier_check_with_same_app_and_name()
    {
        var summary = CommitChecks.Summarize(HeadSha,
        [
            Check(100, "build", CheckOutcome.Failure),
            Check(200, "build", CheckOutcome.Success),
            Check(150, "build", CheckOutcome.Failure, appId: 999), // same name, different app: separate check
            Check(50, "lint", CheckOutcome.Failure, sha: PreviousSha),
        ], []);

        Assert.Equal(RollupState.Failing, summary.Rollup.State);
        Assert.Equal([150L, 200L], summary.CheckRuns.Select(c => c.Id).Order());
    }

    [Fact]
    public void Latest_status_per_context_is_combined_with_check_runs()
    {
        var summary = CommitChecks.Summarize(HeadSha,
            [Check(1, "build", CheckOutcome.Success)],
            [
                LegacyStatus(1, "ci/legacy", CheckOutcome.Queued, minutes: 0),
                LegacyStatus(2, "ci/legacy", CheckOutcome.Success, minutes: 3),
            ]);

        Assert.Equal(RollupState.Passing, summary.Rollup.State);
        Assert.Equal(CheckOutcome.Success, Assert.Single(summary.Statuses).Outcome);
    }

    [Fact]
    public void Reviews_follow_github_rules_and_flag_approvals_on_older_commits()
    {
        var summary = ReviewSummary.From(
        [
            Review(1, "alice", ReviewState.Approved, 0, sha: PreviousSha),
            Review(2, "alice", ReviewState.Commented, 5), // comment keeps the approval
            Review(3, "bob", ReviewState.ChangesRequested, 1),
            Review(4, "bob", ReviewState.Dismissed, 2),
            Review(5, "carol", ReviewState.ChangesRequested, 1),
            Review(6, "carol", ReviewState.Approved, 6),
            Review(7, "dave", ReviewState.Pending, 7),
        ], [new ReviewRequest(ReviewerKind.Team, "org/platform")], HeadSha);

        Assert.Equal(2, summary.Approvals);
        Assert.Equal(0, summary.ChangesRequested);
        Assert.False(summary.LatestByReviewer.ContainsKey("bob"));
        Assert.False(summary.LatestByReviewer.ContainsKey("dave"));
        Assert.Equal(1, summary.ApprovalsOnOlderCommits);
        Assert.Single(summary.PendingRequests);
    }
}
