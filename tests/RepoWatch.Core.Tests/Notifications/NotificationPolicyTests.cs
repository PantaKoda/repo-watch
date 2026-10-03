using RepoWatch.Core.Actions;
using RepoWatch.Core.Identity;
using RepoWatch.Core.Notifications;
using RepoWatch.Core.PullRequests;
using RepoWatch.Core.Settings;
using RepoWatch.Core.State;
using RepoWatch.Core.Status;

namespace RepoWatch.Core.Tests.Notifications;

public sealed class NotificationPolicyTests
{
    private static readonly AccountKey Account = new("github.com", 4242);
    private static readonly RepositoryKey Key = new(Account, 7);
    private static readonly DateTimeOffset T0 = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    private static WorkflowRun Run(long id, string sha, CheckOutcome outcome, int attempt = 1) => new()
    {
        Id = id, WorkflowId = 1, WorkflowName = "CI", RunNumber = (int)id, RunAttempt = attempt, HeadSha = sha, HeadBranch = "main", Event = "push",
        Outcome = outcome, HtmlUrl = new Uri($"https://github.com/octo/hello/actions/runs/{id}"), CreatedAt = T0, UpdatedAt = T0,
    };

    private static PullRequestEntry Pull(int number, params ReviewRequest[] requests) => new()
    {
        PullRequest = new PullRequest
        {
            Id = number, Number = number, Title = $"Change {number}", AuthorLogin = "someone", State = PullRequestState.Open, IsDraft = false,
            HeadSha = "head" + number, HeadRef = "f", BaseRef = "main", RequestedReviewers = requests,
            HtmlUrl = new Uri($"https://github.com/octo/hello/pull/{number}"), CreatedAt = T0, UpdatedAt = T0,
        },
    };

    private static RepositorySnapshot Snapshot(IReadOnlyList<WorkflowRun>? headRuns = null, string sha = "aaa", IReadOnlyList<PullRequestEntry>? pulls = null,
        IReadOnlyList<MergedPullRequest>? merged = null, bool isPrivate = true, IReadOnlyList<WorkflowRun>? recent = null)
    {
        var snapshot = new RepositorySnapshot(Key)
        {
            Metadata = Resource<RepositoryMetadata>.NotLoaded.Succeeded(new RepositoryMetadata
            {
                Key = Key, Owner = "octo", Name = "hello", OwnerKind = RepositoryOwnerKind.User, IsPrivate = isPrivate, IsArchived = false,
                DefaultBranch = "main", HtmlUrl = new Uri("https://github.com/octo/hello"),
            }, T0),
        };
        if (headRuns is not null)
        {
            var health = WorkflowRunSelection.ForCommit(headRuns, sha, "main");
            snapshot = snapshot with
            {
                Actions = Resource<ActionsState>.NotLoaded.Succeeded(new ActionsState { RecentRuns = recent ?? headRuns, DefaultBranch = health, Branches = [health] }, T0),
            };
        }

        if (pulls is not null || merged is not null)
        {
            snapshot = snapshot with
            {
                PullRequests = Resource<PullRequestsState>.NotLoaded.Succeeded(new PullRequestsState
                {
                    Items = pulls ?? [], OpenCount = ItemCount.Exact(pulls?.Count ?? 0), RecentlyMerged = merged ?? [],
                }, T0),
            };
        }

        return snapshot;
    }

    [Fact]
    public void The_first_observation_is_a_silent_baseline()
    {
        var current = Snapshot([Run(1, "aaa", CheckOutcome.Failure)], pulls: [Pull(4, new ReviewRequest(ReviewerKind.User, "octo-test"))]);

        var events = NotificationPolicy.Detect(previous: null, current, "octo-test");

        Assert.Equal([NotificationKind.CiFailure, NotificationKind.ReviewRequested], events.Select(e => e.Kind));
        Assert.All(events, e => Assert.True(e.IsBaseline));
    }

    [Fact]
    public void A_failing_branch_head_is_announced_and_keyed_by_commit_and_attempt()
    {
        var previous = Snapshot([Run(1, "aaa", CheckOutcome.Success)]);
        var failing = Snapshot([Run(2, "bbb", CheckOutcome.Failure)], sha: "bbb");
        var rerunFailing = Snapshot([Run(2, "bbb", CheckOutcome.Failure, attempt: 2)], sha: "bbb");

        var first = Assert.Single(NotificationPolicy.Detect(previous, failing, "octo-test"));
        var again = Assert.Single(NotificationPolicy.Detect(failing, failing, "octo-test"));
        var rerun = Assert.Single(NotificationPolicy.Detect(failing, rerunFailing, "octo-test"));

        Assert.Equal(NotificationKind.CiFailure, first.Kind);
        Assert.False(first.IsBaseline);
        Assert.Equal("ci-failure:7:main:bbb:2.1", first.Key);
        Assert.Equal(first.Key, again.Key); // same event: the history suppresses it
        Assert.Equal("ci-failure:7:main:bbb:2.2", rerun.Key); // a re-run that fails again is a new event
        Assert.Equal(new Uri("https://github.com/octo/hello/actions/runs/2"), first.Url);
    }

    [Fact]
    public void An_old_failed_run_in_the_list_is_not_a_failure_of_the_branch()
    {
        var previous = Snapshot([Run(3, "ccc", CheckOutcome.Success)], sha: "ccc");
        var current = Snapshot([Run(3, "ccc", CheckOutcome.Success)], sha: "ccc", recent: [Run(3, "ccc", CheckOutcome.Success), Run(1, "aaa", CheckOutcome.Failure)]);

        Assert.Empty(NotificationPolicy.Detect(previous, current, "octo-test"));
    }

    [Fact]
    public void Recovery_needs_a_failure_before_it()
    {
        var failing = Snapshot([Run(2, "bbb", CheckOutcome.Failure)], sha: "bbb");
        var passing = Snapshot([Run(3, "ccc", CheckOutcome.Success)], sha: "ccc");

        var recovered = Assert.Single(NotificationPolicy.Detect(failing, passing, "octo-test"));
        Assert.Equal(NotificationKind.CiRecovery, recovered.Kind);
        Assert.Equal("ci-recovery:7:main:ccc", recovered.Key);
        Assert.Empty(NotificationPolicy.Detect(passing, passing, "octo-test"));
    }

    [Fact]
    public void Review_requests_count_only_when_they_ask_the_user_directly()
    {
        var previous = Snapshot(pulls: []);
        var current = Snapshot(pulls:
        [
            Pull(4, new ReviewRequest(ReviewerKind.User, "Octo-Test")),
            Pull(5, new ReviewRequest(ReviewerKind.Team, "acme/octo-test")),
            Pull(6, new ReviewRequest(ReviewerKind.User, "someone-else")),
        ]);

        var review = Assert.Single(NotificationPolicy.Detect(previous, current, "octo-test"));
        Assert.Equal("review:7:4:head4", review.Key);
        Assert.Equal("#4 Change 4", review.Subject);
    }

    [Fact]
    public void Only_tracked_pull_requests_announce_their_merge()
    {
        var previous = Snapshot(pulls: [Pull(4)]);
        var current = Snapshot(pulls: [], merged:
        [
            new MergedPullRequest(4, "Change 4", new Uri("https://github.com/octo/hello/pull/4"), T0),
            new MergedPullRequest(9, "Someone else's", new Uri("https://github.com/octo/hello/pull/9"), T0),
        ]);

        var merged = Assert.Single(NotificationPolicy.Detect(previous, current, "octo-test"));
        Assert.Equal(NotificationKind.PullRequestMerged, merged.Kind);
        Assert.Equal("merged:7:4", merged.Key);
    }

    [Fact]
    public void Private_details_can_be_left_out_of_the_text()
    {
        var failing = NotificationPolicy.Detect(Snapshot([Run(1, "aaa", CheckOutcome.Success)]), Snapshot([Run(2, "bbb", CheckOutcome.Failure)], sha: "bbb"), "x").Single();
        var publicFailing = failing with { IsPrivate = false };

        Assert.Equal(new NotificationText("CI failing in octo/hello", "main is failing."), NotificationPolicy.Text(failing, hidePrivateDetails: false));
        Assert.Equal(new NotificationText("CI failing in a private repository", "A tracked branch is failing."), NotificationPolicy.Text(failing, hidePrivateDetails: true));
        Assert.Equal("CI failing in octo/hello", NotificationPolicy.Text(publicFailing, hidePrivateDetails: true).Title); // public repositories keep their names
    }

    [Fact]
    public void Settings_switch_each_kind_and_repository()
    {
        var failure = new NotificationEvent(NotificationKind.CiFailure, Key, "k", "octo/hello", "main", new Uri("https://github.com/octo/hello"), false, false);
        var settings = new NotificationSettings();

        Assert.True(NotificationPolicy.IsWanted(failure, settings, repositoryEnabled: true));
        Assert.False(NotificationPolicy.IsWanted(failure, settings, repositoryEnabled: false));
        Assert.False(NotificationPolicy.IsWanted(failure, settings with { CiFailure = false }, repositoryEnabled: true));
        Assert.False(NotificationPolicy.IsWanted(failure, settings with { Enabled = false }, repositoryEnabled: true));
        Assert.True(NotificationPolicy.IsWanted(failure with { Kind = NotificationKind.PullRequestMerged }, settings with { CiFailure = false }, repositoryEnabled: true));
    }
}
