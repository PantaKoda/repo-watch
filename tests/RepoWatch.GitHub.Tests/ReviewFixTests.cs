using System.Net;
using RepoWatch.Core.Access;
using RepoWatch.Core.State;
using RepoWatch.GitHub.Access;
using RepoWatch.GitHub.Api;
using RepoWatch.GitHub.Auth;
using static RepoWatch.GitHub.Tests.Fixture;

namespace RepoWatch.GitHub.Tests;

/// <summary>Regression tests for the PR #4 review findings.</summary>
public sealed class ReviewFixTests
{
    private sealed class Tokens(Func<string?> current) : IAccessTokenSource
    {
        public Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult(current());

        public Task<string?> HandleUnauthorizedAsync(string rejectedAccessToken, CancellationToken cancellationToken = default) =>
            throw new TokenUnavailableException("Couldn't reach GitHub to renew the session.");
    }

    private static (AccessCatalogClient Client, StubHandler Handler) Create(Func<string?>? token = null)
    {
        var time = Time();
        var handler = new StubHandler(time);
        var api = new GitHubApiClient(GitHubHttp.CreateClient(handler), Endpoints, new Tokens(token ?? (() => "ghu_current")), time);
        return (new AccessCatalogClient(api, time), handler);
    }

    private static string Installation(long id, string login, string type = "User") =>
        $$$"""{"id":{{{id}}},"account":{"login":"{{{login}}}","id":{{{id * 10}}},"type":"{{{type}}}"},"target_type":"{{{type}}}","target_id":{{{id * 10}}},"repository_selection":"selected","suspended_at":null,"html_url":"https://github.com/settings/installations/{{{id}}}","permissions":{"metadata":"read","actions":"read","checks":"read","statuses":"read","issues":"read","pull_requests":"read"}}""";

    private static string Repository(long id, string owner, string name) =>
        $$"""{"id":{{id}},"name":"{{name}}","owner":{"login":"{{owner}}","id":1,"type":"User"},"private":false,"archived":false,"default_branch":"main","html_url":"https://github.com/{{owner}}/{{name}}"}""";

    [Fact]
    public async Task Sso_partial_results_on_a_successful_installation_list_make_the_catalog_incomplete()
    {
        var (client, handler) = Create();
        handler.JsonWithHeaders($$"""{"total_count":1,"installations":[{{Installation(1, "octo-test")}}]}""", HttpStatusCode.OK,
                ("X-GitHub-SSO", "partial-results; organizations=21955855,20582480"))
            .Json($$"""{"total_count":1,"repositories":[{{Repository(11, "octo-test", "dotfiles")}}]}""");

        var catalog = (await client.LoadAsync(TestContext.Current.CancellationToken)).Value!;

        Assert.True(catalog.SsoHidesInstallations);
        Assert.Equal([20582480L, 21955855L], catalog.SsoHiddenOrganizationIds);
        Assert.False(catalog.IsComplete);
        // A watched repository of the hidden organization is not claimed to be ungranted.
        Assert.Equal(WatchedAccess.Unknown, AccessClassifier.Classify(999, "sso-org", catalog).Access);
    }

    [Fact]
    public async Task Sso_partial_results_on_a_repository_list_mark_that_installation()
    {
        var (client, handler) = Create();
        handler.Json($$"""{"total_count":1,"installations":[{{Installation(2, "acme-org", "Organization")}}]}""")
            .JsonWithHeaders($$"""{"total_count":1,"repositories":[{{Repository(21, "acme-org", "api")}}]}""", HttpStatusCode.OK,
                ("X-GitHub-SSO", "partial-results"));

        var catalog = (await client.LoadAsync(TestContext.Current.CancellationToken)).Value!;

        var installation = Assert.Single(catalog.Installations);
        Assert.True(installation.SsoPartial);
        Assert.False(installation.RepositoriesComplete);
        Assert.False(catalog.IsComplete);
        Assert.Equal(WatchedAccess.Unknown, AccessClassifier.Classify(22, "acme-org", catalog).Access);
    }

    [Fact]
    public void Sso_header_without_partial_results_is_not_partial()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.TryAddWithoutValidation("X-GitHub-SSO", "required; url=https://github.com/orgs/x/sso");

        Assert.Null(GitHubApiClient.PartialSsoResults(response.Headers));
    }

    [Fact]
    public async Task A_renewal_that_cannot_reach_github_is_a_network_result_not_an_exception()
    {
        var (client, handler) = Create(() => throw new TokenUnavailableException("Couldn't reach GitHub to renew the session."));

        var result = await client.ListWorkflowsAsync("o", "r", TestContext.Current.CancellationToken);

        Assert.Equal(ResourceErrorKind.Network, result.Error!.Kind);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_renewal_failure_after_a_401_is_a_network_result_not_an_exception()
    {
        var (client, handler) = Create();
        handler.Status(HttpStatusCode.Unauthorized);

        var result = await client.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ResourceErrorKind.Network, result.Error!.Kind);
    }

    [Fact]
    public async Task A_rate_limit_stops_querying_further_installations()
    {
        var (client, handler) = Create();
        handler.Json($$"""{"total_count":3,"installations":[{{Installation(1, "a")}},{{Installation(2, "b")}},{{Installation(3, "c")}}]}""")
            .JsonWithHeaders("""{"message":"API rate limit exceeded"}""", HttpStatusCode.Forbidden,
                ("x-ratelimit-remaining", "0"), ("x-ratelimit-reset", "1790000000"));

        var catalog = (await client.LoadAsync(TestContext.Current.CancellationToken)).Value!;

        Assert.Equal(2, handler.Requests.Count); // installations + the first (rate-limited) repository list only
        Assert.All(catalog.Installations, i =>
        {
            Assert.Equal(InstallationHealth.Unavailable, i.Health);
            Assert.Equal(ResourceErrorKind.RateLimited, i.Error!.Kind);
            Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1790000000), i.Error.RetryAt);
        });
    }
}
