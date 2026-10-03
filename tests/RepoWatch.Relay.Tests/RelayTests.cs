using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RepoWatch.Core.Relay;

namespace RepoWatch.Relay.Tests;

public sealed class RelayTests : IDisposable
{
    private readonly RelayFixture _relay = new();

    public void Dispose() => _relay.Dispose();

    private static string WorkflowRun(long repositoryId) => $$"""{"action":"completed","workflow_run":{"id":1},"repository":{{RelayFixture.Repository(repositoryId)}}}""";

    private static RelayInvalidation Data(SseEvent e) => JsonSerializer.Deserialize(e.Data, RelayJsonContext.Default.RelayInvalidation)!;

    [Fact]
    public async Task Webhooks_need_a_valid_signature_over_the_exact_bytes()
    {
        var json = WorkflowRun(100);

        var unsigned = await _relay.DeliverAsync("workflow_run", json, signature: "");
        var forged = await _relay.DeliverAsync("workflow_run", json, signature: WebhookSignature.Sign("another-secret-0123456789", Encoding.UTF8.GetBytes(json)));
        var reformatted = await _relay.DeliverAsync("workflow_run", json.Replace(",", ", ", StringComparison.Ordinal),
            signature: WebhookSignature.Sign(RelayFixture.Secret, Encoding.UTF8.GetBytes(json))); // same JSON, different bytes
        var valid = await _relay.DeliverAsync("workflow_run", json);

        Assert.Equal(HttpStatusCode.Unauthorized, unsigned.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, reformatted.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, valid.StatusCode);
    }

    [Fact]
    public async Task Deliveries_are_stored_before_the_answer_and_duplicates_are_processed_once()
    {
        var session = await _relay.CreateSessionAsync("ghu_alice", 100);
        using var events = await _relay.OpenEventsAsync(session.SessionToken);
        Assert.Equal(HttpStatusCode.OK, events.Status);

        var first = await _relay.DeliverAsync("workflow_run", WorkflowRun(100), deliveryId: "delivery-1");
        Assert.NotNull(_relay.Services.GetRequiredService<DeliveryStore>().Find("delivery-1")); // durable when GitHub got 202
        var redelivered = await _relay.DeliverAsync("workflow_run", WorkflowRun(100), deliveryId: "delivery-1");

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, redelivered.StatusCode);
        var invalidate = await events.NextAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RelayProtocol.Invalidate, invalidate!.Name);
        Assert.Equal(new RelayInvalidation(100, [RelayProtocol.Parts.Actions]).RepositoryId, Data(invalidate).RepositoryId);
        Assert.Equal([RelayProtocol.Parts.Actions], Data(invalidate).Parts);
        Assert.Null(await events.NextAsync(TimeSpan.FromSeconds(1.5))); // only keep-alives: the duplicate made no second event
    }

    [Fact]
    public async Task Failed_processing_is_retried()
    {
        var processor = _relay.Services.GetRequiredService<DeliveryProcessor>();
        var failures = 0;
        processor.BeforeApply = _ =>
        {
            if (Interlocked.Increment(ref failures) == 1)
            {
                throw new InvalidOperationException("simulated outage");
            }
        };

        await _relay.DeliverAsync("issues", $$"""{"action":"opened","repository":{{RelayFixture.Repository(100)}}}""", deliveryId: "flaky");
        for (var i = 0; i < 100 && _relay.Services.GetRequiredService<DeliveryStore>().Find("flaky") is not { Status: "done" }; i++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Assert.Equal(("done", 2), _relay.Services.GetRequiredService<DeliveryStore>().Find("flaky"));
    }

    [Fact]
    public async Task Sessions_only_include_repositories_GitHub_confirms_for_that_user()
    {
        var alice = await _relay.CreateSessionAsync("ghu_alice", 100, 200, 999);

        Assert.Equal([100L], alice.Allowed);
        Assert.Equal([200L, 999L], alice.Rejected); // bob's repository and an unknown one

        // Alice never receives bob's events, even though she asked for them.
        using var events = await _relay.OpenEventsAsync(alice.SessionToken);
        await _relay.DeliverAsync("pull_request", $$"""{"action":"opened","repository":{{RelayFixture.Repository(200)}}}""");
        await _relay.DeliverAsync("pull_request", $$"""{"action":"opened","repository":{{RelayFixture.Repository(100)}}}""");
        var received = await events.NextAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(100, Data(received!).RepositoryId);
        Assert.Equal([RelayProtocol.Parts.PullRequests], Data(received!).Parts);
    }

    [Fact]
    public async Task Tokens_are_accepted_only_in_the_authorization_header()
    {
        var client = _relay.CreateClient();
        var noToken = await client.PostAsync("/sessions", new StringContent("""{"repositoryIds":[100]}""", Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
        var inQuery = await client.PostAsync("/sessions?access_token=ghu_alice", new StringContent("""{"repositoryIds":[100]}""", Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
        var unknownUser = await PostSession(client, "ghu_mallory");
        var session = await _relay.CreateSessionAsync("ghu_alice", 100);
        var eventsInQuery = await client.GetAsync($"/events?access_token={session.SessionToken}", HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);
        using var forged = await _relay.OpenEventsAsync("not-a-session");

        Assert.Equal(HttpStatusCode.Unauthorized, noToken.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, inQuery.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownUser.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, eventsInQuery.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, forged.Status);
        Assert.DoesNotContain(_relay.GitHub.Authorizations, uri => uri.Contains("ghu_", StringComparison.Ordinal)); // never in a URL to GitHub either
    }

    private static Task<HttpResponseMessage> PostSession(HttpClient client, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/sessions") { Content = new StringContent("""{"repositoryIds":[100]}""", Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.SendAsync(request);
    }

    [Fact]
    public async Task Reconnecting_replays_missed_events_in_order()
    {
        var session = await _relay.CreateSessionAsync("ghu_alice", 100, 101);
        long lastSeen;
        using (var events = await _relay.OpenEventsAsync(session.SessionToken))
        {
            await _relay.DeliverAsync("issues", $$"""{"action":"opened","repository":{{RelayFixture.Repository(100)}}}""");
            lastSeen = (await events.NextAsync(TimeSpan.FromSeconds(5)))!.Id!.Value;
        }

        // While disconnected, three more changes arrive.
        await _relay.DeliverAsync("issues", $$"""{"action":"closed","repository":{{RelayFixture.Repository(101)}}}""");
        await _relay.DeliverAsync("workflow_job", $$"""{"action":"queued","repository":{{RelayFixture.Repository(100)}}}""");
        await _relay.DeliverAsync("repository", $$"""{"action":"renamed","repository":{{RelayFixture.Repository(101)}}}""");
        await WaitProcessedAsync(3);

        using var resumed = await _relay.OpenEventsAsync(session.SessionToken, lastSeen);
        var replayed = new List<SseEvent>();
        for (var i = 0; i < 3; i++)
        {
            replayed.Add((await resumed.NextAsync(TimeSpan.FromSeconds(5)))!);
        }

        Assert.Equal([101L, 100L, 101L], replayed.Select(e => Data(e).RepositoryId));
        Assert.Equal(replayed.Select(e => e.Id).Order(), replayed.Select(e => e.Id)); // in sequence order
        Assert.All(replayed, e => Assert.True(e.Id > lastSeen));
        Assert.Equal([RelayProtocol.Parts.Metadata], Data(replayed[2]).Parts);
    }

    [Fact]
    public async Task A_gap_beyond_the_replay_buffer_or_an_unknown_id_asks_for_a_full_refresh()
    {
        var session = await _relay.CreateSessionAsync("ghu_alice", 100);
        var first = _relay.Services.GetRequiredService<RelayHub>().LastSeq;
        for (var i = 0; i < 25; i++) // more than the replay capacity (20)
        {
            await _relay.DeliverAsync("issues", $$"""{"action":"edited","repository":{{RelayFixture.Repository(100)}}}""");
        }

        await WaitProcessedAsync(25);

        using (var gap = await _relay.OpenEventsAsync(session.SessionToken, first))
        {
            Assert.Equal(RelayProtocol.Reset, (await gap.NextAsync(TimeSpan.FromSeconds(5)))!.Name);
        }

        using var fromAnotherRun = await _relay.OpenEventsAsync(session.SessionToken, long.MaxValue / 2);
        Assert.Equal(RelayProtocol.Reset, (await fromAnotherRun.NextAsync(TimeSpan.FromSeconds(5)))!.Name);
    }

    [Fact]
    public async Task Removed_repositories_and_revoked_authorizations_end_access()
    {
        var alice = await _relay.CreateSessionAsync("ghu_alice", 100, 101);
        using var events = await _relay.OpenEventsAsync(alice.SessionToken);

        // Repository 101 is removed from the installation: alice is told, and no longer receives its events.
        await _relay.DeliverAsync("installation_repositories",
            """{"action":"removed","installation":{"id":10},"repositories_removed":[{"id":101}],"sender":{"id":1}}""");
        var revokedRepository = await events.NextAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RelayProtocol.Revoked, revokedRepository!.Name);
        Assert.Equal(101, Data(revokedRepository).RepositoryId);
        await _relay.DeliverAsync("issues", $$"""{"action":"opened","repository":{{RelayFixture.Repository(101)}}}""");
        await _relay.DeliverAsync("issues", $$"""{"action":"opened","repository":{{RelayFixture.Repository(100)}}}""");
        Assert.Equal(100, Data((await events.NextAsync(TimeSpan.FromSeconds(5)))!).RepositoryId);

        // Alice revokes the app's authorization: her session ends and can't be used again.
        await _relay.DeliverAsync("github_app_authorization", """{"action":"revoked","sender":{"id":1}}""");
        Assert.Equal(RelayProtocol.Revoked, (await events.NextAsync(TimeSpan.FromSeconds(5)))!.Name);
        using var again = await _relay.OpenEventsAsync(alice.SessionToken);
        Assert.Equal(HttpStatusCode.Unauthorized, again.Status);
    }

    [Fact]
    public async Task Uninstalling_the_app_ends_sessions_for_its_repositories()
    {
        var bob = await _relay.CreateSessionAsync("ghu_bob", 200);
        using var events = await _relay.OpenEventsAsync(bob.SessionToken);

        await _relay.DeliverAsync("installation", """{"action":"deleted","installation":{"id":20},"sender":{"id":2}}""");

        Assert.Equal(RelayProtocol.Revoked, (await events.NextAsync(TimeSpan.FromSeconds(5)))!.Name); // repository 200
        Assert.Equal(RelayProtocol.Revoked, (await events.NextAsync(TimeSpan.FromSeconds(5)))!.Name); // and the session
        Assert.Equal(0, _relay.Services.GetRequiredService<RelayHub>().SessionCount);
    }

    [Fact]
    public async Task Sessions_expire_and_tell_the_client()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);
        using var relay = new RelayFixture(clock);
        var session = await relay.CreateSessionAsync("ghu_alice", 100);
        using var events = await relay.OpenEventsAsync(session.SessionToken);
        Assert.Equal(DateTimeOffset.UtcNow.AddMinutes(15), session.ExpiresAt, TimeSpan.FromMinutes(1));

        clock.Advance(TimeSpan.FromMinutes(16));

        Assert.Equal(RelayProtocol.Expired, (await events.NextAsync(TimeSpan.FromSeconds(5)))!.Name);
        using var reconnect = await relay.OpenEventsAsync(session.SessionToken);
        Assert.Equal(HttpStatusCode.Unauthorized, reconnect.Status);
    }

    [Fact]
    public void Event_mapping_reads_only_ids_and_actions()
    {
        var mapped = EventMapper.Map("check_run", Encoding.UTF8.GetBytes(
            """{"action":"completed","check_run":{"name":"run `rm -rf /`","output":{"summary":"<script>"}},"repository":{"id":42}}"""));

        var invalidation = Assert.Single(mapped.Invalidations);
        Assert.Equal(42, invalidation.RepositoryId);
        Assert.Equal([RelayProtocol.Parts.Actions, RelayProtocol.Parts.PullRequests], invalidation.Parts);
        Assert.Same(MappedDelivery.None, EventMapper.Map("issue_comment", Encoding.UTF8.GetBytes("""{"repository":{"id":42}}""")));
    }

    private async Task WaitProcessedAsync(int count)
    {
        var hub = _relay.Services.GetRequiredService<RelayHub>();
        _ = count;
        // Deliveries are processed in the background; wait until the processor is idle.
        for (var i = 0; i < 100; i++)
        {
            var before = hub.LastSeq;
            await Task.Delay(100, TestContext.Current.CancellationToken);
            if (hub.LastSeq == before)
            {
                return;
            }
        }
    }
}
