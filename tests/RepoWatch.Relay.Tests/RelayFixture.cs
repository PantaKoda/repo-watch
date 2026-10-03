using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using RepoWatch.Core.Relay;

namespace RepoWatch.Relay.Tests;

/// <summary>
/// The relay in memory with a fake GitHub API. The fake knows two users: token "ghu_alice" (user 1) can see
/// repositories 100 and 101 through installation 10; "ghu_bob" (user 2) sees repository 200 through installation 20.
/// </summary>
/// <param name="time">Optional clock for the relay (session expiry tests).</param>
public sealed class RelayFixture(TimeProvider? time = null) : WebApplicationFactory<Program>
{
    public const string Secret = "test-webhook-secret-0123456789";

    private readonly string _database = Path.Combine(Path.GetTempPath(), "relay-" + Guid.NewGuid().ToString("N") + ".db");

    public FakeGitHub GitHub { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Relay:WebhookSecret", Secret);
        builder.UseSetting("Relay:AppId", "77");
        builder.UseSetting("Relay:DatabasePath", _database);
        builder.UseSetting("Relay:GitHubApiBaseUrl", "http://localhost:5999/api/");
        builder.UseSetting("Relay:ReplayCapacity", "20");
        builder.UseSetting("Relay:KeepAliveSeconds", "1");
        builder.ConfigureTestServices(services =>
        {
            services.AddHttpClient<GitHubAccessClient>().ConfigurePrimaryHttpMessageHandler(() => GitHub);
            if (time is not null)
            {
                services.AddSingleton(time);
            }
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _database, _database + "-wal", _database + "-shm" })
        {
            File.Delete(file);
        }
    }

    public async Task<HttpResponseMessage> DeliverAsync(string eventName, string json, string? deliveryId = null, string? signature = null)
    {
        var body = Encoding.UTF8.GetBytes(json);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/github") { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Add("X-GitHub-Event", eventName);
        request.Headers.Add("X-GitHub-Delivery", deliveryId ?? Guid.NewGuid().ToString());
        request.Headers.Add(WebhookSignature.Header, signature ?? WebhookSignature.Sign(Secret, body));
        return await CreateClient().SendAsync(request);
    }

    public async Task<RelaySessionResponse> CreateSessionAsync(string userToken, params long[] repositoryIds)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/sessions")
        {
            Content = new StringContent(JsonSerializer.Serialize(new RelaySessionRequest(repositoryIds), RelayJsonContext.Default.RelaySessionRequest), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", userToken);
        using var response = await CreateClient().SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync(RelayJsonContext.Default.RelaySessionResponse))!;
    }

    /// <summary>Opens the event stream and returns a reader of parsed events.</summary>
    public async Task<SseReader> OpenEventsAsync(string sessionToken, long? lastEventId = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/events");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", sessionToken);
        if (lastEventId is { } id)
        {
            request.Headers.Add("Last-Event-ID", id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        var response = await CreateClient().SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        return new SseReader(response);
    }

    public static string Repository(long id) => "{\"id\":" + id + ",\"name\":\"r" + id + "\",\"owner\":{\"login\":\"o\"}}";
}

/// <summary>
/// A minimal GitHub API for /user, /user/installations and /user/installations/{id}/repositories. Repo Watch's
/// app is 77; "ghu_otherapp" is a token of another app (its installation belongs to app 99).
/// </summary>
public sealed class FakeGitHub : HttpMessageHandler
{
    public List<string> Authorizations { get; } = [];

    /// <summary>When set, repository listings fail with this status (e.g. a transient 502).</summary>
    public HttpStatusCode? RepositoryListFailure { get; set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = request.Headers.Authorization?.Parameter;
        lock (Authorizations)
        {
            Authorizations.Add(request.RequestUri!.AbsoluteUri);
        }

        var (userId, installation, repositories) = token switch
        {
            "ghu_alice" => (1, 10, new long[] { 100, 101 }),
            "ghu_bob" => (2, 20, new long[] { 200 }),
            "ghu_otherapp" => (3, 30, new long[] { 100 }),
            _ => (0, 0, Array.Empty<long>()),
        };
        if (userId == 0)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        }

        var path = request.RequestUri!.AbsolutePath;
        if (RepositoryListFailure is { } failure && path.EndsWith("/repositories", StringComparison.Ordinal))
        {
            return Task.FromResult(new HttpResponseMessage(failure));
        }

        var json = path.EndsWith("/user", StringComparison.Ordinal) ? $$"""{"id":{{userId}},"login":"u{{userId}}"}"""
            : path.EndsWith("/user/installations", StringComparison.Ordinal)
                ? "{\"total_count\":1,\"installations\":[{\"id\":" + installation + ",\"app_id\":" + (token == "ghu_otherapp" ? 99 : 77) + ",\"suspended_at\":null}]}"
            : path.EndsWith($"/user/installations/{installation}/repositories", StringComparison.Ordinal)
                ? "{\"total_count\":" + repositories.Length + ",\"repositories\":[" + string.Join(",", repositories.Select(r => "{\"id\":" + r + "}")) + "]}"
            : null;
        return Task.FromResult(json is null
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }
}

public sealed record SseEvent(long? Id, string Name, string Data);

/// <summary>Reads server-sent events from a streaming response.</summary>
public sealed class SseReader(HttpResponseMessage response) : IDisposable
{
    private StreamReader? _reader;

    public HttpStatusCode Status => response.StatusCode;

    /// <summary>The next event (comments and keep-alives skipped), or null if the stream ended or timed out.</summary>
    public async Task<SseEvent?> NextAsync(TimeSpan timeout)
    {
        _reader ??= new StreamReader(await response.Content.ReadAsStreamAsync());
        using var cancel = new CancellationTokenSource(timeout);
        long? id = null;
        string? name = null;
        var data = new StringBuilder();
        try
        {
            while (await _reader.ReadLineAsync(cancel.Token) is { } line)
            {
                if (line.Length == 0)
                {
                    if (name is not null)
                    {
                        return new SseEvent(id, name, data.ToString());
                    }

                    continue;
                }

                if (line.StartsWith("id: ", StringComparison.Ordinal))
                {
                    id = long.Parse(line[4..], System.Globalization.CultureInfo.InvariantCulture);
                }
                else if (line.StartsWith("event: ", StringComparison.Ordinal))
                {
                    name = line[7..];
                }
                else if (line.StartsWith("data: ", StringComparison.Ordinal))
                {
                    data.Append(line[6..]);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }

        return null;
    }

    public void Dispose()
    {
        _reader?.Dispose();
        response.Dispose();
    }
}
