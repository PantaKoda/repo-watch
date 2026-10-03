using System.Net;
using System.Text.Json;
using RepoWatch.Core.Identity;
using RepoWatch.Core.PullRequests;
using RepoWatch.Core.State;
using RepoWatch.Core.Status;
using RepoWatch.GitHub.Api;
using RepoWatch.GitHub.Repositories;
using static RepoWatch.GitHub.Tests.Fixture;

namespace RepoWatch.GitHub.Tests;

/// <summary>
/// Contract tests for repository data. Fixture bodies follow GitHub's documented REST and GraphQL
/// shapes; all values are synthetic.
/// </summary>
public sealed class RepositoryDataTests
{
    private const string MainSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string PrSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly RepositoryKey Key = new(Account, 1296269);

    private sealed class FakeTokens : IAccessTokenSource
    {
        public Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>("ghu_current");

        public Task<string?> HandleUnauthorizedAsync(string rejectedAccessToken, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }

    private static (RepositoryDataClient Client, StubHandler Handler) Create()
    {
        var time = Time();
        var handler = new StubHandler(time);
        var api = new GitHubApiClient(GitHubHttp.CreateClient(handler), Endpoints, new FakeTokens(), time);
        return (new RepositoryDataClient(api), handler);
    }

    private static string Run(long id, long workflowId, string name, string sha, string branch, string status, string? conclusion, int attempt = 1, int number = 1, string created = "2026-10-03T11:00:00Z") =>
        $$"""{"id":{{id}},"workflow_id":{{workflowId}},"name":"{{name}}","run_number":{{number}},"run_attempt":{{attempt}},"head_sha":"{{sha}}","head_branch":"{{branch}}","event":"push","status":"{{status}}","conclusion":{{(conclusion is null ? "null" : $"\"{conclusion}\"")}},"html_url":"https://github.com/octo-test/hello/actions/runs/{{id}}","created_at":"{{created}}","updated_at":"{{created}}","pull_requests":[]}""";

    private static string Runs(params string[] runs) => $$"""{"total_count":{{runs.Length}},"workflow_runs":[{{string.Join(",", runs)}}]}""";

    private static string Ref(string sha) => $$"""{"state":"pending","sha":"{{sha}}","total_count":0,"statuses":[]}""";

    [Fact]
    public async Task Metadata_is_loaded_by_id_and_follows_renames()
    {
        var (client, handler) = Create();
        handler.Json("""{"id":1296269,"name":"hello-renamed","full_name":"new-owner/hello-renamed","owner":{"login":"new-owner","type":"Organization"},"private":true,"archived":false,"has_issues":false,"default_branch":"trunk","html_url":"https://github.com/new-owner/hello-renamed"}""");

        var result = await client.GetRepositoryAsync(Key, TestContext.Current.CancellationToken);

        Assert.Equal("https://api.github.com/repositories/1296269", handler.Requests.Single().Uri.ToString());
        var info = result.Value!;
        Assert.Equal("new-owner/hello-renamed", info.Metadata.FullName);
        Assert.Equal(RepositoryOwnerKind.Organization, info.Metadata.OwnerKind);
        Assert.True(info.Metadata.IsPrivate);
        Assert.Equal("trunk", info.Metadata.DefaultBranch);
        Assert.False(info.HasIssues);
    }

    [Fact]
    public async Task A_lost_repository_is_reported_as_not_found()
    {
        var (client, handler) = Create();
        handler.Status(HttpStatusCode.NotFound);

        var result = await client.GetRepositoryAsync(Key, TestContext.Current.CancellationToken);

        Assert.Equal(ResourceErrorKind.NotFound, result.Error!.Kind);
    }

    [Fact]
    public async Task Branch_health_uses_the_branch_head_and_the_latest_attempt_of_each_run()
    {
        var (client, handler) = Create();
        handler
            .Json(Runs(
                Run(30, 7, "CI", MainSha, "main", "completed", "failure", attempt: 2, number: 12),
                Run(29, 8, "Docs", PrSha, "feature", "in_progress", null, number: 5, created: "2026-10-03T11:30:00Z")))
            .Json(Ref(MainSha))
            // runs for the head commit: CI failed on attempt 2 after attempt 1 succeeded; an older
            // run of CI for the same commit and a run on another branch must not count.
            .Json(Runs(
                Run(30, 7, "CI", MainSha, "main", "completed", "success", attempt: 1, number: 12),
                Run(30, 7, "CI", MainSha, "main", "completed", "failure", attempt: 2, number: 12),
                Run(25, 7, "CI", MainSha, "main", "completed", "success", number: 11),
                Run(31, 7, "CI", MainSha, "release", "completed", "success", number: 13)));

        var result = await client.GetActionsAsync(new ActionsRequest("octo-test", "hello", ["main"], []), TestContext.Current.CancellationToken);

        Assert.Equal("https://api.github.com/repos/octo-test/hello/commits/main/status?per_page=1", handler.Requests[1].Uri.ToString());
        Assert.Contains($"head_sha={MainSha}", handler.Requests[2].Uri.Query, StringComparison.Ordinal);
        var actions = result.Value!;
        Assert.Equal([29L, 30L], actions.RecentRuns.Select(r => r.Id)); // newest first
        var health = actions.DefaultBranch!;
        Assert.Equal("main", health.Branch);
        Assert.Equal(RollupState.Failing, health.Rollup.State);
        Assert.Equal(2, health.Runs.Single().RunAttempt);
    }

    [Fact]
    public async Task Workflow_filters_apply_to_recent_runs_and_branch_health()
    {
        var (client, handler) = Create();
        handler
            .Json(Runs(Run(30, 7, "CI", MainSha, "main", "completed", "failure"), Run(31, 8, "Docs", MainSha, "main", "completed", "success")))
            .Json(Ref(MainSha))
            .Json(Runs(Run(30, 7, "CI", MainSha, "main", "completed", "failure"), Run(31, 8, "Docs", MainSha, "main", "completed", "success")));

        var result = await client.GetActionsAsync(new ActionsRequest("octo-test", "hello", ["main"], [8]), TestContext.Current.CancellationToken);

        Assert.Equal([31L], result.Value!.RecentRuns.Select(r => r.Id));
        Assert.Equal(RollupState.Passing, result.Value.DefaultBranch!.Rollup.State);
    }

    [Fact]
    public async Task Missing_branches_are_skipped_and_disabled_actions_are_unavailable()
    {
        var (client, handler) = Create();
        handler.Json(Runs()).Status(HttpStatusCode.NotFound).Json(Ref(MainSha)).Json(Runs());

        var result = await client.GetActionsAsync(new ActionsRequest("octo-test", "hello", ["gone", "main"], []), TestContext.Current.CancellationToken);

        var health = Assert.Single(result.Value!.Branches);
        Assert.Equal("main", health.Branch);
        Assert.Equal(RollupState.NoChecks, health.Rollup.State); // loaded, and no checks: not "unknown"

        var (disabled, disabledHandler) = Create();
        disabledHandler.Status(HttpStatusCode.NotFound);
        var unavailable = await disabled.GetActionsAsync(new ActionsRequest("octo-test", "hello", ["main"], []), TestContext.Current.CancellationToken);
        Assert.True(unavailable.FeatureUnavailable);
    }

    [Fact]
    public async Task A_failing_branch_lookup_fails_the_section_instead_of_hiding_the_branch()
    {
        var (client, handler) = Create();
        handler.Json(Runs()).Status(HttpStatusCode.InternalServerError);

        var result = await client.GetActionsAsync(new ActionsRequest("octo-test", "hello", ["main"], []), TestContext.Current.CancellationToken);

        Assert.Equal(ResourceErrorKind.ServerError, result.Error!.Kind);
    }

    private static string PullRequest(int number, string author, string mergeable = "MERGEABLE", bool draft = false, string reviewRequests = "", string reviews = "", string contexts = "", int contextTotal = -1)
    {
        var total = contextTotal < 0 ? contexts.Split("__typename").Length - 1 : contextTotal;
        var rollup = contexts.Length == 0 && contextTotal < 0 ? "null" : "{\"contexts\":{\"totalCount\":" + total + ",\"nodes\":[" + contexts + "]}}";
        return "{\"databaseId\":" + (number * 100) + ",\"number\":" + number + ",\"title\":\"PR " + number + " <script>\",\"url\":\"https://github.com/octo-test/hello/pull/" + number + "\","
            + "\"isDraft\":" + (draft ? "true" : "false") + ",\"createdAt\":\"2026-10-01T10:00:00Z\",\"updatedAt\":\"2026-10-03T10:00:00Z\",\"mergeable\":\"" + mergeable + "\","
            + "\"headRefName\":\"feature-" + number + "\",\"baseRefName\":\"main\",\"headRefOid\":\"" + PrSha + "\",\"author\":{\"login\":\"" + author + "\"},"
            + "\"reviewRequests\":{\"totalCount\":0,\"nodes\":[" + reviewRequests + "]},"
            + "\"reviews\":{\"totalCount\":0,\"nodes\":[" + reviews + "]},"
            + "\"commits\":{\"totalCount\":1,\"nodes\":[{\"commit\":{\"statusCheckRollup\":" + rollup + "}}]}}";
    }
    private static string CheckRun(long id, string name, long suite, string status, string? conclusion) =>
        $$$$"""{"__typename":"CheckRun","databaseId":{{{{id}}}},"name":"{{{{name}}}}","status":"{{{{status}}}}","conclusion":{{{{(conclusion is null ? "null" : $"\"{conclusion}\"")}}}},"url":"https://github.com/octo-test/hello/runs/{{{{id}}}}","startedAt":"2026-10-03T10:00:00Z","completedAt":null,"checkSuite":{"databaseId":{{{{suite}}}},"app":{"databaseId":15368}}}""";

    private static string Status(string context, string state) =>
        $$"""{"__typename":"StatusContext","context":"{{context}}","state":"{{state}}","targetUrl":"https://ci.example.test/{{context}}","createdAt":"2026-10-03T10:00:00Z"}""";

    private static string Review(long id, string author, string state, string sha, string submitted = "2026-10-02T10:00:00Z") =>
        $$$"""{"databaseId":{{{id}}},"state":"{{{state}}}","submittedAt":"{{{submitted}}}","url":"https://github.com/octo-test/hello/pull/1#pullrequestreview-{{{id}}}","author":{"login":"{{{author}}}"},"commit":{"oid":"{{{sha}}}"}}""";

    private static string PullRequests(int total, params string[] nodes) =>
        $$$$$"""{"data":{"repository":{"pullRequests":{"totalCount":{{{{{total}}}}},"nodes":[{{{{{string.Join(",", nodes)}}}}}]}}}}""";

    [Fact]
    public async Task Pull_requests_keep_reviews_checks_and_mergeability_separate()
    {
        var (client, handler) = Create();
        handler.Json(PullRequests(57,
            PullRequest(1, "octo-test",
                reviewRequests: """{"requestedReviewer":{"__typename":"Team","slug":"core","organization":{"login":"acme"}}}""",
                reviews: string.Join(",", Review(1, "alice", "APPROVED", "0000000000000000000000000000000000000000"), Review(2, "bob", "CHANGES_REQUESTED", PrSha), Review(3, "bob", "COMMENTED", PrSha, "2026-10-02T11:00:00Z")),
                contexts: string.Join(",", CheckRun(10, "build", 900, "COMPLETED", "FAILURE"), CheckRun(11, "build", 900, "COMPLETED", "SUCCESS"), CheckRun(12, "build", 901, "IN_PROGRESS", null), Status("ci/legacy", "FAILURE")))));

        var result = await client.GetPullRequestsAsync("octo-test", "hello", mineOnly: false, "octo-test", Time().GetUtcNow(), TestContext.Current.CancellationToken);

        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.github.com/graphql", request.Uri.ToString());
        using (var body = JsonDocument.Parse(request.Body!))
        {
            Assert.Equal("octo-test", body.RootElement.GetProperty("variables").GetProperty("owner").GetString());
        }

        var state = result.Value!;
        Assert.Equal(ItemCount.Exact(57), state.OpenCount); // totalCount, not the page size
        var entry = Assert.Single(state.Items);
        Assert.Equal("PR 1 <script>", entry.PullRequest.Title); // carried as plain text
        Assert.Equal(MergeState.Clean, entry.PullRequest.MergeState);

        var checks = entry.Checks.Value!;
        Assert.Equal(2, checks.CheckRuns.Count); // re-run 11 supersedes 10 in suite 900; suite 901 counted separately
        Assert.Equal(RollupState.Failing, checks.Rollup.State); // the legacy status failed
        Assert.True(checks.IsComplete);

        var reviews = entry.Reviews.Value!;
        Assert.Equal(1, reviews.Approvals);
        Assert.Equal(1, reviews.ApprovalsOnOlderCommits);
        Assert.Equal(1, reviews.ChangesRequested); // a later comment doesn't clear bob's decision
        Assert.Equal(new ReviewRequest(ReviewerKind.Team, "acme/core"), Assert.Single(reviews.PendingRequests));
    }

    [Fact]
    public async Task Mine_keeps_authored_and_directly_requested_pull_requests_only()
    {
        var (client, handler) = Create();
        handler.Json(PullRequests(3,
            PullRequest(1, "Octo-Test"),
            PullRequest(2, "someone", reviewRequests: """{"requestedReviewer":{"__typename":"User","login":"octo-test"}}"""),
            PullRequest(3, "someone", reviewRequests: """{"requestedReviewer":{"__typename":"Team","slug":"core","organization":{"login":"acme"}}}""")));

        var result = await client.GetPullRequestsAsync("octo-test", "hello", mineOnly: true, "octo-test", Time().GetUtcNow(), TestContext.Current.CancellationToken);

        // A team request is not "for me" without established membership.
        Assert.Equal([1, 2], result.Value!.Items.Select(e => e.PullRequest.Number));
        Assert.Equal(ItemCount.Exact(3), result.Value.OpenCount);
    }

    [Fact]
    public async Task Incomplete_checks_never_roll_up_to_passing_and_missing_checks_are_no_checks()
    {
        var (client, handler) = Create();
        handler.Json(PullRequests(2,
            PullRequest(1, "a", contexts: CheckRun(10, "build", 900, "COMPLETED", "SUCCESS"), contextTotal: 150),
            PullRequest(2, "b", mergeable: "UNKNOWN", draft: true)));

        var result = await client.GetPullRequestsAsync("octo-test", "hello", mineOnly: false, "octo-test", Time().GetUtcNow(), TestContext.Current.CancellationToken);

        var partial = result.Value!.Items[0].Checks.Value!;
        Assert.False(partial.IsComplete);
        Assert.Equal(RollupState.Unknown, partial.Rollup.State);

        var draft = result.Value.Items[1];
        Assert.Equal(RollupState.NoChecks, draft.Checks.Value!.Rollup.State);
        Assert.Equal(MergeState.Draft, draft.PullRequest.MergeState);
    }

    [Fact]
    public async Task GraphQL_errors_are_classified()
    {
        var (client, handler) = Create();
        handler.Json("""{"data":{"repository":null},"errors":[{"type":"NOT_FOUND","path":["repository"],"message":"Could not resolve to a Repository with the name 'octo-test/hello'."}]}""");

        var result = await client.GetPullRequestsAsync("octo-test", "hello", mineOnly: false, "octo-test", Time().GetUtcNow(), TestContext.Current.CancellationToken);

        Assert.Equal(ResourceErrorKind.NotFound, result.Error!.Kind);
        Assert.DoesNotContain("octo-test/hello", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Issue_counts_are_exact_and_exclude_pull_requests()
    {
        var (client, handler) = Create();
        handler.Json("""
            {"data":{"repository":{"hasIssuesEnabled":true,"issues":{"totalCount":143,"nodes":[
              {"databaseId":501,"number":12,"title":"Crash on start","url":"https://github.com/octo-test/hello/issues/12","createdAt":"2026-09-01T10:00:00Z","updatedAt":"2026-10-03T09:00:00Z",
               "author":{"login":"alice"},"labels":{"totalCount":1,"nodes":[{"name":"bug"}]},"assignees":{"totalCount":1,"nodes":[{"login":"octo-test"}]}},
              {"databaseId":502,"number":13,"title":"Ghost author","url":"https://github.com/octo-test/hello/issues/13","createdAt":"2026-09-01T10:00:00Z","updatedAt":"2026-10-02T09:00:00Z",
               "author":null,"labels":{"totalCount":0,"nodes":[]},"assignees":{"totalCount":0,"nodes":[]}}
            ]}}}}
            """);

        var result = await client.GetIssuesAsync("octo-test", "hello", TestContext.Current.CancellationToken);

        var issues = result.Value!;
        Assert.Equal(ItemCount.Exact(143), issues.OpenCount);
        Assert.Equal([12, 13], issues.Items.Select(i => i.Number));
        Assert.Equal(["bug"], issues.Items[0].Labels);
        Assert.Equal("ghost", issues.Items[1].AuthorLogin);
    }

    [Fact]
    public async Task Disabled_issues_are_unavailable_rather_than_empty()
    {
        var (client, handler) = Create();
        handler.Json("""{"data":{"repository":{"hasIssuesEnabled":false,"issues":{"totalCount":0,"nodes":[]}}}}""");

        var result = await client.GetIssuesAsync("octo-test", "hello", TestContext.Current.CancellationToken);

        Assert.True(result.FeatureUnavailable);
    }

    [Fact]
    public async Task Rate_limited_GraphQL_requests_are_classified_with_their_reset_time()
    {
        var (client, handler) = Create();
        var reset = Time().GetUtcNow().AddMinutes(10).ToUnixTimeSeconds();
        handler.JsonWithHeaders("""{"message":"API rate limit exceeded"}""", HttpStatusCode.Forbidden,
            ("x-ratelimit-remaining", "0"), ("x-ratelimit-reset", reset.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        var result = await client.GetIssuesAsync("octo-test", "hello", TestContext.Current.CancellationToken);

        Assert.Equal(ResourceErrorKind.RateLimited, result.Error!.Kind);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(reset), result.Error.RetryAt);
    }
}
