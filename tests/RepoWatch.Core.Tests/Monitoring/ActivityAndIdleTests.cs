using RepoWatch.Core.Actions;
using RepoWatch.Core.Identity;
using RepoWatch.Core.Issues;
using RepoWatch.Core.Monitoring;
using RepoWatch.Core.PullRequests;
using RepoWatch.Core.Settings;
using RepoWatch.Core.State;
using RepoWatch.Core.Status;
using static RepoWatch.Core.Tests.Fixtures;

namespace RepoWatch.Core.Tests.Monitoring;

public sealed class ActivityAndIdleTests
{
    private static RepositoryMetadata Metadata(long id, DateTimeOffset? pushedAt) => new()
    {
        Key = new RepositoryKey(Account, id),
        Owner = "o",
        Name = $"r{id}",
        OwnerKind = RepositoryOwnerKind.User,
        IsPrivate = false,
        IsArchived = false,
        DefaultBranch = "main",
        HtmlUrl = new Uri($"https://github.com/o/r{id}"),
        PushedAt = pushedAt,
    };

    private static RepositorySnapshot Snapshot(
        long id,
        DateTimeOffset? pushedAt = null,
        WorkflowRun[]? runs = null,
        int openPullRequests = 0,
        int openIssues = 0,
        bool loaded = true)
    {
        var snapshot = new RepositorySnapshot(new RepositoryKey(Account, id));
        if (!loaded)
        {
            return snapshot;
        }

        runs ??= [];
        return snapshot with
        {
            Metadata = Resource<RepositoryMetadata>.NotLoaded.Succeeded(Metadata(id, pushedAt), T0),
            Actions = Resource<ActionsState>.NotLoaded.Succeeded(
                new ActionsState { RecentRuns = runs, DefaultBranch = WorkflowRunSelection.ForCommit(runs, HeadSha) }, T0),
            PullRequests = Resource<PullRequestsState>.NotLoaded.Succeeded(new PullRequestsState { OpenCount = ItemCount.Exact(openPullRequests) }, T0),
            Issues = Resource<IssuesState>.NotLoaded.Succeeded(new IssuesState { OpenCount = ItemCount.Exact(openIssues) }, T0),
        };
    }

    private static MonitoredRepository Monitored(RepositorySnapshot snapshot, int index) =>
        new(new WatchedRepository { RepositoryId = snapshot.Key.RepositoryId, Owner = "o", Name = $"r{snapshot.Key.RepositoryId}" }, snapshot, index);

    [Fact]
    public void Last_activity_is_the_newest_of_push_run_pull_request_and_issue_times()
    {
        var pushOnly = Snapshot(1, pushedAt: T0.AddDays(-3));
        var newerRun = Snapshot(2, pushedAt: T0.AddDays(-3), runs: [Run(9, CheckOutcome.Success, minutes: 30)]);

        Assert.Equal(T0.AddDays(-3), AttentionPolicy.LastActivity(pushOnly));
        Assert.Equal(T0.AddMinutes(30), AttentionPolicy.LastActivity(newerRun));
        Assert.Null(AttentionPolicy.LastActivity(Snapshot(3, loaded: false)));
    }

    [Fact]
    public void Recent_activity_order_puts_the_newest_first_and_unknown_last()
    {
        var repositories = new[]
        {
            Monitored(Snapshot(1, pushedAt: T0.AddDays(-10)), 0),
            Monitored(Snapshot(2, loaded: false), 1),
            Monitored(Snapshot(3, pushedAt: T0.AddHours(-1)), 2),
            Monitored(Snapshot(4, pushedAt: T0.AddDays(-2)), 3),
        };

        var ordered = AttentionPolicy.Order(repositories, RepositoryOrdering.RecentActivity).Select(r => r.Key.RepositoryId);

        Assert.Equal([3L, 4L, 1L, 2L], ordered);
    }

    [Fact]
    public void A_quiet_repository_with_nothing_open_or_running_is_idle()
    {
        Assert.True(AttentionPolicy.IsIdle(Snapshot(1, runs: [Run(1, CheckOutcome.Success)])));
    }

    [Theory]
    [InlineData("open pull request")]
    [InlineData("open issue")]
    [InlineData("running workflow")]
    [InlineData("failing branch")]
    [InlineData("not loaded yet")]
    public void Anything_to_look_at_or_unknown_is_not_idle(string reason)
    {
        var snapshot = reason switch
        {
            "open pull request" => Snapshot(1, openPullRequests: 1),
            "open issue" => Snapshot(1, openIssues: 2),
            "running workflow" => Snapshot(1, runs: [Run(1, CheckOutcome.Running)]),
            "failing branch" => Snapshot(1, runs: [Run(1, CheckOutcome.Failure)]),
            _ => Snapshot(1, loaded: false),
        };

        Assert.False(AttentionPolicy.IsIdle(snapshot));
    }

    [Fact]
    public void Sections_turned_off_count_as_idle()
    {
        var snapshot = Snapshot(1) with
        {
            Issues = Resource<IssuesState>.NotLoaded.FeatureUnavailable(T0),
            PullRequests = Resource<PullRequestsState>.NotLoaded.FeatureUnavailable(T0),
        };

        Assert.True(AttentionPolicy.IsIdle(snapshot));
    }
}
