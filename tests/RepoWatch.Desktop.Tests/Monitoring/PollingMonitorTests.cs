using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using RepoWatch.Core.Actions;
using RepoWatch.Core.Identity;
using RepoWatch.Core.Issues;
using RepoWatch.Core.Monitoring;
using RepoWatch.Core.PullRequests;
using RepoWatch.Core.Settings;
using RepoWatch.Core.State;
using RepoWatch.Core.Status;
using RepoWatch.Desktop.Services;
using RepoWatch.GitHub.Api;
using RepoWatch.GitHub.Repositories;

namespace RepoWatch.Desktop.Tests.Monitoring;

/// <summary>A scripted data source: per-repository answers, recorded calls, optional gates.</summary>
internal sealed class FakeDataSource : IRepositoryDataSource
{
    public ConcurrentQueue<string> Calls { get; } = new();

    public Func<long, ApiResult<RepositoryInfo>> Repository { get; set; } = id => ApiResult<RepositoryInfo>.Ok(Info(id));

    public Func<ActionsRequest, SectionResult<ActionsState>> Actions { get; set; } = _ => SectionResult<ActionsState>.Ok(new ActionsState());

    public Func<string, bool, SectionResult<PullRequestsState>> PullRequests { get; set; } =
        (_, _) => SectionResult<PullRequestsState>.Ok(new PullRequestsState { OpenCount = ItemCount.Exact(0) });

    public Func<string, SectionResult<IssuesState>> Issues { get; set; } = _ => SectionResult<IssuesState>.Ok(new IssuesState { OpenCount = ItemCount.Exact(0) });

    /// <summary>When set, the Actions call waits for it, so tests can change things mid-refresh.</summary>
    public TaskCompletionSource? ActionsGate { get; set; }

    public TaskCompletionSource ActionsEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static RepositoryInfo Info(long id, bool hasIssues = true) => new(new RepositoryMetadata
    {
        Key = new RepositoryKey(PollingMonitorTests.Account, id),
        Owner = "octo",
        Name = $"repo{id}",
        OwnerKind = RepositoryOwnerKind.User,
        IsPrivate = true,
        IsArchived = false,
        DefaultBranch = "main",
        HtmlUrl = new Uri($"https://github.com/octo/repo{id}"),
    }, hasIssues);

    public Task<ApiResult<RepositoryInfo>> GetRepositoryAsync(RepositoryKey key, CancellationToken cancellationToken)
    {
        Calls.Enqueue($"repo {key.RepositoryId}");
        return Task.FromResult(Repository(key.RepositoryId));
    }

    public async Task<SectionResult<ActionsState>> GetActionsAsync(ActionsRequest request, CancellationToken cancellationToken)
    {
        Calls.Enqueue($"actions {request.Name} {string.Join(",", request.Branches)}");
        ActionsEntered.TrySetResult();
        if (ActionsGate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }

        return Actions(request);
    }

    public Task<SectionResult<PullRequestsState>> GetPullRequestsAsync(string owner, string name, bool mineOnly, string login, DateTimeOffset now, CancellationToken cancellationToken)
    {
        Calls.Enqueue($"pulls {name} mine={mineOnly} login={login}");
        return Task.FromResult(PullRequests(name, mineOnly));
    }

    public Task<SectionResult<IssuesState>> GetIssuesAsync(string owner, string name, CancellationToken cancellationToken)
    {
        Calls.Enqueue($"issues {name}");
        return Task.FromResult(Issues(name));
    }
}

public sealed class PollingMonitorTests
{
    internal static readonly AccountKey Account = new("github.com", 4242);

    private static readonly PollingIntervals Quiet = new()
    {
        Active = TimeSpan.FromHours(1),
        Normal = TimeSpan.FromHours(1),
        Failed = TimeSpan.FromHours(1),
    };

    private static AccountSettings Watching(params WatchedRepository[] repositories) => new() { Watchlist = repositories };

    private static WatchedRepository Watch(long id) => new() { RepositoryId = id, Owner = "octo", Name = $"repo{id}" };

    private static PollingRepositoryMonitor Create(FakeDataSource source, AccountSettings settings, PollingIntervals? intervals = null) =>
        new(Account, settings, source, "octo", TimeProvider.System, NullLogger.Instance, CancellationToken.None, intervals ?? Quiet);

    private static ResourceError Error(ResourceErrorKind kind, DateTimeOffset? retryAt = null) =>
        new(kind, $"synthetic {kind}", DateTimeOffset.UtcNow) { RetryAt = retryAt };

    private static RepositorySnapshot Snapshot(IRepositoryMonitor monitor, long id) => monitor.Repositories.Single(r => r.Key.RepositoryId == id).Snapshot;

    [Fact]
    public async Task Each_section_keeps_its_own_data_and_errors()
    {
        var source = new FakeDataSource();
        using var monitor = Create(source, Watching(Watch(1)));
        await monitor.RefreshAsync(cancellationToken: TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(ResourceAvailability.Available, Snapshot(monitor, 1).PullRequests.Availability);

        // Pull requests start failing; their last good value stays, and the other sections still refresh.
        source.PullRequests = (_, _) => SectionResult<PullRequestsState>.Fail(Error(ResourceErrorKind.ServerError));
        source.Issues = _ => SectionResult<IssuesState>.Ok(new IssuesState { OpenCount = ItemCount.Exact(7) });
        await monitor.RefreshAsync(cancellationToken: TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var snapshot = Snapshot(monitor, 1);
        Assert.NotNull(snapshot.PullRequests.Value);
        Assert.Equal(ResourceErrorKind.ServerError, snapshot.PullRequests.LastError!.Kind);
        Assert.Equal(Freshness.Stale, snapshot.PullRequests.GetFreshness(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(10)));
        Assert.Equal(ItemCount.Exact(7), snapshot.Issues.Value!.OpenCount);
        Assert.Null(snapshot.Actions.LastError);
        Assert.Equal(ConnectionState.Polling, monitor.State);
    }

    [Fact]
    public async Task Losing_access_withholds_every_section_and_stops_section_requests()
    {
        var source = new FakeDataSource();
        using var monitor = Create(source, Watching(Watch(1)));
        await monitor.RefreshAsync(cancellationToken: TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        source.Repository = _ => ApiResult<RepositoryInfo>.Fail(Error(ResourceErrorKind.NotFound));
        source.Calls.Clear();
        await monitor.RefreshAsync(cancellationToken: TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var snapshot = Snapshot(monitor, 1);
        Assert.All(new[] { snapshot.Actions.Availability, snapshot.PullRequests.Availability, snapshot.Issues.Availability },
            a => Assert.Equal(ResourceAvailability.AccessLost, a));
        Assert.Null(snapshot.PullRequests.Value);
        Assert.Equal(["repo 1"], source.Calls);
    }

    [Fact]
    public async Task Network_failures_keep_cached_values_and_report_offline()
    {
        var source = new FakeDataSource();
        using var monitor = Create(source, Watching(Watch(1)));
        await monitor.RefreshAsync(cancellationToken: TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        source.Repository = _ => ApiResult<RepositoryInfo>.Fail(Error(ResourceErrorKind.Network));
        await monitor.RefreshAsync(cancellationToken: TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var snapshot = Snapshot(monitor, 1);
        Assert.NotNull(snapshot.Actions.Value);
        Assert.NotNull(snapshot.Issues.Value);
        Assert.Equal(ResourceErrorKind.Network, snapshot.Issues.LastError!.Kind);
        Assert.Equal(ConnectionState.Offline, monitor.State);
    }

    [Fact]
    public async Task Turned_off_sections_make_no_requests()
    {
        var source = new FakeDataSource();
        using var monitor = Create(source, Watching(Watch(1) with { PullRequests = PullRequestScope.None, ShowIssues = false, Branches = ["release"] }));
        await monitor.RefreshAsync(cancellationToken: TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(["repo 1", "actions repo1 release"], source.Calls);
        var snapshot = Snapshot(monitor, 1);
        Assert.Equal(ResourceAvailability.FeatureUnavailable, snapshot.PullRequests.Availability);
        Assert.Equal(ResourceAvailability.FeatureUnavailable, snapshot.Issues.Availability);
    }

    [Fact]
    public async Task Repositories_with_issues_disabled_on_GitHub_skip_the_issue_request()
    {
        var source = new FakeDataSource { Repository = id => ApiResult<RepositoryInfo>.Ok(FakeDataSource.Info(id, hasIssues: false)) };
        using var monitor = Create(source, Watching(Watch(1)));
        await monitor.RefreshAsync(cancellationToken: TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.DoesNotContain(source.Calls, c => c.StartsWith("issues", StringComparison.Ordinal));
        Assert.Contains("pulls repo1 mine=True login=octo", source.Calls);
        Assert.Equal(ResourceAvailability.FeatureUnavailable, Snapshot(monitor, 1).Issues.Availability);
    }

    [Fact]
    public async Task Only_watched_repositories_are_requested_and_removed_ones_stop()
    {
        var source = new FakeDataSource();
        using var monitor = Create(source, Watching(Watch(1)));
        await monitor.RefreshAsync(cancellationToken: TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.All(source.Calls, c => Assert.Contains("1", c, StringComparison.Ordinal));

        // Adding a repository loads it promptly; removing one stops its requests.
        monitor.ApplyWatchlist(Watching(Watch(1), Watch(2)));
        await monitor.RefreshAsync(new RepositoryKey(Account, 2), TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Contains("repo 2", source.Calls);

        monitor.ApplyWatchlist(Watching(Watch(2)));
        source.Calls.Clear();
        await monitor.RefreshAsync(cancellationToken: TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.DoesNotContain("repo 1", source.Calls);
        Assert.Equal([2L], monitor.Repositories.Select(r => r.Key.RepositoryId));
    }

    [Fact]
    public async Task A_result_for_superseded_options_is_discarded_and_reloaded()
    {
        var source = new FakeDataSource { ActionsGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        source.Actions = request => SectionResult<ActionsState>.Ok(new ActionsState
        {
            DefaultBranch = new CommitWorkflowSummary("abc", [], CheckRollup.From([])) { Branch = request.Branches[0] },
        });
        using var monitor = Create(source, Watching(Watch(1)));
        await source.ActionsEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // The user switches the tracked branch while the old request is in flight.
        monitor.ApplyWatchlist(Watching(Watch(1) with { Branches = ["release"] }));
        var refreshed = monitor.RefreshAsync(cancellationToken: TestContext.Current.CancellationToken);
        source.ActionsGate.SetResult();
        await refreshed.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await WaitUntil(() => Snapshot(monitor, 1).Actions.Value?.DefaultBranch?.Branch == "release");

        Assert.Contains("actions repo1 release", source.Calls);
        Assert.Equal("release", Snapshot(monitor, 1).Actions.Value!.DefaultBranch!.Branch);
    }

    [Fact]
    public async Task A_rate_limit_pauses_all_polling_until_the_reset()
    {
        var source = new FakeDataSource
        {
            Actions = _ => SectionResult<ActionsState>.Fail(Error(ResourceErrorKind.RateLimited, DateTimeOffset.UtcNow.AddMinutes(30))),
        };
        using var monitor = Create(source, Watching(Watch(1), Watch(2)), Quiet with { Normal = TimeSpan.Zero, Failed = TimeSpan.Zero });
        await WaitUntil(() => source.Calls.Contains("actions repo1 main"));
        await Task.Delay(300, TestContext.Current.CancellationToken);

        // Nothing after the rate-limited call: no pull request/issue requests, and repository 2 waits.
        Assert.Equal(["repo 1", "actions repo1 main"], source.Calls);
        _ = monitor.RefreshAsync(cancellationToken: TestContext.Current.CancellationToken);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.Equal(2, source.Calls.Count);
    }

    [Fact]
    public async Task Disposing_stops_all_requests()
    {
        var source = new FakeDataSource();
        var monitor = Create(source, Watching(Watch(1)), Quiet with { Normal = TimeSpan.FromMilliseconds(20) });
        await WaitUntil(() => source.Calls.Count(c => c == "repo 1") >= 2);

        monitor.Dispose();
        await Task.Delay(100, TestContext.Current.CancellationToken);
        var count = source.Calls.Count;
        await Task.Delay(200, TestContext.Current.CancellationToken);

        Assert.Equal(count, source.Calls.Count);
    }

    [Fact]
    public async Task Active_work_is_refreshed_sooner_than_quiet_repositories()
    {
        var running = new WorkflowRun
        {
            Id = 1, WorkflowId = 1, WorkflowName = "CI", RunNumber = 1, RunAttempt = 1, HeadSha = "abc", HeadBranch = "main", Event = "push",
            Outcome = CheckOutcome.Running, HtmlUrl = new Uri("https://github.com/octo/repo1/actions/runs/1"),
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        var source = new FakeDataSource { Actions = _ => SectionResult<ActionsState>.Ok(new ActionsState { RecentRuns = [running] }) };
        using var monitor = Create(source, Watching(Watch(1)), Quiet with { Active = TimeSpan.FromMilliseconds(20) });

        await WaitUntil(() => source.Calls.Count(c => c == "repo 1") >= 3);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(condition());
    }
}
