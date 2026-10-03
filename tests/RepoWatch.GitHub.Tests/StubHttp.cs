using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using RepoWatch.Core.Accounts;
using RepoWatch.Core.Configuration;
using RepoWatch.Core.Identity;

namespace RepoWatch.GitHub.Tests;

/// <summary>A captured outgoing request, with its body read eagerly.</summary>
internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string> Form, HttpRequestMessage Message, DateTimeOffset At)
{
    public string? Header(string name) => Message.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;
}

/// <summary>
/// Answers requests from a queue of responders and records what was sent. Contract fixtures:
/// response bodies follow GitHub's documented shapes; values are synthetic.
/// </summary>
internal sealed class StubHandler(TimeProvider time) : HttpMessageHandler
{
    private readonly ConcurrentQueue<Func<RecordedRequest, Task<HttpResponseMessage>>> _responses = new();

    public List<RecordedRequest> Requests { get; } = [];

    public StubHandler Json(string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        _responses.Enqueue(_ => Task.FromResult(JsonResponse(json, status)));
        return this;
    }

    public StubHandler Status(HttpStatusCode status)
    {
        _responses.Enqueue(_ => Task.FromResult(new HttpResponseMessage(status)));
        return this;
    }

    public StubHandler Throws(Exception exception)
    {
        _responses.Enqueue(_ => throw exception);
        return this;
    }

    /// <summary>
    /// Holds the response until <paramref name="release"/> completes, simulating GitHub having
    /// processed the request while the response is still in flight. Ignores the caller's cancellation.
    /// </summary>
    public StubHandler Deferred(Task release, string json, TaskCompletionSource? received = null)
    {
        _responses.Enqueue(async _ =>
        {
            received?.TrySetResult();
            await release;
            return JsonResponse(json, HttpStatusCode.OK);
        });
        return this;
    }

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>();
        if (request.Content is FormUrlEncodedContent content)
        {
            var body = await content.ReadAsStringAsync(cancellationToken);
            foreach (var pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split('=', 2);
                form[Uri.UnescapeDataString(parts[0])] = Uri.UnescapeDataString(parts.Length > 1 ? parts[1].Replace('+', ' ') : "");
            }
        }

        var recorded = new RecordedRequest(request.Method, request.RequestUri!, form, request, time.GetUtcNow());
        lock (Requests)
        {
            Requests.Add(recorded);
        }

        return _responses.TryDequeue(out var respond)
            ? await respond(recorded)
            : throw new InvalidOperationException($"Unexpected request {request.Method} {request.RequestUri}");
    }
}

internal sealed class MemoryCredentialStore : ICredentialStore
{
    public ConcurrentDictionary<AccountKey, StoredCredential> Items { get; } = new();

    public List<string> Operations { get; } = [];

    public bool FailWrites { get; set; }

    public bool IsPersistent => true;

    public string Description => "test store";

    public Task<StoredCredential?> ReadAsync(AccountKey account, CancellationToken cancellationToken = default) =>
        Task.FromResult(Items.TryGetValue(account, out var credential) ? credential : null);

    public Task WriteAsync(AccountKey account, StoredCredential credential, CancellationToken cancellationToken = default)
    {
        lock (Operations)
        {
            Operations.Add("write");
        }

        if (FailWrites)
        {
            throw new System.ComponentModel.Win32Exception(5, "Access is denied.");
        }

        Items[account] = credential;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(AccountKey account, CancellationToken cancellationToken = default)
    {
        lock (Operations)
        {
            Operations.Add("delete");
        }

        Items.TryRemove(account, out _);
        return Task.CompletedTask;
    }
}

internal static class Fixture
{
    public const string ClientId = "Iv23liTestClient01";

    public static readonly AccountKey Account = new("github.com", 4242);

    public static readonly GitHubEndpoints Endpoints = new(new GitHubOptions());

    public static FakeTimeProvider Time() => new(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));

    public static string TokenJson(string access = "ghu_new", string refresh = "ghr_new") =>
        $$"""{"access_token":"{{access}}","expires_in":28800,"refresh_token":"{{refresh}}","refresh_token_expires_in":15897600,"token_type":"bearer","scope":""}""";

    public const string DeviceCodeJson =
        """{"device_code":"3584d83530557fdd1f46af8289938c8ef79f9dc5","user_code":"WDJB-MJHT","verification_uri":"https://github.com/login/device","expires_in":900,"interval":5}""";

    /// <summary>Advances fake time in small steps until the task finishes, letting continuations run.</summary>
    public static async Task<T> Drive<T>(Task<T> task, FakeTimeProvider time, TimeSpan step, int maxSteps = 5000)
    {
        for (var i = 0; i < maxSteps && !task.IsCompleted; i++)
        {
            await Task.Delay(1);
            if (!task.IsCompleted)
            {
                time.Advance(step);
            }
        }

        return await task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
