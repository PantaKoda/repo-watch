using System.Net;
using System.Text.Json;
using RepoWatch.GitHub.Relay;
using static RepoWatch.GitHub.Tests.Fixture;

namespace RepoWatch.GitHub.Tests;

public sealed class RelayClientTests
{
    [Fact]
    public void The_parser_reads_ids_events_multi_line_data_and_skips_comments()
    {
        var parser = new SseParser();
        var lines = new[]
        {
            "retry: 5000", ": connected", "",
            "id: 42", "event: invalidate", "data: {\"repositoryId\":7,", "data: \"parts\":[\"actions\"]}", "",
            ": keep-alive", "",
            "event: reset", "data: {}", "",
        };

        var messages = lines.Select(parser.Feed).OfType<RelayMessage>().ToList();

        Assert.Equal(2, messages.Count);
        Assert.Equal(new RelayMessage(42, "invalidate", "{\"repositoryId\":7,\n\"parts\":[\"actions\"]}"), messages[0]);
        Assert.Equal(new RelayMessage(null, "reset", "{}"), messages[1]);
    }

    [Fact]
    public async Task Sessions_send_the_token_only_in_the_header()
    {
        var handler = new StubHandler(Time());
        handler.Json("""{"sessionToken":"s3ss10n","expiresAt":"2026-10-03T12:15:00+00:00","allowed":[1],"rejected":[2]}""");
        var client = new RelayClient(GitHubHttp.CreateClient(handler), new Uri("https://relay.example.test/"));

        var (session, failure) = await client.CreateSessionAsync("ghu_secret", [1, 2], TestContext.Current.CancellationToken);

        Assert.Null(failure);
        Assert.Equal([1L], session!.Allowed);
        var request = handler.Requests.Single();
        Assert.Equal("https://relay.example.test/sessions", request.Uri.ToString());
        Assert.Equal("Bearer ghu_secret", request.Header("Authorization"));
        Assert.DoesNotContain("ghu_secret", request.Uri.ToString() + request.Body, StringComparison.Ordinal);
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal([1L, 2L], body.RootElement.GetProperty("repositoryIds").EnumerateArray().Select(e => e.GetInt64()));
    }

    [Fact]
    public async Task An_unreachable_or_refusing_relay_is_reported_not_thrown()
    {
        var handler = new StubHandler(Time());
        handler.Status(HttpStatusCode.Unauthorized).Throws(new HttpRequestException("connection refused"));
        var client = new RelayClient(GitHubHttp.CreateClient(handler), new Uri("https://relay.example.test/"));

        var refused = await client.CreateSessionAsync("ghu_x", [1], TestContext.Current.CancellationToken);
        var down = await client.CreateSessionAsync("ghu_x", [1], TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, refused.Failure);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, down.Failure);
    }
}
