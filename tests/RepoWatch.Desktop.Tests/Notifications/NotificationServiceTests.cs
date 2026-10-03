using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using RepoWatch.Core.Actions;
using RepoWatch.Core.Identity;
using RepoWatch.Core.Monitoring;
using RepoWatch.Core.Settings;
using RepoWatch.Core.State;
using RepoWatch.Core.Status;
using RepoWatch.Desktop.Platform.Notifications;
using RepoWatch.Desktop.Services;
using RepoWatch.Desktop.Storage;

namespace RepoWatch.Desktop.Tests.Notifications;

internal sealed class FakeMonitor : IRepositoryMonitor
{
    public ConnectionState State { get; set; } = ConnectionState.Polling;

    public IReadOnlyList<MonitoredRepository> Repositories { get; set; } = [];

    public bool IsRefreshing => false;

    public event EventHandler? Changed;

    public void Publish(params MonitoredRepository[] repositories)
    {
        Repositories = repositories;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Task RefreshAsync(RepositoryKey? repository = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

internal sealed class RecordingSink : INotificationSink
{
    public List<DesktopNotification> Shown { get; } = [];

    public NotificationAvailability Availability { get; set; } = NotificationAvailability.Available;

    public bool Show(DesktopNotification notification)
    {
        if (Availability != NotificationAvailability.Available)
        {
            return false;
        }

        Shown.Add(notification);
        return true;
    }
}

public sealed class NotificationServiceTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "repowatch-notify-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));
    private AccountKit _kit = null!;
    private RepositoryCache _cache = null!;
    private readonly FakeMonitor _monitor = new();
    private readonly RecordingSink _sink = new();

    public async ValueTask InitializeAsync()
    {
        var database = new LocalDatabase(Path.Combine(_directory, "repowatch.db"));
        database.Initialize();
        _cache = new RepositoryCache(database, _time, NullLogger<RepositoryCache>.Instance);
        _kit = new AccountKit { Cache = _cache }.Start();
        await _kit.SignInAsync(AccountKit.TokenJson(), AccountKit.UserJson());
        Account = _kit.Accounts.Identity!.Account;
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
        _kit.Monitors.SetBase(_monitor);
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
        return ValueTask.CompletedTask;
    }

    private NotificationService Service() =>
        new(_kit.Monitors, _kit.Settings, _kit.Accounts, _cache, _sink, _time, NullLogger<NotificationService>.Instance);

    private AccountKey Account { get; set; } = null!;

    private MonitoredRepository Repository(string sha, CheckOutcome outcome, bool notifications = true, long id = 7)
    {
        var key = new RepositoryKey(Account, id);
        var run = new WorkflowRun
        {
            Id = sha.GetHashCode(StringComparison.Ordinal) & 0xFFFF, WorkflowId = 1, WorkflowName = "CI", RunNumber = 1, RunAttempt = 1, HeadSha = sha, HeadBranch = "main",
            Event = "push", Outcome = outcome, HtmlUrl = new Uri("https://github.com/octo/hello/actions/runs/1"), CreatedAt = _time.GetUtcNow(), UpdatedAt = _time.GetUtcNow(),
        };
        var health = WorkflowRunSelection.ForCommit([run], sha, "main");
        var snapshot = new RepositorySnapshot(key)
        {
            Metadata = Resource<RepositoryMetadata>.NotLoaded.Succeeded(new RepositoryMetadata
            {
                Key = key, Owner = "octo", Name = "hello", OwnerKind = RepositoryOwnerKind.User, IsPrivate = true, IsArchived = false,
                DefaultBranch = "main", HtmlUrl = new Uri("https://github.com/octo/hello"),
            }, _time.GetUtcNow()),
            Actions = Resource<ActionsState>.NotLoaded.Succeeded(new ActionsState { RecentRuns = [run], DefaultBranch = health, Branches = [health] }, _time.GetUtcNow()),
        };
        return new MonitoredRepository(new WatchedRepository { RepositoryId = id, Owner = "octo", Name = "hello", NotificationsEnabled = notifications }, snapshot, 0);
    }

    [Fact]
    public void Starting_with_a_failing_repository_notifies_nothing()
    {
        _monitor.Repositories = [Repository("aaa", CheckOutcome.Failure)];
        using var service = Service();

        _monitor.Publish(Repository("aaa", CheckOutcome.Failure));

        Assert.Empty(_sink.Shown);
        Assert.Contains(_cache.ForAccount(Account).NotificationHistory(), h => h.Outcome == "baseline");
    }

    [Fact]
    public void A_new_failure_is_announced_once_also_across_a_restart()
    {
        _monitor.Repositories = [Repository("aaa", CheckOutcome.Success)];
        using (var service = Service())
        {
            _monitor.Publish(Repository("bbb", CheckOutcome.Failure));
            _monitor.Publish(Repository("bbb", CheckOutcome.Failure)); // the same state again (e.g. partial progress)
        }

        var shown = Assert.Single(_sink.Shown);
        Assert.Equal("CI failing in octo/hello", shown.Title);
        Assert.Equal(new Uri("https://github.com/octo/hello/actions/runs/1"), shown.Url);

        // Restart: a new service sees the same failing state; the history says it was announced.
        using (var restarted = Service())
        {
            _monitor.Publish(Repository("bbb", CheckOutcome.Failure));
        }

        Assert.Single(_sink.Shown);
    }

    [Fact]
    public void After_a_restart_events_that_happened_while_closed_are_not_announced()
    {
        // The monitor first shows data restored from the cache (as after a restart)...
        var cached = Repository("aaa", CheckOutcome.Success);
        cached = cached with
        {
            Snapshot = cached.Snapshot with { Actions = Resource<ActionsState>.FromCache(cached.Snapshot.Actions.Value!, _time.GetUtcNow().AddDays(-2)) },
        };
        _monitor.Repositories = [cached];
        using var service = Service();

        // ...then the first live refresh shows a failure that happened while Repo Watch was closed: silent.
        _monitor.Publish(Repository("bbb", CheckOutcome.Failure));
        Assert.Empty(_sink.Shown);

        // From now on, changes are news.
        _monitor.Publish(Repository("ccc", CheckOutcome.Success));
        Assert.Equal("CI recovered in octo/hello", Assert.Single(_sink.Shown).Title);
    }

    [Fact]
    public void Quiet_hours_skip_events_without_saving_them_for_later()
    {
        _kit.Settings.UpdateApp(s => s with { Notifications = s.Notifications with { QuietHours = new QuietHours { Enabled = true, Start = new(11, 0), End = new(13, 0) } } });
        _monitor.Repositories = [Repository("aaa", CheckOutcome.Success)];
        using var service = Service();

        _monitor.Publish(Repository("bbb", CheckOutcome.Failure)); // 12:00: quiet
        _time.Advance(TimeSpan.FromHours(2)); // 14:00: quiet hours over
        _monitor.Publish(Repository("bbb", CheckOutcome.Failure));

        Assert.Empty(_sink.Shown);
        Assert.Contains(_cache.ForAccount(Account).NotificationHistory(), h => h.Outcome == "quiet");

        _monitor.Publish(Repository("ccc", CheckOutcome.Success)); // a later event is announced normally
        Assert.Equal("CI recovered in octo/hello", Assert.Single(_sink.Shown).Title);
    }

    [Fact]
    public void Switched_off_repositories_and_kinds_stay_silent()
    {
        _monitor.Repositories = [Repository("aaa", CheckOutcome.Success, notifications: false)];
        using var service = Service();

        _monitor.Publish(Repository("bbb", CheckOutcome.Failure, notifications: false));
        _kit.Settings.UpdateApp(s => s with { Notifications = s.Notifications with { CiRecovery = false } });
        _monitor.Publish(Repository("ccc", CheckOutcome.Success));

        Assert.Empty(_sink.Shown);
    }

    [Fact]
    public void Private_details_are_left_out_when_asked()
    {
        _kit.Settings.UpdateApp(s => s with { Notifications = s.Notifications with { HidePrivateDetails = true } });
        _monitor.Repositories = [Repository("aaa", CheckOutcome.Success)];
        using var service = Service();

        _monitor.Publish(Repository("bbb", CheckOutcome.Failure));

        var shown = Assert.Single(_sink.Shown);
        Assert.DoesNotContain("octo/hello", shown.Title + shown.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void A_refusing_system_is_handled_and_does_not_cause_a_later_burst()
    {
        _sink.Availability = NotificationAvailability.DisabledByUser;
        _monitor.Repositories = [Repository("aaa", CheckOutcome.Success)];
        using var service = Service();

        _monitor.Publish(Repository("bbb", CheckOutcome.Failure));
        _sink.Availability = NotificationAvailability.Available; // the user turns notifications back on
        _monitor.Publish(Repository("bbb", CheckOutcome.Failure));

        Assert.Empty(_sink.Shown);
        Assert.Equal(NotificationAvailability.Available, service.Availability);
    }

    [Fact]
    public void Removed_repositories_and_demo_data_never_notify()
    {
        _monitor.Repositories = [Repository("aaa", CheckOutcome.Success), Repository("aaa", CheckOutcome.Success, id: 8)];
        using var service = Service();

        _monitor.Publish(Repository("aaa", CheckOutcome.Success)); // repository 8 removed
        _monitor.Publish(Repository("aaa", CheckOutcome.Success), Repository("bbb", CheckOutcome.Failure, id: 8)); // added back: a fresh baseline
        Assert.Empty(_sink.Shown);

        _kit.Monitors.EnterDemo();
        _monitor.Publish(Repository("ccc", CheckOutcome.Failure));
        Assert.Empty(_sink.Shown);
    }

    [Fact]
    public async Task Signing_out_stops_notifications_and_clears_their_history()
    {
        _monitor.Repositories = [Repository("aaa", CheckOutcome.Success)];
        using var service = Service();
        _monitor.Publish(Repository("bbb", CheckOutcome.Failure));
        var account = Account;

        await _kit.Accounts.SignOutAsync();
        _monitor.Publish(Repository("ccc", CheckOutcome.Success));

        Assert.Single(_sink.Shown);
        Assert.Empty(_cache.ForAccount(account).NotificationHistory());
    }
}
