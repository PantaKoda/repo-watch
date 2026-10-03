using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using RepoWatch.Core.Accounts;
using RepoWatch.Core.Configuration;
using RepoWatch.Core.Identity;
using RepoWatch.Core.Platform;
using RepoWatch.Desktop.Infrastructure;
using RepoWatch.Desktop.Platform;
using RepoWatch.Desktop.Services;
using RepoWatch.Desktop.ViewModels;
using RepoWatch.GitHub;

namespace RepoWatch.Desktop.Tests;

/// <summary>Answers GitHub requests from a queue and records them (synthetic contract fixtures).</summary>
internal sealed class QueueHandler : HttpMessageHandler
{
    private readonly ConcurrentQueue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();

    public ConcurrentQueue<string> Requests { get; } = new();

    public QueueHandler Json(string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        _responses.Enqueue(_ => new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        return this;
    }

    public QueueHandler Status(HttpStatusCode status)
    {
        _responses.Enqueue(_ => new HttpResponseMessage(status));
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Enqueue($"{request.Method} {request.RequestUri!.AbsolutePath}");
        return _responses.TryDequeue(out var respond)
            ? Task.FromResult(respond(request))
            : throw new InvalidOperationException($"Unexpected request {request.Method} {request.RequestUri}");
    }
}

internal sealed class MemoryCredentialStore : ICredentialStore
{
    public ConcurrentDictionary<AccountKey, StoredCredential> Items { get; } = new();

    public bool FailWrites { get; set; }

    public bool IsPersistent => true;

    public string Description => "test credential store";

    public Task<StoredCredential?> ReadAsync(AccountKey account, CancellationToken cancellationToken = default) =>
        Task.FromResult(Items.TryGetValue(account, out var credential) ? credential : null);

    public Task WriteAsync(AccountKey account, StoredCredential credential, CancellationToken cancellationToken = default)
    {
        if (FailWrites)
        {
            throw new IOException("credential store unavailable");
        }

        Items[account] = credential;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(AccountKey account, CancellationToken cancellationToken = default)
    {
        Items.TryRemove(account, out _);
        return Task.CompletedTask;
    }
}

/// <summary>Everything an <see cref="AccountService"/> needs, with shared stores so a restart can be simulated.</summary>
internal sealed class AccountKit
{
    public const string ClientId = "Iv23liTestClient01";

    public static readonly AccountKey Octo = new("github.com", 4242);

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));

    public QueueHandler Http { get; } = new();

    public InMemorySettingsStore SettingsStore { get; } = new();

    public MemoryCredentialStore Credentials { get; } = new();

    public RepoWatchOptions Options { get; } = new() { GitHub = { ClientId = ClientId, AppSlug = "repo-watch-test" } };

    public SettingsService Settings { get; private set; } = null!;

    public MonitorHost Monitors { get; private set; } = null!;

    public AccountService Accounts { get; private set; } = null!;

    public WatchlistService Watchlist { get; private set; } = null!;

    public AccessCatalogService Catalog { get; private set; } = null!;

    public MonitorCoordinator Coordinator { get; private set; } = null!;

    /// <summary>Optional monitor factory handed to the coordinator; null lists repositories without loading them.</summary>
    public IRepositoryMonitorFactory? MonitorFactory { get; set; }

    /// <summary>Optional repository cache handed to the account service (cleared on sign-out).</summary>
    public RepoWatch.Desktop.Storage.RepositoryCache? Cache { get; set; }

    public static string DeviceCodeJson =>
        """{"device_code":"3584d83530557fdd1f46af8289938c8ef79f9dc5","user_code":"WDJB-MJHT","verification_uri":"https://github.com/login/device","expires_in":900,"interval":5}""";

    public static string TokenJson(string access = "ghu_a", string refresh = "ghr_a") =>
        $$"""{"access_token":"{{access}}","expires_in":28800,"refresh_token":"{{refresh}}","refresh_token_expires_in":15897600,"token_type":"bearer","scope":""}""";

    public static string UserJson(long id = 4242, string login = "octo-test") =>
        $$"""{"login":"{{login}}","id":{{id}},"name":"Octo Test","avatar_url":"https://avatars.githubusercontent.com/u/{{id}}"}""";

    /// <summary>Creates (or re-creates, simulating a restart) the services over the shared stores.</summary>
    public AccountKit Start()
    {
        Settings = new SettingsService(() => SettingsStore, NullLogger<SettingsService>.Instance);
        Settings.Load();
        Monitors = new MonitorHost(Time);
        var http = GitHubHttp.CreateClient(Http);
        var endpoints = new GitHubEndpoints(Options.GitHub);
        Accounts = new AccountService(Options, endpoints, http, Credentials, Settings, new ImmediateDispatcher(), Time, NullLoggerFactory.Instance, Cache);
        Watchlist = new WatchlistService(Settings, Accounts);
        Catalog = new AccessCatalogService(Accounts, Watchlist, http, endpoints, new ImmediateDispatcher(), Time, NullLogger<AccessCatalogService>.Instance);
        Coordinator = new MonitorCoordinator(Accounts, Watchlist, Monitors, MonitorFactory);
        return this;
    }

    public async Task<SignInResult> SignInAsync(params string[] pollResponses)
    {
        Http.Json(DeviceCodeJson);
        foreach (var response in pollResponses)
        {
            Http.Json(response);
        }

        var code = await Accounts.BeginSignInAsync(CancellationToken.None);
        return await Drive(Accounts.CompleteSignInAsync(code, null, CancellationToken.None));
    }

    public async Task Drive(Task task) => await Drive(task.ContinueWith(_ => true, TaskScheduler.Default));

    public async Task<T> Drive<T>(Task<T> task)
    {
        for (var i = 0; i < 5000 && !task.IsCompleted; i++)
        {
            await Task.Delay(1);
            if (!task.IsCompleted)
            {
                Time.Advance(TimeSpan.FromSeconds(1));
            }
        }

        return await task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}

internal static class SettingsViewModels
{
    public static SettingsViewModel Create(SettingsService settings, MonitorHost monitors, FakeShell shell, RepoWatchOptions? options = null,
        DesktopIntegration? integration = null, RepoWatch.Desktop.Updates.UpdateService? updates = null)
    {
        options ??= new RepoWatchOptions();
        var endpoints = new GitHubEndpoints(options.GitHub);
        var http = GitHubHttp.CreateClient(new QueueHandler());
        var accounts = new AccountService(options, endpoints, http, new MemoryCredentialStore(), settings,
            new ImmediateDispatcher(), TimeProvider.System, NullLoggerFactory.Instance);
        var account = new AccountViewModel(accounts, shell, new RecordingBrowser(), new AvatarLoader(http, NullLogger<AvatarLoader>.Instance),
            endpoints, new ImmediateDispatcher(), TimeProvider.System);
        var paths = new AppPaths(Path.GetTempPath(), "d.json", "u.json", "logs");
        return new SettingsViewModel(settings, monitors, shell, new ImmediateDispatcher(), account, new WatchlistService(settings, accounts), paths, new VisualStateService(), integration, updates, options);
    }
}
