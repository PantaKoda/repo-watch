using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using RepoWatch.Core.Accounts;
using RepoWatch.GitHub.Auth;
using RepoWatch.GitHub.Users;
using static RepoWatch.GitHub.Tests.Fixture;

namespace RepoWatch.GitHub.Tests;

public sealed class AccountSessionTests
{
    private static (AccountSession Session, StubHandler Handler, MemoryCredentialStore Store, FakeTimeProvider Time) Create(TimeSpan accessValidFor)
    {
        var time = Time();
        var handler = new StubHandler(time);
        var store = new MemoryCredentialStore();
        var credential = new StoredCredential("ghu_old", time.GetUtcNow() + accessValidFor, "ghr_old", time.GetUtcNow().AddDays(100));
        store.Items[Account] = credential;
        var client = new DeviceFlowClient(GitHubHttp.CreateClient(handler), Endpoints, ClientId, time);
        return (new AccountSession(Account, credential, store, client, time, NullLogger.Instance), handler, store, time);
    }

    [Fact]
    public async Task A_valid_token_is_used_without_refreshing()
    {
        var (session, handler, _, _) = Create(TimeSpan.FromHours(2));

        Assert.Equal("ghu_old", await session.GetAccessTokenAsync(TestContext.Current.CancellationToken));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_token_near_expiry_is_renewed_and_the_rotated_pair_is_persisted()
    {
        var (session, handler, store, _) = Create(TimeSpan.FromMinutes(3));
        handler.Json(TokenJson("ghu_rotated", "ghr_rotated"));

        var token = await session.GetAccessTokenAsync(TestContext.Current.CancellationToken);

        Assert.Equal("ghu_rotated", token);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("refresh_token", request.Form["grant_type"]);
        Assert.Equal("ghr_old", request.Form["refresh_token"]);
        Assert.Equal(ClientId, request.Form["client_id"]);
        Assert.False(request.Form.ContainsKey("client_secret"));
        Assert.Equal("ghr_rotated", store.Items[Account].RefreshToken);
        Assert.Equal("ghu_rotated", await session.GetAccessTokenAsync(TestContext.Current.CancellationToken));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Concurrent_callers_share_a_single_refresh()
    {
        var (session, handler, store, _) = Create(TimeSpan.Zero);
        handler.Json(TokenJson("ghu_rotated", "ghr_rotated"));

        var tokens = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => session.GetAccessTokenAsync(TestContext.Current.CancellationToken))));

        Assert.All(tokens, t => Assert.Equal("ghu_rotated", t));
        Assert.Single(handler.Requests);
        Assert.Equal(["write"], store.Operations);
    }

    [Fact]
    public async Task A_rejected_refresh_requires_reconnect_and_stops_retrying()
    {
        var (session, handler, store, _) = Create(TimeSpan.Zero);
        handler.Json("""{"error":"bad_refresh_token","error_description":"The refresh token passed is incorrect or expired."}""");
        var changes = 0;
        session.StatusChanged += (_, _) => changes++;

        Assert.Null(await session.GetAccessTokenAsync(TestContext.Current.CancellationToken));
        Assert.Null(await session.GetAccessTokenAsync(TestContext.Current.CancellationToken));
        Assert.Null(await session.GetAccessTokenAsync(TestContext.Current.CancellationToken));

        Assert.Equal(SessionStatus.ReconnectRequired, session.Status);
        Assert.Single(handler.Requests);
        Assert.Empty(store.Items);
        Assert.True(session.Lifetime.IsCancellationRequested);
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task A_reused_refresh_token_as_github_reports_it_requires_reconnect()
    {
        // Observed live (October 2026): reusing a rotated refresh token returns incorrect_client_credentials.
        var (session, handler, store, _) = Create(TimeSpan.Zero);
        handler.Json("""{"error":"incorrect_client_credentials","error_description":"The client_id and/or client_secret passed are incorrect."}""");

        Assert.Null(await session.GetAccessTokenAsync(TestContext.Current.CancellationToken));

        Assert.Equal(SessionStatus.ReconnectRequired, session.Status);
        Assert.Empty(store.Items);
    }

    [Fact]
    public async Task Transient_refresh_failure_keeps_a_still_valid_token()
    {
        var (session, handler, store, _) = Create(TimeSpan.FromMinutes(2));
        handler.Status(HttpStatusCode.ServiceUnavailable);

        Assert.Equal("ghu_old", await session.GetAccessTokenAsync(TestContext.Current.CancellationToken));
        Assert.Equal(SessionStatus.Active, session.Status);
        Assert.Equal("ghr_old", store.Items[Account].RefreshToken);
    }

    [Fact]
    public async Task Transient_refresh_failure_with_an_expired_token_is_reported_but_keeps_the_session()
    {
        var (session, handler, _, _) = Create(TimeSpan.FromMinutes(-1));
        handler.Throws(new HttpRequestException("offline"));

        await Assert.ThrowsAsync<TokenUnavailableException>(() => session.GetAccessTokenAsync(TestContext.Current.CancellationToken));
        Assert.Equal(SessionStatus.Active, session.Status);
    }

    [Fact]
    public async Task A_revoked_token_reported_by_the_api_requires_reconnect_when_refresh_also_fails()
    {
        var (session, handler, store, _) = Create(TimeSpan.FromHours(2));
        handler.Json("""{"error":"bad_refresh_token"}""");

        Assert.Null(await session.HandleUnauthorizedAsync("ghu_old", TestContext.Current.CancellationToken));

        Assert.Equal(SessionStatus.ReconnectRequired, session.Status);
        Assert.Empty(store.Items);
    }

    [Fact]
    public async Task A_closed_session_hands_out_no_tokens()
    {
        var (session, handler, _, _) = Create(TimeSpan.FromHours(2));

        await session.CloseAsync();

        Assert.Null(await session.GetAccessTokenAsync(TestContext.Current.CancellationToken));
        Assert.True(session.Lifetime.IsCancellationRequested);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Identity_lookup_sends_the_token_only_in_the_authorization_header()
    {
        var time = Time();
        var handler = new StubHandler(time)
            .Json("""{"login":"octo-test","id":4242,"name":"Octo Test","avatar_url":"https://avatars.githubusercontent.com/u/4242?v=4"}""")
            .Status(HttpStatusCode.Unauthorized)
            .Status(HttpStatusCode.BadGateway);
        var users = new GitHubUserClient(GitHubHttp.CreateClient(handler), Endpoints);

        var ok = await users.GetAuthenticatedUserAsync("ghu_secret", TestContext.Current.CancellationToken);
        var revoked = await users.GetAuthenticatedUserAsync("ghu_secret", TestContext.Current.CancellationToken);
        var outage = await users.GetAuthenticatedUserAsync("ghu_secret", TestContext.Current.CancellationToken);

        Assert.Equal(UserLookupStatus.Success, ok.Status);
        Assert.Equal(Account, ok.Identity!.Account);
        Assert.Equal("octo-test", ok.Identity.Login);
        Assert.Equal(UserLookupStatus.Unauthorized, revoked.Status);
        Assert.Equal(UserLookupStatus.Unavailable, outage.Status);

        var request = handler.Requests[0];
        Assert.Equal("https://api.github.com/user", request.Uri.ToString());
        Assert.Equal("Bearer ghu_secret", request.Message.Headers.Authorization?.ToString());
        Assert.Equal(GitHubHttp.ApiVersion, request.Header("X-GitHub-Api-Version"));
        Assert.DoesNotContain("ghu_secret", request.Uri.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Credentials_are_redacted_in_text()
    {
        var credential = new StoredCredential("ghu_secret", null, "ghr_secret", null);

        Assert.DoesNotContain("secret", credential.ToString(), StringComparison.Ordinal);
    }
}
