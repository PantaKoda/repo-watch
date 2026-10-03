using System.Net;
using RepoWatch.Core.State;
using RepoWatch.GitHub.Api;
using RepoWatch.GitHub.Repositories;
using static RepoWatch.GitHub.Tests.Fixture;

namespace RepoWatch.GitHub.Tests;

/// <summary>ETag conditional requests and request-budget tracking.</summary>
public sealed class ConditionalRequestTests
{
    private sealed class FakeTokens : IAccessTokenSource
    {
        public Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>("ghu_current");

        public Task<string?> HandleUnauthorizedAsync(string rejectedAccessToken, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }

    private sealed class MemoryCache : IConditionalCache
    {
        public Dictionary<string, CachedResponse> Items { get; } = [];

        public CachedResponse? Get(Uri uri) => Items.GetValueOrDefault(uri.AbsoluteUri);

        public void Put(Uri uri, CachedResponse response) => Items[uri.AbsoluteUri] = response;
    }

    private const string RepositoryJson =
        """{"id":7,"name":"hello","owner":{"login":"octo","type":"User"},"private":false,"archived":false,"has_issues":true,"default_branch":"main","html_url":"https://github.com/octo/hello"}""";

    private static (RepositoryDataClient Client, StubHandler Handler, MemoryCache Cache, RateBudget Budget, Microsoft.Extensions.Time.Testing.FakeTimeProvider Time) Create()
    {
        var time = Time();
        var handler = new StubHandler(time);
        var cache = new MemoryCache();
        var budget = new RateBudget(time);
        var api = new GitHubApiClient(GitHubHttp.CreateClient(handler), Endpoints, new FakeTokens(), time, cache, budget);
        return (new RepositoryDataClient(api), handler, cache, budget, time);
    }

    private static (string, string)[] Budget(int remaining, int limit, DateTimeOffset reset, string resource = "core") =>
    [
        ("x-ratelimit-remaining", remaining.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        ("x-ratelimit-limit", limit.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        ("x-ratelimit-reset", reset.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)),
        ("x-ratelimit-resource", resource),
    ];

    [Fact]
    public async Task An_unchanged_resource_is_answered_from_the_cache_with_a_304()
    {
        var (client, handler, cache, _, time) = Create();
        var key = new Core.Identity.RepositoryKey(Account, 7);
        handler.JsonWithHeaders(RepositoryJson, HttpStatusCode.OK, ("ETag", "\"abc123\""))
            .JsonWithHeaders("", HttpStatusCode.NotModified);

        var first = await client.GetRepositoryAsync(key, TestContext.Current.CancellationToken);
        var second = await client.GetRepositoryAsync(key, TestContext.Current.CancellationToken);

        Assert.Null(handler.Requests[0].Header("If-None-Match"));
        Assert.Equal("\"abc123\"", handler.Requests[1].Header("If-None-Match"));
        Assert.Equal("octo/hello", first.Value!.Metadata.FullName);
        Assert.Equal("octo/hello", second.Value!.Metadata.FullName); // served from the cached body
        Assert.Single(cache.Items);
    }

    [Fact]
    public async Task A_changed_resource_replaces_the_cache_entry()
    {
        var (client, handler, cache, _, _) = Create();
        var key = new Core.Identity.RepositoryKey(Account, 7);
        handler.JsonWithHeaders(RepositoryJson, HttpStatusCode.OK, ("ETag", "W/\"v1\""))
            .JsonWithHeaders(RepositoryJson.Replace("hello\",\"owner", "renamed\",\"owner", StringComparison.Ordinal), HttpStatusCode.OK, ("ETag", "W/\"v2\""));

        await client.GetRepositoryAsync(key, TestContext.Current.CancellationToken);
        var renamed = await client.GetRepositoryAsync(key, TestContext.Current.CancellationToken);

        Assert.Equal("W/\"v1\"", handler.Requests[1].Header("If-None-Match"));
        Assert.Equal("renamed", renamed.Value!.Metadata.Name);
        Assert.Equal("W/\"v2\"", Assert.Single(cache.Items).Value.ETag);
    }

    [Fact]
    public async Task Failures_and_GraphQL_are_never_cached()
    {
        var (client, handler, cache, _, _) = Create();
        handler.JsonWithHeaders("""{"message":"Server Error"}""", HttpStatusCode.InternalServerError, ("ETag", "\"err\""))
            .Json("""{"data":{"repository":{"hasIssuesEnabled":true,"issues":{"totalCount":0,"nodes":[]}}}}""");

        await client.GetRepositoryAsync(new Core.Identity.RepositoryKey(Account, 7), TestContext.Current.CancellationToken);
        await client.GetIssuesAsync("octo", "hello", TestContext.Current.CancellationToken);

        Assert.Empty(cache.Items);
    }

    [Fact]
    public async Task The_budget_tracks_each_rate_limit_resource()
    {
        var (client, handler, _, budget, time) = Create();
        var reset = time.GetUtcNow().AddMinutes(30);
        handler.JsonWithHeaders(RepositoryJson, HttpStatusCode.OK, Budget(4000, 5000, reset))
            .JsonWithHeaders("""{"data":{"repository":{"hasIssuesEnabled":false}}}""", HttpStatusCode.OK, Budget(300, 5000, reset, "graphql"));

        await client.GetRepositoryAsync(new Core.Identity.RepositoryKey(Account, 7), TestContext.Current.CancellationToken);
        Assert.False(budget.IsLow);

        await client.GetIssuesAsync("octo", "hello", TestContext.Current.CancellationToken);
        Assert.True(budget.IsLow); // graphql: 6% left

        time.Advance(TimeSpan.FromMinutes(31));
        Assert.False(budget.IsLow); // the budget was reset
    }

    [Fact]
    public async Task A_rate_limited_answer_is_not_mistaken_for_not_modified()
    {
        var (client, handler, _, _, time) = Create();
        handler.JsonWithHeaders("""{"message":"API rate limit exceeded"}""", HttpStatusCode.Forbidden, Budget(0, 5000, time.GetUtcNow().AddMinutes(5)));

        var result = await client.GetRepositoryAsync(new Core.Identity.RepositoryKey(Account, 7), TestContext.Current.CancellationToken);

        Assert.Equal(ResourceErrorKind.RateLimited, result.Error!.Kind);
    }
}
