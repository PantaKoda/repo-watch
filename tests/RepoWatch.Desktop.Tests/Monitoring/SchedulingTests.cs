using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using RepoWatch.Core.Actions;
using RepoWatch.Core.Identity;
using RepoWatch.Core.Monitoring;
using RepoWatch.Core.Settings;
using RepoWatch.Core.State;
using RepoWatch.Desktop.Services;
using RepoWatch.Desktop.Storage;
using RepoWatch.GitHub.Api;
using RepoWatch.GitHub.Repositories;

namespace RepoWatch.Desktop.Tests.Monitoring;

/// <summary>Scheduling rules, pause/wake handling and the snapshot cache, end to end with a fake data source.</summary>
public sealed class SchedulingTests
{
    private static readonly AccountKey Account = PollingMonitorTests.Account;
    private static readonly PollingIntervals Defaults = new();

    [Theory]
    [InlineData(RefreshParts.Actions, false, false, 180)]
    [InlineData(RefreshParts.Actions, true, false, 20)]
    [InlineData(RefreshParts.Actions, false, true, 20)]
    [InlineData(RefreshParts.PullRequests, false, false, 90)]
    [InlineData(RefreshParts.PullRequests, false, true, 20)]
    [InlineData(RefreshParts.Issues, false, false, 120)]
    [InlineData(RefreshParts.Issues, false, true, 90)]
    [InlineData(RefreshParts.Metadata, true, true, 180)]
    public void Each_part_has_its_own_interval(RefreshParts part, bool active, bool focused, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), PollingPolicy.Interval(part, active, focused, Defaults));

    [Fact]
    public void Hidden_battery_and_a_low_budget_slow_polling_down_within_bounds()
    {
        Assert.Equal(1, PollingPolicy.Slowdown(widgetVisible: true, onBattery: false, budgetLow: false));
        Assert.Equal(3, PollingPolicy.Slowdown(widgetVisible: false, onBattery: false, budgetLow: false));
        Assert.Equal(2, PollingPolicy.Slowdown(widgetVisible: true, onBattery: true, budgetLow: false));
        Assert.Equal(8, PollingPolicy.Slowdown(widgetVisible: false, onBattery: true, budgetLow: true)); // capped
        Assert.Equal(TimeSpan.FromHours(1), PollingPolicy.Scale(TimeSpan.FromMinutes(30), 8)); // never beyond an hour
    }

    [Fact]
    public void Backoff_grows_exponentially_is_capped_and_jittered()
    {
        Assert.Equal(TimeSpan.FromSeconds(48), PollingPolicy.Backoff(1, Defaults, jitter: 0)); // 60 s × 0.8
        Assert.Equal(TimeSpan.FromSeconds(72), PollingPolicy.Backoff(1, Defaults, jitter: 1)); // 60 s × 1.2
        Assert.Equal(TimeSpan.FromSeconds(240), PollingPolicy.Backoff(3, Defaults, jitter: 0.5)); // 60 × 4
        Assert.Equal(TimeSpan.FromMinutes(15), PollingPolicy.Backoff(50, Defaults, jitter: 0.5)); // capped, no overflow
    }

    [Fact]
    public void Polling_conditions_detect_sleep_power_and_pause_changes()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var settings = TestServices.Settings();
        var battery = false;
        using var conditions = new PollingConditions(settings, time, () => battery, watchSystem: false);
        var resumed = 0;
        var changed = 0;
        conditions.Resumed += (_, _) => resumed++;
        conditions.Changed += (_, _) => changed++;

        time.Advance(TimeSpan.FromSeconds(30));
        conditions.Check();
        Assert.Equal(0, resumed); // a normal tick

        time.Advance(TimeSpan.FromMinutes(45)); // the computer slept
        conditions.Check();
        Assert.Equal(1, resumed);

        battery = true;
        conditions.Check();
        Assert.True(conditions.IsOnBattery);

        conditions.SetPaused(true);
        Assert.True(conditions.IsPaused);
        Assert.True(settings.App.MonitoringPaused);
        conditions.SetWidgetVisible(false);
        Assert.Equal(3, changed);
    }

    private static (PollingRepositoryMonitor Monitor, FakeDataSource Source, PollingConditions Conditions) Create(PollingIntervals intervals, params long[] ids) =>
        Create(new FakeDataSource(), intervals, ids);

    private static (PollingRepositoryMonitor Monitor, FakeDataSource Source, PollingConditions Conditions) Create(FakeDataSource source, PollingIntervals intervals, params long[] ids)
    {
        var conditions = new PollingConditions(TestServices.Settings(), TimeProvider.System, () => false, watchSystem: false);
        var settings = new AccountSettings { Watchlist = ids.Select(id => new WatchedRepository { RepositoryId = id, Owner = "octo", Name = $"repo{id}" }).ToList() };
        var monitor = new PollingRepositoryMonitor(Account, settings, source, "octo", TimeProvider.System, NullLogger.Instance, CancellationToken.None, intervals, conditions);
        return (monitor, source, conditions);
    }

    [Fact]
    public async Task Pausing_stops_all_requests_until_resumed()
    {
        var (monitor, source, conditions) = Create(PollingMonitorTests.Every(TimeSpan.FromMilliseconds(30)), 1);
        using var _ = monitor;
        await WaitUntil(() => source.Calls.Contains("repo 1"));

        conditions.SetPaused(true);
        await WaitUntil(() => monitor.State == ConnectionState.Paused);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        var count = source.Calls.Count;
        await Task.Delay(250, TestContext.Current.CancellationToken);
        Assert.Equal(count, source.Calls.Count);

        // A manual refresh while paused answers at once and requests nothing.
        await monitor.RefreshAsync(cancellationToken: TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        Assert.Equal(count, source.Calls.Count);

        conditions.SetPaused(false);
        await WaitUntil(() => source.Calls.Count > count);
        Assert.Equal(ConnectionState.Polling, monitor.State);
    }

    [Fact]
    public async Task Waking_or_reconnecting_retries_at_once_instead_of_waiting_out_the_backoff()
    {
        var offline = new FakeDataSource { Repository = _ => ApiResult<RepositoryInfo>.Fail(new ResourceError(ResourceErrorKind.Network, "offline", DateTimeOffset.UtcNow)) };
        var (monitor, source, conditions) = Create(offline, PollingMonitorTests.Every(TimeSpan.FromHours(1)), 1);
        using var _ = monitor;
        await WaitUntil(() => monitor.State == ConnectionState.Offline);
        var count = source.Calls.Count;

        source.Repository = id => ApiResult<RepositoryInfo>.Ok(FakeDataSource.Info(id));
        conditions.NotifyNetworkAvailable();

        await WaitUntil(() => monitor.State == ConnectionState.Polling);
        Assert.True(source.Calls.Count > count);
        Assert.True(monitor.Repositories.Single().Snapshot.Metadata.HasValue);
    }

    [Fact]
    public async Task The_repository_whose_details_are_open_is_refreshed_first()
    {
        var (monitor, source, _) = Create(PollingMonitorTests.Every(TimeSpan.FromHours(1)), 1, 2);
        using var _ = monitor;
        await monitor.RefreshAsync(cancellationToken: TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        source.Calls.Clear();

        monitor.SetFocus(new RepositoryKey(Account, 2));

        await WaitUntil(() => source.Calls.Contains("pulls repo2 mine=True login=octo"));
        Assert.Contains("actions repo2 main", source.Calls);
        Assert.DoesNotContain(source.Calls, c => c.Contains("repo1", StringComparison.Ordinal) || c == "repo 1" || c == "repo 2");
    }

    [Fact]
    public async Task Cached_snapshots_show_at_start_and_are_saved_after_refreshing()
    {
        var directory = Path.Combine(Path.GetTempPath(), "repowatch-sched-" + Guid.NewGuid().ToString("N"));
        try
        {
            var database = new LocalDatabase(Path.Combine(directory, "repowatch.db"));
            database.Initialize();
            var cache = new RepositoryCache(database, TimeProvider.System, NullLogger<RepositoryCache>.Instance);
            var key = new RepositoryKey(Account, 1);
            var earlier = DateTimeOffset.UtcNow.AddMinutes(-5);
            cache.Save(new RepositorySnapshot(key)
            {
                Metadata = Resource<RepositoryMetadata>.NotLoaded.Succeeded(FakeDataSource.Info(1).Metadata, earlier),
                Issues = Resource<Core.Issues.IssuesState>.NotLoaded.Succeeded(new Core.Issues.IssuesState { OpenCount = ItemCount.Exact(42) }, earlier),
            });

            var source = new FakeDataSource { ActionsGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
            source.Issues = _ => SectionResult<Core.Issues.IssuesState>.Ok(new Core.Issues.IssuesState { OpenCount = ItemCount.Exact(43) });
            var settings = new AccountSettings { Watchlist = [new WatchedRepository { RepositoryId = 1, Owner = "octo", Name = "repo1" }] };
            using (var monitor = new PollingRepositoryMonitor(Account, settings, source, "octo", TimeProvider.System, NullLogger.Instance, CancellationToken.None,
                PollingMonitorTests.Every(TimeSpan.FromHours(1)), cache: cache))
            {
                // Before GitHub answers, the last data is already there, labeled as cached (not a false empty state).
                var restored = monitor.Repositories.Single().Snapshot.Issues;
                Assert.Equal(ItemCount.Exact(42), restored.Value!.OpenCount);
                Assert.True(restored.IsFromCache);

                source.ActionsGate.SetResult();
                await WaitUntil(() => monitor.Repositories.Single().Snapshot.Issues.Value?.OpenCount == ItemCount.Exact(43));
                await WaitUntil(() => cache.Load(Account).TryGetValue(1, out var saved) && saved.Issues.Value?.OpenCount == ItemCount.Exact(43));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(condition());
    }
}
