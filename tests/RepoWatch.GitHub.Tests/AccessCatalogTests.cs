using System.Net;
using RepoWatch.Core.Access;
using RepoWatch.Core.Identity;
using RepoWatch.Core.State;
using RepoWatch.GitHub.Access;
using RepoWatch.GitHub.Api;
using static RepoWatch.GitHub.Tests.Fixture;

namespace RepoWatch.GitHub.Tests;

/// <summary>Contract tests for installation and repository discovery. Fixture bodies follow GitHub's documented shapes.</summary>
public sealed class AccessCatalogTests
{
    private sealed class FakeTokens(params string?[] renewals) : IAccessTokenSource
    {
        private readonly Queue<string?> _renewals = new(renewals);

        public string? Current { get; private set; } = "ghu_current";

        public int UnauthorizedCalls { get; private set; }

        public Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult(Current);

        public Task<string?> HandleUnauthorizedAsync(string rejectedAccessToken, CancellationToken cancellationToken = default)
        {
            UnauthorizedCalls++;
            Current = _renewals.Count > 0 ? _renewals.Dequeue() : null;
            return Task.FromResult(Current);
        }
    }

    private static (AccessCatalogClient Client, StubHandler Handler, GitHubApiClient Api, FakeTokens Tokens) Create(params string?[] renewals)
    {
        var time = Time();
        var handler = new StubHandler(time);
        var tokens = new FakeTokens(renewals);
        var api = new GitHubApiClient(GitHubHttp.CreateClient(handler), Endpoints, tokens, time);
        return (new AccessCatalogClient(api, time), handler, api, tokens);
    }

    private static string Installation(long id, string login, string type = "User", string selection = "selected", string? suspendedAt = null, string permissions = """{"metadata":"read","actions":"read","checks":"read","statuses":"read","issues":"read","pull_requests":"read"}""") =>
        $$"""{"id":{{id}},"account":{"login":"{{login}}","id":{{id * 10}},"type":"{{type}}"},"target_type":"{{type}}","target_id":{{id * 10}},"repository_selection":"{{selection}}","suspended_at":{{(suspendedAt is null ? "null" : $"\"{suspendedAt}\"")}},"html_url":"https://github.com/settings/installations/{{id}}","permissions":{{permissions}},"app_slug":"repo-watch-pantakoda"}""";

    private static string Repository(long id, string owner, string name, bool isPrivate = false, string ownerType = "User", bool archived = false) =>
        $$"""{"id":{{id}},"name":"{{name}}","full_name":"{{owner}}/{{name}}","owner":{"login":"{{owner}}","id":1,"type":"{{ownerType}}"},"private":{{(isPrivate ? "true" : "false")}},"archived":{{(archived ? "true" : "false")}},"default_branch":"main","html_url":"https://github.com/{{owner}}/{{name}}","description":"Synthetic fixture"}""";

    private static string Installations(params string[] items) => $$"""{"total_count":{{items.Length}},"installations":[{{string.Join(",", items)}}]}""";

    private static string Repositories(params string[] items) => $$"""{"total_count":{{items.Length}},"repository_selection":"selected","repositories":[{{string.Join(",", items)}}]}""";

    private static (string, string) NextLink(string url) => ("Link", $"<{url}>; rel=\"next\", <{url}>; rel=\"last\"");

    [Fact]
    public async Task Combines_personal_and_organization_installations_following_every_page()
    {
        var (client, handler, _, _) = Create();
        handler.Json(Installations(Installation(1, "octo-test"), Installation(2, "acme-org", "Organization", "all")))
            // installation 1: two pages
            .JsonWithHeaders(Repositories(Repository(11, "octo-test", "dotfiles", isPrivate: true)), HttpStatusCode.OK,
                NextLink("https://api.github.com/user/installations/1/repositories?per_page=100&page=2"))
            .Json(Repositories(Repository(12, "octo-test", "blog")))
            // installation 2: one page, including a repository also listed by installation 1
            .Json(Repositories(Repository(21, "acme-org", "api", isPrivate: true, ownerType: "Organization", archived: true), Repository(11, "octo-test", "dotfiles", isPrivate: true)));

        var result = await client.LoadAsync(TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        var catalog = result.Value!;
        Assert.True(catalog.IsComplete);
        Assert.Equal(["acme-org/api", "octo-test/blog", "octo-test/dotfiles"], catalog.Repositories.Select(r => r.FullName));
        var api = catalog.Repositories[0];
        Assert.True(api.IsPrivate);
        Assert.True(api.IsArchived);
        Assert.Equal(RepositoryOwnerKind.Organization, api.OwnerKind);
        Assert.Equal(RepositoryOwnerKind.Organization, catalog.Installations[1].Installation.AccountKind);
        Assert.Equal(RepositorySelection.All, catalog.Installations[1].Installation.Selection);
        Assert.Empty(catalog.Installations[0].Installation.MissingPermissions);

        var paths = handler.Requests.Select(r => r.Uri.PathAndQuery).ToList();
        Assert.Equal("/user/installations?per_page=100", paths[0]);
        Assert.Equal("/user/installations/1/repositories?per_page=100", paths[1]);
        Assert.Equal("/user/installations/1/repositories?per_page=100&page=2", paths[2]);
        Assert.All(handler.Requests, r => Assert.Equal("Bearer ghu_current", r.Message.Headers.Authorization?.ToString()));
    }

    [Fact]
    public async Task Pagination_never_follows_a_link_to_another_host()
    {
        var (_, handler, api, _) = Create();
        handler.JsonWithHeaders(Repositories(Repository(1, "o", "a")), HttpStatusCode.OK, NextLink("https://evil.example/user/installations/1/repositories?page=2"));

        var result = await api.GetAllPagesAsync(api.ApiUri("user/installations/1/repositories"), AccessJsonContext.Default.RepositoriesPage, p => p.Repositories, TestContext.Current.CancellationToken);

        Assert.Single(result.Value!.Items);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Reaching_the_page_limit_is_reported_as_incomplete()
    {
        var (_, handler, api, _) = Create();
        handler.JsonWithHeaders(Repositories(Repository(1, "o", "a")), HttpStatusCode.OK, NextLink("https://api.github.com/x?page=2"))
            .JsonWithHeaders(Repositories(Repository(2, "o", "b")), HttpStatusCode.OK, NextLink("https://api.github.com/x?page=3"));

        var result = await api.GetAllPagesAsync(api.ApiUri("x"), AccessJsonContext.Default.RepositoriesPage, p => p.Repositories, TestContext.Current.CancellationToken, maxPages: 2);

        Assert.False(result.Value!.IsComplete);
        Assert.Equal(2, result.Value.Items.Count);
    }

    [Fact]
    public async Task Suspended_installations_and_sso_requirements_are_reported_not_hidden()
    {
        var (client, handler, _, _) = Create();
        handler.Json(Installations(
                Installation(1, "octo-test"),
                Installation(2, "suspended-org", "Organization", suspendedAt: "2026-09-01T00:00:00Z"),
                Installation(3, "sso-org", "Organization")))
            .Json(Repositories(Repository(11, "octo-test", "dotfiles")))
            .JsonWithHeaders("""{"message":"Resource protected by organization SAML enforcement."}""", HttpStatusCode.Forbidden,
                ("X-GitHub-SSO", "required; url=https://github.com/orgs/sso-org/sso?authorization_request=abc"));

        var catalog = (await client.LoadAsync(TestContext.Current.CancellationToken)).Value!;

        Assert.False(catalog.IsComplete);
        Assert.Equal(InstallationHealth.Suspended, catalog.Installations[1].Health);
        var sso = catalog.Installations[2];
        Assert.Equal(InstallationHealth.SsoRequired, sso.Health);
        Assert.Equal(new Uri("https://github.com/orgs/sso-org/sso?authorization_request=abc"), sso.Error!.ActionUrl);
        Assert.DoesNotContain(handler.Requests, r => r.Uri.AbsolutePath.Contains("/installations/2/", StringComparison.Ordinal)); // suspended: not requested
    }

    [Fact]
    public async Task Missing_permissions_after_a_permission_change_are_listed()
    {
        var (client, handler, _, _) = Create();
        handler.Json(Installations(Installation(1, "octo-test", permissions: """{"metadata":"read","issues":"read"}""")))
            .Json(Repositories());

        var catalog = (await client.LoadAsync(TestContext.Current.CancellationToken)).Value!;

        Assert.Equal(["actions", "checks", "statuses", "pull_requests"], catalog.Installations[0].Installation.MissingPermissions);
    }

    [Fact]
    public async Task Rate_limits_carry_the_reset_time()
    {
        var (client, handler, _, _) = Create();
        handler.JsonWithHeaders("""{"message":"API rate limit exceeded"}""", HttpStatusCode.Forbidden,
            ("x-ratelimit-remaining", "0"), ("x-ratelimit-reset", "1790000000"));

        var result = await client.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ResourceErrorKind.RateLimited, result.Error!.Kind);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1790000000), result.Error.RetryAt);
    }

    [Fact]
    public async Task A_401_is_retried_once_with_a_renewed_token()
    {
        var (client, handler, _, tokens) = Create("ghu_renewed");
        handler.Status(HttpStatusCode.Unauthorized)
            .Json(Installations())
            ;

        var result = await client.LoadAsync(TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, tokens.UnauthorizedCalls);
        Assert.Equal("Bearer ghu_renewed", handler.Requests[1].Message.Headers.Authorization?.ToString());
    }

    [Fact]
    public async Task A_401_that_renewal_cannot_fix_is_unauthorized()
    {
        var (client, handler, _, _) = Create((string?)null);
        handler.Status(HttpStatusCode.Unauthorized);

        var result = await client.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ResourceErrorKind.Unauthorized, result.Error!.Kind);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Workflows_are_listed_across_pages()
    {
        var (client, handler, _, _) = Create();
        handler.JsonWithHeaders("""{"total_count":2,"workflows":[{"id":7,"name":"CI","path":".github/workflows/ci.yml","state":"active","html_url":"https://github.com/o/r/blob/main/.github/workflows/ci.yml"}]}""",
                HttpStatusCode.OK, NextLink("https://api.github.com/repos/o/r/actions/workflows?per_page=100&page=2"))
            .Json("""{"total_count":2,"workflows":[{"id":9,"name":"Release","path":".github/workflows/release.yml","state":"active","html_url":"https://github.com/o/r/blob/main/.github/workflows/release.yml"}]}""");

        var result = await client.ListWorkflowsAsync("o", "r", TestContext.Current.CancellationToken);

        Assert.Equal([7L, 9L], result.Value!.Items.Select(w => w.Id));
        Assert.Equal("/repos/o/r/actions/workflows?per_page=100", handler.Requests[0].Uri.PathAndQuery);
    }
}
