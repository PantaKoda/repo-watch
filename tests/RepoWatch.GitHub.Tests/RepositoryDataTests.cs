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
        handler.Json("""{"id":1296269,"name":"hello-renamed","full_name":"new-owner/hello-renamed","owner":{"login":"new-owner","type":"Organization"},"private":true,"archived":false,"has_issues":false,"default_branch":"trunk","html_url":"https://github.com/new-owner/hello-renamed","pushed_at":"2026-10-02T08:30:00Z"}""");

        var result = await client.GetRepositoryAsync(Key, TestContext.Current.CancellationToken);

        Assert.Equal("https://api.github.com/repositories/1296269", handler.Requests.Single().Uri.ToString());
        var info = result.Value!;
        Assert.Equal("new-owner/hello-renamed", info.Metadata.FullName);
        Assert.Equal(RepositoryOwnerKind.Organization, info.Metadata.OwnerKind);
        Assert.True(info.Metadata.IsPrivate);
        Assert.Equal("trunk", info.Metadata.DefaultBranch);
        Assert.False(info.HasIssues);
        Assert.Equal(new DateTimeOffset(2026, 10, 2, 8, 30, 0, TimeSpan.Zero), info.Metadata.PushedAt);
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
    public async Task A_run_keeps_only_pull_requests_of_its_own_repository_and_names_its_head_owner()
    {
        var (client, handler) = Create();
        // A watched fork (repository 42): GitHub also lists the upstream pull request (base repository 99).
        var run = """
            {"id":40,"workflow_id":7,"name":"CI","run_number":3,"run_attempt":1,"head_sha":"PR_SHA","head_branch":"patch-1","event":"pull_request",
             "status":"completed","conclusion":"success","html_url":"https://github.com/octo-test/hello/actions/runs/40","created_at":"2026-10-03T11:00:00Z","updated_at":"2026-10-03T11:00:00Z",
             "repository":{"id":42},"head_repository":{"owner":{"login":"forker"}},
             "pull_requests":[{"number":900,"base":{"repo":{"id":99}}},{"number":7,"base":{"repo":{"id":42}}},{"number":8}]}
            """.Replace("PR_SHA", PrSha, StringComparison.Ordinal);
        handler.Json(Runs(run)).Json(Ref(MainSha)).Json(Runs());

        var result = await client.GetActionsAsync(new ActionsRequest("octo-test", "hello", ["main"], []), TestContext.Current.CancellationToken);

        var mapped = Assert.Single(result.Value!.RecentRuns);
        Assert.Equal([7], mapped.PullRequestNumbers); // not upstream's #900, nor one whose base repository is unknown
        Assert.Equal("forker", mapped.HeadOwner);
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

        // The missing primary branch is reported, never silently replaced by the next one.
        Assert.Null(result.Value.DefaultBranch);
        Assert.Equal(["gone"], result.Value.MissingBranches);

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

    private static string PullRequest(int number, string author, string mergeable = "MERGEABLE", bool draft = false, string reviewRequests = "", string reviews = "", int? comments = null) =>
        "{\"databaseId\":" + (number * 100) + ",\"number\":" + number + ",\"title\":\"PR " + number + " <script>\",\"url\":\"https://github.com/octo-test/hello/pull/" + number + "\","
        + "\"isDraft\":" + (draft ? "true" : "false") + ",\"createdAt\":\"2026-10-01T10:00:00Z\",\"updatedAt\":\"2026-10-03T10:0" + (number % 10) + ":00Z\",\"mergeable\":\"" + mergeable + "\","
        + "\"headRefName\":\"feature-" + number + "\",\"baseRefName\":\"main\",\"headRefOid\":\"" + PrSha + "\",\"author\":{\"login\":\"" + author + "\"},\"headRepositoryOwner\":{\"login\":\"octo-test\"},"
        + "\"reviewRequests\":{\"totalCount\":0,\"nodes\":[" + reviewRequests + "]},"
        + (comments is { } count ? "\"totalCommentsCount\":" + count + "," : "")
        + "\"reviews\":{\"totalCount\":0,\"nodes\":[" + reviews + "]}}";

    // REST shapes for GET /commits/{sha}/check-runs and /commits/{sha}/status.
    private static string CheckRun(long id, string name, long suite, string status, string? conclusion) =>
        "{\"id\":" + id + ",\"name\":\"" + name + "\",\"head_sha\":\"" + PrSha + "\",\"status\":\"" + status + "\",\"conclusion\":" + (conclusion is null ? "null" : "\"" + conclusion + "\"")
        + ",\"html_url\":\"https://github.com/octo-test/hello/runs/" + id + "\",\"started_at\":\"2026-10-03T10:00:00Z\",\"completed_at\":null,\"check_suite\":{\"id\":" + suite + "},\"app\":{\"id\":15368}}";

    private static string CheckRuns(int total, params string[] runs) => "{\"total_count\":" + total + ",\"check_runs\":[" + string.Join(",", runs) + "]}";

    private static string Status(long id, string context, string state) =>
        "{\"id\":" + id + ",\"context\":\"" + context + "\",\"state\":\"" + state + "\",\"target_url\":\"https://ci.example.test/" + context + "\",\"created_at\":\"2026-10-03T10:00:00Z\"}";

    private static string Statuses(int total, params string[] statuses) =>
        "{\"state\":\"pending\",\"sha\":\"" + PrSha + "\",\"total_count\":" + total + ",\"statuses\":[" + string.Join(",", statuses) + "]}";

    private static StubHandler NoChecks(StubHandler handler, int pullRequests = 1)
    {
        for (var i = 0; i < pullRequests; i++)
        {
            handler.Json(CheckRuns(0)).Json(Statuses(0));
        }

        return handler;
    }

    private static string Review(long id, string author, string state, string sha, string submitted = "2026-10-02T10:00:00Z") =>
        "{\"databaseId\":" + id + ",\"state\":\"" + state + "\",\"submittedAt\":\"" + submitted + "\",\"url\":\"https://github.com/octo-test/hello/pull/1#pullrequestreview-" + id
        + "\",\"author\":{\"login\":\"" + author + "\"},\"commit\":{\"oid\":\"" + sha + "\"}}";

    private static string PullRequests(int total, params string[] nodes) =>
        "{\"data\":{\"repository\":{\"pullRequests\":{\"totalCount\":" + total + ",\"nodes\":[" + string.Join(",", nodes) + "]}}}}";

    [Fact]
    public async Task Pull_requests_keep_reviews_checks_and_mergeability_separate()
    {
        var (client, handler) = Create();
        handler.Json(PullRequests(57,
                PullRequest(1, "octo-test",
                    reviewRequests: """{"requestedReviewer":{"__typename":"Team","slug":"core","organization":{"login":"acme"}}}""",
                    reviews: string.Join(",", Review(1, "alice", "APPROVED", "0000000000000000000000000000000000000000"), Review(2, "bob", "CHANGES_REQUESTED", PrSha), Review(3, "bob", "COMMENTED", PrSha, "2026-10-02T11:00:00Z")),
                    comments: 5)))
            .Json(CheckRuns(3, CheckRun(10, "build", 900, "completed", "failure"), CheckRun(11, "build", 900, "completed", "success"), CheckRun(12, "build", 901, "in_progress", null)))
            .Json(Statuses(1, Status(5, "ci/legacy", "failure")));

        var result = await client.GetPullRequestsAsync("octo-test", "hello", mineOnly: false, "octo-test", Time().GetUtcNow(), TestContext.Current.CancellationToken);

        var graphql = handler.Requests[0];
        Assert.Equal(HttpMethod.Post, graphql.Method);
        Assert.Equal("https://api.github.com/graphql", graphql.Uri.ToString());
        using (var body = JsonDocument.Parse(graphql.Body!))
        {
            Assert.Equal("octo-test", body.RootElement.GetProperty("variables").GetProperty("owner").GetString());
            // Commit data in GraphQL needs Contents access, which Repo Watch doesn't request.
            Assert.DoesNotContain("statusCheckRollup", body.RootElement.GetProperty("query").GetString(), StringComparison.Ordinal);
        }

        Assert.Equal($"https://api.github.com/repos/octo-test/hello/commits/{PrSha}/check-runs?per_page=100", handler.Requests[1].Uri.ToString());
        Assert.Equal($"https://api.github.com/repos/octo-test/hello/commits/{PrSha}/status?per_page=100", handler.Requests[2].Uri.ToString());

        var state = result.Value!;
        Assert.Equal(ItemCount.Exact(57), state.OpenCount); // totalCount, not the page size
        var entry = Assert.Single(state.Items);
        Assert.Equal("PR 1 <script>", entry.PullRequest.Title); // carried as plain text
        Assert.Equal(MergeState.Clean, entry.PullRequest.MergeState);
        Assert.Equal(5, entry.PullRequest.CommentCount); // conversation and inline review comments, one field
        Assert.Equal("octo-test", entry.PullRequest.HeadOwner);

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

    private static string Search(int issueCount, params string[] nodes) =>
        "{\"issueCount\":" + issueCount + ",\"nodes\":[" + string.Join(",", nodes) + "]}";

    private static string MyPullRequests(string authored, string requested) =>
        "{\"data\":{\"authored\":" + authored + ",\"requested\":" + requested + "}}";

    [Fact]
    public async Task Mine_searches_authored_and_requested_pull_requests_beyond_the_newest_page()
    {
        var (client, handler) = Create();
        const string RequestsMe = """{"requestedReviewer":{"__typename":"User","login":"octo-test"}}""";
        handler.Json(MyPullRequests(
            Search(2, PullRequest(1, "Octo-Test"), PullRequest(7, "octo-test", reviewRequests: RequestsMe)),
            Search(3,
                PullRequest(7, "octo-test", reviewRequests: RequestsMe),
                PullRequest(2, "someone", reviewRequests: RequestsMe),
                PullRequest(3, "someone", reviewRequests: """{"requestedReviewer":{"__typename":"Team","slug":"core","organization":{"login":"acme"}}}"""))));
        NoChecks(handler, 3);

        var result = await client.GetPullRequestsAsync("octo-org", "hello", mineOnly: true, "octo-test", Time().GetUtcNow(), TestContext.Current.CancellationToken);

        using (var body = JsonDocument.Parse(handler.Requests[0].Body!))
        {
            var variables = body.RootElement.GetProperty("variables");
            Assert.Equal("is:pr is:open repo:octo-org/hello author:octo-test", variables.GetProperty("authored").GetString());
            Assert.Equal("is:pr is:open repo:octo-org/hello review-requested:octo-test", variables.GetProperty("requested").GetString());
        }

        // De-duplicated across both searches, newest first; a team-only request is not "for me".
        Assert.Equal([7, 2, 1], result.Value!.Items.Select(e => e.PullRequest.Number));
        Assert.Equal(ItemCount.Exact(3), result.Value.OpenCount); // both searches returned every result
    }

    [Fact]
    public async Task Mine_counts_are_lower_bounds_when_a_search_was_cut_off()
    {
        var (client, handler) = Create();
        NoChecks(handler.Json(MyPullRequests(Search(75, PullRequest(1, "octo-test")), Search(0))));

        var result = await client.GetPullRequestsAsync("octo-org", "hello", mineOnly: true, "octo-test", Time().GetUtcNow(), TestContext.Current.CancellationToken);

        Assert.Equal(ItemCount.AtLeast(1), result.Value!.OpenCount);
        Assert.Equal("1+", result.Value.OpenCount.ToString());
    }

    [Fact]
    public async Task A_field_error_fails_only_that_pull_requests_reviews()
    {
        var (client, handler) = Create();
        handler.Json("{\"data\":{\"repository\":{\"pullRequests\":{\"totalCount\":2,\"nodes\":[" + PullRequest(1, "a") + "," + PullRequest(2, "b") + "]}}},"
            + "\"errors\":[{\"type\":\"FORBIDDEN\",\"message\":\"Resource not accessible by integration\",\"path\":[\"repository\",\"pullRequests\",\"nodes\",0,\"reviews\"]}]}");
        NoChecks(handler, 2);

        var result = await client.GetPullRequestsAsync("octo-test", "hello", mineOnly: false, "octo-test", Time().GetUtcNow(), TestContext.Current.CancellationToken);

        var first = result.Value!.Items.Single(e => e.PullRequest.Number == 1);
        Assert.Null(first.Reviews.Value); // unknown, not "no reviews"
        Assert.Equal(ResourceErrorKind.Forbidden, first.Reviews.LastError!.Kind);
        Assert.NotNull(result.Value.Items.Single(e => e.PullRequest.Number == 2).Reviews.Value);
    }

    [Fact]
    public async Task An_error_not_tied_to_one_pull_request_fails_its_reviews()
    {
        var (client, handler) = Create();
        NoChecks(handler.Json("{\"data\":{\"repository\":{\"pullRequests\":{\"totalCount\":1,\"nodes\":[" + PullRequest(1, "a") + "]}}},\"errors\":[{\"type\":\"SOMETHING_NEW\",\"message\":\"x\"}]}"));

        var result = await client.GetPullRequestsAsync("octo-test", "hello", mineOnly: false, "octo-test", Time().GetUtcNow(), TestContext.Current.CancellationToken);

        var entry = Assert.Single(result.Value!.Items);
        Assert.NotNull(entry.Reviews.LastError);
        Assert.Equal(RollupState.NoChecks, entry.Checks.Value!.Rollup.State); // checks come from REST
    }

    [Fact]
    public async Task Denied_checks_are_unknown_not_no_checks()
    {
        var (client, handler) = Create();
        handler.Json(PullRequests(2, PullRequest(1, "a"), PullRequest(2, "b")))
            .Status(HttpStatusCode.Forbidden)
            .Json(CheckRuns(0)).Json(Statuses(0));

        var result = await client.GetPullRequestsAsync("octo-test", "hello", mineOnly: false, "octo-test", Time().GetUtcNow(), TestContext.Current.CancellationToken);

        var denied = result.Value!.Items[0].Checks;
        Assert.Null(denied.Value);
        Assert.Equal(ResourceErrorKind.Forbidden, denied.LastError!.Kind);
        Assert.Equal(RollupState.NoChecks, result.Value.Items[1].Checks.Value!.Rollup.State);
    }

    [Fact]
    public async Task Checks_stop_loading_after_a_rate_limit_and_are_capped()
    {
        var (client, handler) = Create();
        var reset = Time().GetUtcNow().AddMinutes(5).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        handler.Json(PullRequests(3, PullRequest(1, "a"), PullRequest(2, "b"), PullRequest(3, "c")))
            .JsonWithHeaders("""{"message":"API rate limit exceeded"}""", HttpStatusCode.Forbidden, ("x-ratelimit-remaining", "0"), ("x-ratelimit-reset", reset));

        var result = await client.GetPullRequestsAsync("octo-test", "hello", mineOnly: false, "octo-test", Time().GetUtcNow(), TestContext.Current.CancellationToken);

        Assert.Equal(2, handler.Requests.Count); // no further check requests after the limit
        Assert.All(result.Value!.Items, e => Assert.Equal(ResourceErrorKind.RateLimited, e.Checks.LastError!.Kind));

        var (many, manyHandler) = Create();
        manyHandler.Json(PullRequests(12, Enumerable.Range(1, 12).Select(n => PullRequest(n, "a")).ToArray()));
        NoChecks(manyHandler, RepositoryDataClient.ChecksLoadedFor);
        var capped = await many.GetPullRequestsAsync("octo-test", "hello", mineOnly: false, "octo-test", Time().GetUtcNow(), TestContext.Current.CancellationToken);
        Assert.Equal(1 + (2 * RepositoryDataClient.ChecksLoadedFor), manyHandler.Requests.Count);
        Assert.Equal(Freshness.NotLoaded, capped.Value!.Items[^1].Checks.GetFreshness(Time().GetUtcNow(), TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public async Task GraphQL_rate_limits_use_the_reset_header()
    {
        var (client, handler) = Create();
        var reset = Time().GetUtcNow().AddMinutes(20).ToUnixTimeSeconds();
        handler.JsonWithHeaders("""{"data":null,"errors":[{"type":"RATE_LIMITED","message":"API rate limit exceeded"}]}""", HttpStatusCode.OK,
            ("x-ratelimit-remaining", "0"), ("x-ratelimit-reset", reset.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        var result = await client.GetIssuesAsync("octo-test", "hello", TestContext.Current.CancellationToken);

        Assert.Equal(ResourceErrorKind.RateLimited, result.Error!.Kind);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(reset), result.Error.RetryAt);
    }

    [Fact]
    public async Task A_connection_reset_while_reading_the_body_is_a_network_error()
    {
        var (client, handler) = Create();
        handler.Content(new ThrowingContent());

        var result = await client.GetRepositoryAsync(Key, TestContext.Current.CancellationToken);

        Assert.Equal(ResourceErrorKind.Network, result.Error!.Kind);
    }

    private sealed class ThrowingContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) => throw new IOException("connection reset");

        protected override Task<Stream> CreateContentReadStreamAsync() => throw new HttpIOException(HttpRequestError.ResponseEnded, "connection reset");

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    [Fact]
    public async Task Incomplete_checks_never_roll_up_to_passing_and_missing_checks_are_no_checks()
    {
        var (client, handler) = Create();
        handler.Json(PullRequests(2, PullRequest(1, "a"), PullRequest(2, "b", mergeable: "UNKNOWN", draft: true)))
            .Json(CheckRuns(150, CheckRun(10, "build", 900, "completed", "success"))).Json(Statuses(0))
            .Json(CheckRuns(0)).Json(Statuses(0));

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
               "author":{"login":"alice"},"labels":{"totalCount":1,"nodes":[{"name":"bug"}]},"assignees":{"totalCount":1,"nodes":[{"login":"octo-test"}]},"comments":{"totalCount":4}},
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
        Assert.Equal([4, 0], issues.Items.Select(i => i.CommentCount)); // a missing count is zero, not an error
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
