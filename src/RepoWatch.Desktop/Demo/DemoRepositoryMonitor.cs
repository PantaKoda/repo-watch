using RepoWatch.Core.Actions;
using RepoWatch.Core.Identity;
using RepoWatch.Core.Issues;
using RepoWatch.Core.Monitoring;
using RepoWatch.Core.PullRequests;
using RepoWatch.Core.Settings;
using RepoWatch.Core.State;
using RepoWatch.Core.Status;

namespace RepoWatch.Desktop.Demo;

/// <summary>
/// DEMO DATA. Synthetic repositories that exercise every UI state (failing, running, stale,
/// unavailable, empty, access lost). Nothing here comes from GitHub, and no network calls are made.
/// Used only in explicit demo mode, which the UI labels on screen.
/// </summary>
public sealed class DemoRepositoryMonitor : IRepositoryMonitor, IDisposable
{
    public static readonly AccountKey DemoAccount = new("demo.invalid", 1);

    private static readonly TimeSpan SimulatedLatency = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan AutoRefreshInterval = TimeSpan.FromSeconds(20);

    // Links point at GitHub documentation because the demo repositories do not exist.
    private static readonly Uri ActionsDocs = new("https://docs.github.com/actions");
    private static readonly Uri PullRequestDocs = new("https://docs.github.com/pull-requests");
    private static readonly Uri IssueDocs = new("https://docs.github.com/issues");
    private static readonly Uri RepoDocs = new("https://docs.github.com/repositories");

    private readonly TimeProvider _time;
    private readonly ITimer _autoRefresh;
    private readonly Lock _gate = new();
    private int _tick;
    private bool _refreshing;
    private IReadOnlyList<MonitoredRepository> _repositories;

    public DemoRepositoryMonitor(TimeProvider time)
    {
        _time = time;
        _repositories = Build(_time.GetUtcNow(), tick: 0);
        _autoRefresh = _time.CreateTimer(_ => _ = RefreshAsync(), null, AutoRefreshInterval, AutoRefreshInterval);
    }

    public ConnectionState State => ConnectionState.Demo;

    public IReadOnlyList<MonitoredRepository> Repositories
    {
        get
        {
            lock (_gate)
            {
                return _repositories;
            }
        }
    }

    public bool IsRefreshing => Volatile.Read(ref _refreshing);

    public event EventHandler? Changed;

    public async Task RefreshAsync(RepositoryKey? repository = null, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_refreshing)
            {
                return;
            }

            _refreshing = true;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        try
        {
            await Task.Delay(SimulatedLatency, _time, cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                _tick++;
                _repositories = Build(_time.GetUtcNow(), _tick);
            }
        }
        finally
        {
            Volatile.Write(ref _refreshing, false);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose() => _autoRefresh.Dispose();

    private static IReadOnlyList<MonitoredRepository> Build(DateTimeOffset now, int tick)
    {
        return
        [
            WebApp(now, 0),
            ApiService(now, tick, 1),
            Dotfiles(now, 2),
            LegacyTool(now, 3),
            Retired(now, 4),
        ];
    }

    // Default branch failing; open PRs with mixed checks and reviews.
    private static MonitoredRepository WebApp(DateTimeOffset now, int index)
    {
        var key = new RepositoryKey(DemoAccount, 101);
        const string head = "c0ffee0000000000000000000000000000000001";
        var runs = new[]
        {
            Run(5001, 1, "CI", 412, 1, head, "main", "push", CheckOutcome.Failure, now.AddMinutes(-7)),
            Run(5002, 2, "Deploy preview", 88, 1, head, "main", "push", CheckOutcome.Skipped, now.AddMinutes(-7)),
            Run(4990, 1, "CI", 411, 2, "c0ffee0000000000000000000000000000000003", "demo/61", "pull_request", CheckOutcome.Success, now.AddMinutes(-40)),
        };

        var prHead = "c0ffee0000000000000000000000000000000003";
        var pullRequests = new PullRequestsState
        {
            OpenCount = ItemCount.Exact(3),
            Items =
            [
                PullRequestEntry(now, 61, "Add login rate limiting", "demo-dev", prHead, MergeState.Blocked,
                    CommitChecks.Summarize(prHead, [Check(1, 11, "CI / build", CheckOutcome.Failure, prHead), Check(2, 11, "CI / test", CheckOutcome.Success, prHead)], []),
                    ReviewSummary.From([], [new ReviewRequest(ReviewerKind.User, "demo-you")], prHead)),
                PullRequestEntry(now, 58, "Upgrade bundler configuration", "demo-bot", "c0ffee0000000000000000000000000000000004", MergeState.Unknown,
                    CommitChecks.Summarize("c0ffee0000000000000000000000000000000004", [Check(3, 12, "CI / build", CheckOutcome.Running, "c0ffee0000000000000000000000000000000004")], []),
                    ReviewSummary.From([Review(1, "demo-lead", ReviewState.Approved, "c0ffee0000000000000000000000000000000004", now.AddHours(-2))], [], "c0ffee0000000000000000000000000000000004")),
                PullRequestEntry(now, 55, "Draft: new settings page", "demo-you", "c0ffee0000000000000000000000000000000005", MergeState.Draft,
                    CommitChecks.Summarize("c0ffee0000000000000000000000000000000005", [], []),
                    ReviewSummary.From([], [], "c0ffee0000000000000000000000000000000005"), isDraft: true),
            ],
        };

        return Repository(key, "demo-org", "web-app", isPrivate: false, isArchived: false, index, now, now.AddMinutes(-8),
            actions: Resource<ActionsState>.NotLoaded.Succeeded(new ActionsState
            {
                RecentRuns = runs,
                DefaultBranch = WorkflowRunSelection.ForCommit(runs, head, "main"),
            }, now),
            pullRequests: Resource<PullRequestsState>.NotLoaded.Succeeded(pullRequests, now),
            issues: Resource<IssuesState>.NotLoaded.Succeeded(new IssuesState
            {
                OpenCount = ItemCount.AtLeast(30),
                Items =
                [
                    Issue(now, 140, "Login button misaligned on narrow screens", "demo-user", ["bug", "ui"]),
                    Issue(now, 137, "Document the deploy preview workflow", "demo-dev", ["docs"]),
                ],
            }, now));
    }

    // A workflow that progresses queued -> running -> success across refreshes.
    private static MonitoredRepository ApiService(DateTimeOffset now, int tick, int index)
    {
        var key = new RepositoryKey(DemoAccount, 102);
        const string head = "beef000000000000000000000000000000000001";
        var outcome = (tick % 3) switch
        {
            0 => CheckOutcome.Queued,
            1 => CheckOutcome.Running,
            _ => CheckOutcome.Success,
        };
        var runs = new[]
        {
            Run(6001, 3, "Build and test", 977, 1, head, "main", "push", outcome, now.AddMinutes(-1)),
            Run(5999, 4, "CodeQL", 120, 1, head, "main", "push", CheckOutcome.Success, now.AddMinutes(-3)),
        };

        return Repository(key, "demo-org", "api-service", isPrivate: true, isArchived: false, index, now, now.AddMinutes(-2),
            actions: Resource<ActionsState>.NotLoaded.Succeeded(new ActionsState { RecentRuns = runs, DefaultBranch = WorkflowRunSelection.ForCommit(runs, head, "main") }, now),
            pullRequests: Resource<PullRequestsState>.NotLoaded.Succeeded(new PullRequestsState { OpenCount = ItemCount.Exact(0) }, now),
            issues: Resource<IssuesState>.NotLoaded.Succeeded(new IssuesState
            {
                OpenCount = ItemCount.Exact(1),
                Items = [Issue(now, 12, "Rotate demo signing key", "demo-lead", ["security"])],
            }, now));
    }

    // No workflows at all, issues turned off for the repository.
    private static MonitoredRepository Dotfiles(DateTimeOffset now, int index)
    {
        var key = new RepositoryKey(DemoAccount, 103);
        return Repository(key, "demo-user", "dotfiles", isPrivate: true, isArchived: false, index, now, now.AddDays(-12),
            actions: Resource<ActionsState>.NotLoaded.Succeeded(new ActionsState
            {
                DefaultBranch = WorkflowRunSelection.ForCommit([], "abc0000000000000000000000000000000000001"),
            }, now),
            pullRequests: Resource<PullRequestsState>.NotLoaded.Succeeded(new PullRequestsState { OpenCount = ItemCount.Exact(0) }, now),
            issues: Resource<IssuesState>.NotLoaded.FeatureUnavailable(now));
    }

    // Pull requests failed to refresh: older data stays visible but marked stale.
    private static MonitoredRepository LegacyTool(DateTimeOffset now, int index)
    {
        var key = new RepositoryKey(DemoAccount, 104);
        const string head = "dead000000000000000000000000000000000001";
        var runs = new[] { Run(7001, 5, "Nightly", 300, 1, head, "main", "schedule", CheckOutcome.Success, now.AddHours(-9)) };
        var stalePullRequests = Resource<PullRequestsState>.NotLoaded
            .Succeeded(new PullRequestsState
            {
                OpenCount = ItemCount.Exact(1),
                Items =
                [
                    PullRequestEntry(now.AddHours(-1), 9, "Replace deprecated API", "demo-dev", "dead000000000000000000000000000000000002", MergeState.Clean,
                        CommitChecks.Summarize("dead000000000000000000000000000000000002", [Check(4, 13, "build", CheckOutcome.Success, "dead000000000000000000000000000000000002")], []),
                        ReviewSummary.From([], [], "dead000000000000000000000000000000000002")),
                ],
            }, now.AddMinutes(-45))
            .Failed(new ResourceError(ResourceErrorKind.ServerError, "GitHub returned a server error (demo).", now.AddMinutes(-1)));

        return Repository(key, "demo-org", "legacy-tool", isPrivate: false, isArchived: true, index, now, now.AddDays(-95),
            actions: Resource<ActionsState>.NotLoaded.Succeeded(new ActionsState { RecentRuns = runs, DefaultBranch = WorkflowRunSelection.ForCommit(runs, head, "main") }, now),
            pullRequests: stalePullRequests,
            issues: Resource<IssuesState>.NotLoaded.Succeeded(new IssuesState { OpenCount = ItemCount.Exact(0) }, now));
    }

    // Access was lost: cached content is withheld.
    private static MonitoredRepository Retired(DateTimeOffset now, int index)
    {
        var key = new RepositoryKey(DemoAccount, 105);
        var snapshot = new RepositorySnapshot(key).WithAccessLost(
            new ResourceError(ResourceErrorKind.NotFound, "This repository is no longer accessible to Repo Watch (demo).", now.AddMinutes(-5)));
        return new MonitoredRepository(new WatchedRepository { RepositoryId = key.RepositoryId, Owner = "demo-org", Name = "retired-service" }, snapshot, index);
    }

    private static MonitoredRepository Repository(
        RepositoryKey key, string owner, string name, bool isPrivate, bool isArchived, int index, DateTimeOffset now, DateTimeOffset pushedAt,
        Resource<ActionsState> actions, Resource<PullRequestsState> pullRequests, Resource<IssuesState> issues)
    {
        var metadata = new RepositoryMetadata
        {
            Key = key,
            Owner = owner,
            Name = name,
            OwnerKind = owner == "demo-user" ? RepositoryOwnerKind.User : RepositoryOwnerKind.Organization,
            IsPrivate = isPrivate,
            IsArchived = isArchived,
            DefaultBranch = "main",
            HtmlUrl = RepoDocs,
            PushedAt = pushedAt,
        };

        var snapshot = new RepositorySnapshot(key)
        {
            Metadata = Resource<RepositoryMetadata>.NotLoaded.Succeeded(metadata, now),
            Actions = actions,
            PullRequests = pullRequests,
            Issues = issues,
        };

        return new MonitoredRepository(new WatchedRepository { RepositoryId = key.RepositoryId, Owner = owner, Name = name }, snapshot, index);
    }

    private static WorkflowRun Run(long id, long workflowId, string workflow, int number, int attempt, string sha, string branch, string @event, CheckOutcome outcome, DateTimeOffset at) => new()
    {
        Id = id,
        WorkflowId = workflowId,
        WorkflowName = workflow,
        RunNumber = number,
        RunAttempt = attempt,
        HeadSha = sha,
        HeadBranch = branch,
        Event = @event,
        Outcome = outcome,
        HtmlUrl = ActionsDocs,
        CreatedAt = at,
        UpdatedAt = at,
    };

    private static CheckRun Check(long id, long suite, string name, CheckOutcome outcome, string sha) => new()
    {
        Id = id,
        CheckSuiteId = suite,
        AppId = 1,
        Name = name,
        HeadSha = sha,
        Outcome = outcome,
        HtmlUrl = ActionsDocs,
    };

    private static PullRequestReview Review(long id, string author, ReviewState state, string sha, DateTimeOffset at) => new()
    {
        Id = id,
        AuthorLogin = author,
        State = state,
        CommitSha = sha,
        SubmittedAt = at,
        HtmlUrl = PullRequestDocs,
    };

    private static PullRequestEntry PullRequestEntry(
        DateTimeOffset now, int number, string title, string author, string head, MergeState merge,
        CommitChecksSummary checks, ReviewSummary reviews, bool isDraft = false) => new()
        {
            PullRequest = new PullRequest
            {
                Id = 90000 + number,
                Number = number,
                Title = title,
                AuthorLogin = author,
                State = PullRequestState.Open,
                IsDraft = isDraft,
                HeadSha = head,
                HeadRef = $"demo/{number}",
                BaseRef = "main",
                RequestedReviewers = reviews.PendingRequests,
                MergeState = merge,
                CommentCount = number % 6,
                HtmlUrl = PullRequestDocs,
                CreatedAt = now.AddDays(-number % 5 - 1),
                UpdatedAt = now.AddHours(-(number % 7)),
            },
            Checks = Resource<CommitChecksSummary>.NotLoaded.Succeeded(checks, now),
            Reviews = Resource<ReviewSummary>.NotLoaded.Succeeded(reviews, now),
        };

    private static Issue Issue(DateTimeOffset now, int number, string title, string author, IReadOnlyList<string> labels) => new()
    {
        CommentCount = number % 4,
        Id = 80000 + number,
        Number = number,
        Title = title,
        AuthorLogin = author,
        State = IssueState.Open,
        Labels = labels,
        HtmlUrl = IssueDocs,
        CreatedAt = now.AddDays(-3),
        UpdatedAt = now.AddHours(-number % 11),
    };
}
