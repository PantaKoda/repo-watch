using System.Net;
using RepoWatch.GitHub.Auth;
using static RepoWatch.GitHub.Tests.Fixture;

namespace RepoWatch.GitHub.Tests;

public sealed class DeviceFlowTests
{
    private static (DeviceFlowClient Client, StubHandler Handler, Microsoft.Extensions.Time.Testing.FakeTimeProvider Time) Create()
    {
        var time = Time();
        var handler = new StubHandler(time);
        var client = new DeviceFlowClient(GitHubHttp.CreateClient(handler), Endpoints, ClientId, time);
        return (client, handler, time);
    }

    [Fact]
    public async Task Device_code_request_sends_only_the_public_client_id()
    {
        var (client, handler, time) = Create();
        handler.Json(DeviceCodeJson);

        var code = await client.RequestCodeAsync(TestContext.Current.CancellationToken);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://github.com/login/device/code", request.Uri.ToString());
        Assert.Equal(new Dictionary<string, string> { ["client_id"] = ClientId }, request.Form);
        Assert.Contains("application/json", request.Message.Headers.Accept.ToString(), StringComparison.Ordinal);
        Assert.StartsWith("RepoWatch/", request.Message.Headers.UserAgent.ToString(), StringComparison.Ordinal);

        Assert.Equal("WDJB-MJHT", code.UserCode);
        Assert.Equal(new Uri("https://github.com/login/device"), code.VerificationUri);
        Assert.Equal(time.GetUtcNow().AddSeconds(900), code.ExpiresAt);
        Assert.Equal(TimeSpan.FromSeconds(5), code.Interval);
        Assert.DoesNotContain(code.DeviceCode, code.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Device_code_errors_are_reported()
    {
        var (client, handler, _) = Create();
        handler.Json("""{"error":"incorrect_client_credentials","error_description":"The client_id is not valid."}""");

        var ex = await Assert.ThrowsAsync<DeviceFlowException>(() => client.RequestCodeAsync(TestContext.Current.CancellationToken));

        Assert.Equal("incorrect_client_credentials", ex.Error);
    }

    [Fact]
    public async Task An_unknown_client_id_is_reported_as_an_error_not_a_transient_failure()
    {
        // Response observed from github.com for a client ID that does not exist (October 2026).
        var (client, handler, _) = Create();
        handler.Json("""{"error":"Not Found"}""", HttpStatusCode.NotFound);

        var ex = await Assert.ThrowsAsync<DeviceFlowException>(() => client.RequestCodeAsync(TestContext.Current.CancellationToken));

        Assert.Equal("Not Found", ex.Error);
    }

    [Fact]
    public async Task Approval_after_pending_polls_returns_tokens_and_respects_the_interval()
    {
        var (client, handler, time) = Create();
        handler.Json(DeviceCodeJson)
            .Json("""{"error":"authorization_pending"}""")
            .Json("""{"error":"authorization_pending"}""")
            .Json(TokenJson("ghu_abc", "ghr_def"));
        var code = await client.RequestCodeAsync(TestContext.Current.CancellationToken);
        var start = time.GetUtcNow();

        var result = await Drive(new DeviceFlowSignIn(client, time).WaitForAuthorizationAsync(code, null, TestContext.Current.CancellationToken), time, TimeSpan.FromSeconds(1));

        Assert.Equal(DeviceFlowOutcome.Authorized, result.Outcome);
        Assert.Equal("ghu_abc", result.Credential!.AccessToken);
        Assert.Equal("ghr_def", result.Credential.RefreshToken);
        Assert.Equal(time.GetUtcNow().AddSeconds(28800), result.Credential.AccessTokenExpiresAt);

        var polls = handler.Requests.Skip(1).ToList();
        Assert.Equal(3, polls.Count);
        Assert.All(polls, p =>
        {
            Assert.Equal("https://github.com/login/oauth/access_token", p.Uri.ToString());
            Assert.Equal("urn:ietf:params:oauth:grant-type:device_code", p.Form["grant_type"]);
            Assert.Equal(ClientId, p.Form["client_id"]);
            Assert.False(p.Form.ContainsKey("client_secret"));
        });
        var times = new[] { start }.Concat(polls.Select(p => p.At)).ToList();
        for (var i = 1; i < times.Count; i++)
        {
            Assert.True(times[i] - times[i - 1] >= TimeSpan.FromSeconds(5), "polled faster than the interval");
        }
    }

    [Fact]
    public async Task Slow_down_increases_the_interval_for_all_later_polls()
    {
        var (client, handler, time) = Create();
        handler.Json(DeviceCodeJson)
            .Json("""{"error":"slow_down"}""")
            .Json("""{"error":"authorization_pending"}""")
            .Json(TokenJson());
        var code = await client.RequestCodeAsync(TestContext.Current.CancellationToken);
        var progress = new List<DeviceFlowProgress>();

        var result = await Drive(new DeviceFlowSignIn(client, time).WaitForAuthorizationAsync(code, new SyncProgress(progress), TestContext.Current.CancellationToken), time, TimeSpan.FromSeconds(1));

        Assert.Equal(DeviceFlowOutcome.Authorized, result.Outcome);
        var polls = handler.Requests.Skip(1).Select(r => r.At).ToList();
        Assert.True(polls[1] - polls[0] >= TimeSpan.FromSeconds(10));
        Assert.True(polls[2] - polls[1] >= TimeSpan.FromSeconds(10));
        Assert.Contains(progress, p => p.Kind == DeviceFlowProgressKind.SlowedDown && p.Interval == TimeSpan.FromSeconds(10));
    }

    [Theory]
    [InlineData("""{"error":"access_denied"}""", DeviceFlowOutcome.Denied, null)]
    [InlineData("""{"error":"expired_token"}""", DeviceFlowOutcome.Expired, null)]
    [InlineData("""{"error":"device_flow_disabled"}""", DeviceFlowOutcome.Rejected, "device_flow_disabled")]
    [InlineData("""{"error":"incorrect_device_code"}""", DeviceFlowOutcome.Rejected, "incorrect_device_code")]
    public async Task Terminal_poll_errors_end_the_flow(string json, DeviceFlowOutcome outcome, string? error)
    {
        var (client, handler, time) = Create();
        handler.Json(DeviceCodeJson).Json(json);
        var code = await client.RequestCodeAsync(TestContext.Current.CancellationToken);

        var result = await Drive(new DeviceFlowSignIn(client, time).WaitForAuthorizationAsync(code, null, TestContext.Current.CancellationToken), time, TimeSpan.FromSeconds(1));

        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(error, result.Error);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task An_expired_code_stops_polling_without_another_request()
    {
        var (client, handler, time) = Create();
        handler.Json("""{"device_code":"d","user_code":"AAAA-BBBB","verification_uri":"https://github.com/login/device","expires_in":12,"interval":5}""")
            .Json("""{"error":"authorization_pending"}""")
            .Json("""{"error":"authorization_pending"}""");
        var code = await client.RequestCodeAsync(TestContext.Current.CancellationToken);

        var result = await Drive(new DeviceFlowSignIn(client, time).WaitForAuthorizationAsync(code, null, TestContext.Current.CancellationToken), time, TimeSpan.FromSeconds(1));

        Assert.Equal(DeviceFlowOutcome.Expired, result.Outcome);
        Assert.Equal(3, handler.Requests.Count); // code + polls at 5s and 10s, none after expiry
    }

    [Fact]
    public async Task Cancelling_stops_polling_immediately()
    {
        var (client, handler, time) = Create();
        handler.Json(DeviceCodeJson).Json("""{"error":"authorization_pending"}""");
        var code = await client.RequestCodeAsync(TestContext.Current.CancellationToken);
        using var cancel = new CancellationTokenSource();

        var flow = new DeviceFlowSignIn(client, time).WaitForAuthorizationAsync(code, null, cancel.Token);
        time.Advance(TimeSpan.FromSeconds(5));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        await cancel.CancelAsync();
        var result = await flow.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal(DeviceFlowOutcome.Cancelled, result.Outcome);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Transient_failures_keep_polling_until_approval()
    {
        var (client, handler, time) = Create();
        handler.Json(DeviceCodeJson)
            .Throws(new HttpRequestException("connection reset"))
            .Status(HttpStatusCode.BadGateway)
            .Json(TokenJson());
        var code = await client.RequestCodeAsync(TestContext.Current.CancellationToken);
        var progress = new List<DeviceFlowProgress>();

        var result = await Drive(new DeviceFlowSignIn(client, time).WaitForAuthorizationAsync(code, new SyncProgress(progress), TestContext.Current.CancellationToken), time, TimeSpan.FromSeconds(1));

        Assert.Equal(DeviceFlowOutcome.Authorized, result.Outcome);
        Assert.Equal(2, progress.Count(p => p.Kind == DeviceFlowProgressKind.RetryingAfterError));
    }

    private sealed class SyncProgress(List<DeviceFlowProgress> sink) : IProgress<DeviceFlowProgress>
    {
        public void Report(DeviceFlowProgress value)
        {
            lock (sink)
            {
                sink.Add(value);
            }
        }
    }
}
