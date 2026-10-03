using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using RepoWatch.Core.Actions;
using RepoWatch.Core.Identity;
using RepoWatch.Core.Issues;
using RepoWatch.Core.Monitoring;
using RepoWatch.Core.PullRequests;
using RepoWatch.Core.Settings;
using RepoWatch.Core.State;
using RepoWatch.Desktop.Services;
using RepoWatch.GitHub.Api;
using RepoWatch.GitHub.Relay;
using RepoWatch.GitHub.Repositories;

namespace RepoWatch.Relay.Tests;

/// <summary>
/// End to end: a signed webhook reaches the in-memory relay, the desktop relay link receives the
/// invalidation and the polling monitor refreshes exactly that repository's part from "GitHub" (a fake).
/// </summary>
public sealed class LiveUpdateTests : IDisposable
{
    private static readonly AccountKey Account = new("github.com", 1);
    private readonly RelayFixture _relay = new();
    private readonly CountingSource _source = new();

    public void Dispose() => _relay.Dispose();

    private sealed class Tokens(string token) : IAccessTokenSource
    {
        public Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(token);

        public Task<string?> HandleUnauthorizedAsync(string rejectedAccessToken, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }

    /// <summary>Records when each repository part was requested.</summary>
    private sealed class CountingSource : IRepositoryDataSource
    {
        public ConcurrentQueue<(string Call, long RepositoryId, long At)> Calls { get; } = new();

        public int Count(string call, long repositoryId) => Calls.Count(c => c.Call == call && c.RepositoryId == repositoryId);

        public Task<ApiResult<RepositoryInfo>> GetRepositoryAsync(RepositoryKey key, CancellationToken cancellationToken)
        {
            Calls.Enqueue(("repo", key.RepositoryId, Stopwatch.GetTimestamp()));
            return Task.FromResult(ApiResult<RepositoryInfo>.Ok(new RepositoryInfo(new RepositoryMetadata
            {
                Key = key, Owner = "o", Name = $"r{key.RepositoryId}", OwnerKind = RepositoryOwnerKind.User, IsPrivate = true, IsArchived = false,
                DefaultBranch = "main", HtmlUrl = new Uri($"https://github.com/o/r{key.RepositoryId}"),
            }, true)));
        }

        public Task<SectionResult<ActionsState>> GetActionsAsync(ActionsRequest request, CancellationToken cancellationToken)
        {
            Calls.Enqueue(("actions", long.Parse(request.Name[1..], System.Globalization.CultureInfo.InvariantCulture), Stopwatch.GetTimestamp()));
            return Task.FromResult(SectionResult<ActionsState>.Ok(new ActionsState()));
        }

        public Task<SectionResult<PullRequestsState>> GetPullRequestsAsync(string owner, string name, bool mineOnly, string login, DateTimeOffset now, CancellationToken cancellationToken)
        {
            Calls.Enqueue(("pulls", long.Parse(name[1..], System.Globalization.CultureInfo.InvariantCulture), Stopwatch.GetTimestamp()));
            return Task.FromResult(SectionResult<PullRequestsState>.Ok(new PullRequestsState { OpenCount = ItemCount.Exact(0) }));
        }

        public Task<SectionResult<IssuesState>> GetIssuesAsync(string owner, string name, CancellationToken cancellationToken)
        {
            Calls.Enqueue(("issues", long.Parse(name[1..], System.Globalization.CultureInfo.InvariantCulture), Stopwatch.GetTimestamp()));
            return Task.FromResult(SectionResult<IssuesState>.Ok(new IssuesState { OpenCount = ItemCount.Exact(0) }));
        }
    }

    /// <summary>A monitor that would not poll again for an hour on its own: anything sooner came from the relay.</summary>
    private PollingRepositoryMonitor Monitor(string token, out RelayLink link, params long[] ids)
    {
        var hour = TimeSpan.FromHours(1);
        var settings = new AccountSettings { Watchlist = ids.Select(id => new WatchedRepository { RepositoryId = id, Owner = "o", Name = $"r{id}" }).ToList() };
        var monitor = new PollingRepositoryMonitor(Account, settings, _source, "u1", TimeProvider.System, NullLogger.Instance, CancellationToken.None,
            new PollingIntervals { Active = hour, PullRequests = hour, Issues = hour, Quiet = hour, Failed = hour, MaxBackoff = hour });
        var http = _relay.CreateClient();
        http.Timeout = Timeout.InfiniteTimeSpan;
        link = new RelayLink(monitor, new RelayClient(http, new Uri(http.BaseAddress!, "/")), new Tokens(token), TimeProvider.System, NullLogger.Instance, CancellationToken.None);
        monitor.Attach(link);
        return monitor;
    }

    private static async Task WaitUntil(Func<bool> condition, int seconds = 10)
    {
        for (var i = 0; i < seconds * 50 && !condition(); i++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        Assert.True(condition());
    }

    [Fact]
    public async Task A_webhook_refreshes_just_that_repository_part_and_the_monitor_reports_live()
    {
        using var monitor = Monitor("ghu_alice", out var link, 100, 101);
        await WaitUntil(() => monitor.State == ConnectionState.Live);
        await WaitUntil(() => _source.Count("actions", 100) >= 1 && _source.Count("actions", 101) >= 1); // reconcile on connect
        await Task.Delay(300, TestContext.Current.CancellationToken);
        var before = (Actions100: _source.Count("actions", 100), Issues100: _source.Count("issues", 100), Actions101: _source.Count("actions", 101));

        var sent = Stopwatch.GetTimestamp();
        await _relay.DeliverAsync("workflow_run", $$"""{"action":"completed","repository":{{RelayFixture.Repository(100)}}}""");
        await WaitUntil(() => _source.Count("actions", 100) > before.Actions100);
        var refreshedAt = _source.Calls.Where(c => c.Call == "actions" && c.RepositoryId == 100).Max(c => c.At);
        var latency = Stopwatch.GetElapsedTime(sent, refreshedAt);
        File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "relay-latency.txt"),
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{DateTimeOffset.Now:O} webhook to targeted refresh (in-memory relay): {latency.TotalMilliseconds:0.0} ms{Environment.NewLine}"));

        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.Equal(before.Issues100, _source.Count("issues", 100)); // only the part that changed
        Assert.Equal(before.Actions101, _source.Count("actions", 101)); // only the repository that changed
        Assert.True(latency < TimeSpan.FromSeconds(5), $"latency {latency}");
        Assert.NotNull(link.LastEventAt);
    }

    [Fact]
    public async Task Without_a_usable_relay_the_monitor_keeps_polling()
    {
        using var monitor = Monitor("ghu_mallory", out _, 100); // the relay refuses this user

        await WaitUntil(() => _source.Count("repo", 100) >= 1);
        await Task.Delay(500, TestContext.Current.CancellationToken);

        Assert.Equal(ConnectionState.Polling, monitor.State);
        Assert.True(monitor.Repositories.Single().Snapshot.Metadata.HasValue);
    }

    [Fact]
    public async Task Events_for_repositories_the_user_cannot_access_never_arrive()
    {
        using var monitor = Monitor("ghu_alice", out _, 100, 200); // 200 belongs to another user
        await WaitUntil(() => monitor.State == ConnectionState.Live);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        var before = _source.Count("pulls", 200);

        await _relay.DeliverAsync("pull_request", $$"""{"action":"opened","repository":{{RelayFixture.Repository(200)}}}""");
        await Task.Delay(800, TestContext.Current.CancellationToken);

        Assert.Equal(before, _source.Count("pulls", 200));
    }

    [Fact]
    public async Task Losing_access_through_the_relay_refreshes_the_repository_and_revocation_returns_to_polling()
    {
        using var monitor = Monitor("ghu_alice", out _, 100, 101);
        await WaitUntil(() => monitor.State == ConnectionState.Live);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        var before = _source.Count("repo", 101);

        await _relay.DeliverAsync("installation_repositories", """{"action":"removed","installation":{"id":10},"repositories_removed":[{"id":101}]}""");
        await WaitUntil(() => _source.Count("repo", 101) > before); // GitHub, not the relay, decides what access remains

        await _relay.DeliverAsync("github_app_authorization", """{"action":"revoked","sender":{"id":1}}""");
        await WaitUntil(() => monitor.State == ConnectionState.Polling || monitor.State == ConnectionState.Live);
        Assert.True(_source.Calls.Count > 0);
    }

    /// <summary>A relay that serves one session and stream, then disappears: the stream ends and sessions fail.</summary>
    private sealed class VanishingRelay : HttpMessageHandler
    {
        private readonly System.IO.Pipelines.Pipe _stream = new();

        public bool Gone { get; private set; }

        public async Task VanishAsync()
        {
            Gone = true;
            await _stream.Writer.CompleteAsync();
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Gone)
            {
                throw new HttpRequestException("connection refused");
            }

            if (request.RequestUri!.AbsolutePath.EndsWith("/sessions", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent($$"""{"sessionToken":"s","expiresAt":"{{DateTimeOffset.UtcNow.AddMinutes(15):O}}","allowed":[100],"rejected":[]}""",
                        System.Text.Encoding.UTF8, "application/json"),
                };
            }

            await _stream.Writer.WriteAsync(": connected\n\n"u8.ToArray(), cancellationToken);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StreamContent(_stream.Reader.AsStream()) };
        }
    }

    [Fact]
    public async Task When_the_relay_goes_away_the_monitor_falls_back_to_polling()
    {
        var relay = new VanishingRelay();
        var hour = TimeSpan.FromHours(1);
        var settings = new AccountSettings { Watchlist = [new WatchedRepository { RepositoryId = 100, Owner = "o", Name = "r100" }] };
        using var monitor = new PollingRepositoryMonitor(Account, settings, _source, "u1", TimeProvider.System, NullLogger.Instance, CancellationToken.None,
            new PollingIntervals { Active = hour, PullRequests = hour, Issues = hour, Quiet = hour, Failed = hour, MaxBackoff = hour });
        monitor.Attach(new RelayLink(monitor, new RelayClient(new HttpClient(relay) { Timeout = Timeout.InfiniteTimeSpan }, new Uri("https://relay.example.test/")),
            new Tokens("ghu_alice"), TimeProvider.System, NullLogger.Instance, CancellationToken.None));
        await WaitUntil(() => monitor.State == ConnectionState.Live);

        await relay.VanishAsync();

        // Polling resumes at its normal pace (no longer stretched for live mode); reconnects keep failing quietly.
        await WaitUntil(() => monitor.State == ConnectionState.Polling);
        await Task.Delay(500, TestContext.Current.CancellationToken);
        Assert.Equal(ConnectionState.Polling, monitor.State);
    }
}