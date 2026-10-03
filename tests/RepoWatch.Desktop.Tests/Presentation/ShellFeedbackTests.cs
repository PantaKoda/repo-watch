using System.ComponentModel;
using Microsoft.Extensions.Logging.Abstractions;
using RepoWatch.Core.Actions;
using RepoWatch.Core.Configuration;
using RepoWatch.Core.Identity;
using RepoWatch.Core.Monitoring;
using RepoWatch.Core.Platform;
using RepoWatch.Core.Settings;
using RepoWatch.Core.State;
using RepoWatch.Core.Status;
using RepoWatch.Desktop.Infrastructure;
using RepoWatch.Desktop.Presentation;
using RepoWatch.Desktop.Services;
using RepoWatch.Desktop.ViewModels;

namespace RepoWatch.Desktop.Tests.Presentation;

public sealed class ShellFeedbackTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
    private const string Sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>Records saved documents; the first save blocks until released.</summary>
    private sealed class GatedStore : ISettingsStore
    {
        private int _saves;

        public ManualResetEventSlim FirstSaveStarted { get; } = new();

        public ManualResetEventSlim ReleaseFirstSave { get; } = new();

        public AppSettings? LastSaved { get; private set; }

        public bool ThrowOnSave { get; set; }

        public SettingsLoadResult<AppSettings> LoadAppSettings() => SettingsCodecs.App.Deserialize(null);

        public bool SaveAppSettings(AppSettings settings)
        {
            if (ThrowOnSave)
            {
                throw new IOException("The data folder is not writable.");
            }

            if (Interlocked.Increment(ref _saves) == 1)
            {
                FirstSaveStarted.Set();
                ReleaseFirstSave.Wait(TimeSpan.FromSeconds(10));
            }

            LastSaved = settings;
            return true;
        }

        public SettingsLoadResult<AccountSettings> LoadAccountSettings(AccountKey account) => SettingsCodecs.Account.Deserialize(null);

        public bool SaveAccountSettings(AccountKey account, AccountSettings settings) => true;
    }

    private static MonitoredRepository Repo(DateTimeOffset loadedAt)
    {
        var key = new RepositoryKey(new AccountKey("github.com", 1), 7);
        var run = new WorkflowRun
        {
            Id = 1, WorkflowId = 1, WorkflowName = "CI", RunNumber = 1, RunAttempt = 1, HeadSha = Sha, HeadBranch = "main",
            Event = "push", Outcome = CheckOutcome.Success, HtmlUrl = new Uri("https://github.com/o/r/actions/runs/1"),
            CreatedAt = loadedAt, UpdatedAt = loadedAt,
        };
        var snapshot = new RepositorySnapshot(key)
        {
            Actions = Resource<ActionsState>.NotLoaded.Succeeded(
                new ActionsState { RecentRuns = [run], DefaultBranch = WorkflowRunSelection.ForCommit([run], Sha) }, loadedAt),
        };
        return new MonitoredRepository(new WatchedRepository { RepositoryId = 7, Owner = "o", Name = "r" }, snapshot, 0);
    }

    private static (WidgetViewModel Widget, FakeMonitor Monitor, RecordingBrowser Browser, SettingsService Settings) Widget(TimeProvider time)
    {
        var monitors = new MonitorHost(time);
        var monitor = new FakeMonitor();
        monitors.SetBase(monitor);
        var settings = TestServices.Settings();
        var browser = new RecordingBrowser();
        var widget = new WidgetViewModel(monitors, settings, new FakeShell(), browser, time, new ImmediateDispatcher(), new RepoWatch.Core.Configuration.RepoWatchOptions());
        return (widget, monitor, browser, settings);
    }

    [Fact]
    public void Data_that_stops_arriving_turns_stale_on_the_periodic_tick()
    {
        var time = new ManualTime(T0);
        var (widget, monitor, _, _) = Widget(time);
        monitor.Publish([Repo(T0)]);
        var row = widget.Repositories.Single();
        Assert.Equal(Freshness.Fresh, row.Actions.Freshness);
        Assert.Equal("Updated just now", row.Actions.StatusText);

        // No monitor events for 20 minutes: polling has silently stalled.
        time.Now = T0.AddMinutes(20);
        widget.Tick();

        Assert.Equal(Freshness.Stale, row.Actions.Freshness);
        Assert.Equal("Stale · updated 20m ago", row.Actions.StatusText);
        Assert.Equal("Some data is stale", row.FreshnessWarning);
        Assert.Equal("20m ago", row.Actions.Items.Single().TimeText);
    }

    [Fact]
    public void Placement_saves_do_not_rebuild_the_widget()
    {
        var (widget, monitor, _, settings) = Widget(TimeProvider.System);
        monitor.Publish([Repo(DateTimeOffset.UtcNow)]);
        var changes = new List<string?>();
        PropertyChangedEventHandler record = (_, e) => changes.Add(e.PropertyName);
        widget.PropertyChanged += record;
        widget.Repositories.Single().PropertyChanged += record;
        widget.Repositories.Single().Actions.PropertyChanged += record;

        settings.UpdateApp(s => s with
        {
            Window = s.Window with { Placements = [new WindowPlacement { DisplayKey = "d", X = 10, Y = 10, Width = 400, Height = 500 }] },
        });

        Assert.Empty(changes);

        settings.UpdateApp(s => s with { Window = s.Window with { AlwaysOnTop = true } });
        Assert.Contains(nameof(WidgetViewModel.AlwaysOnTop), changes);
    }

    [Fact]
    public async Task An_older_snapshot_is_never_saved_after_a_newer_one()
    {
        var store = new GatedStore();
        using var settings = new SettingsService(() => store, NullLogger<SettingsService>.Instance);
        settings.Load();

        var cancel = TestContext.Current.CancellationToken;
        settings.UpdateApp(s => s with { MonitoringPaused = true });                          // S1
        var timerFlush = Task.Run(settings.Flush, cancel);                                   // takes S1, blocks in the store
        Assert.True(store.FirstSaveStarted.Wait(TimeSpan.FromSeconds(5), cancel));

        settings.UpdateApp(s => s with { Window = s.Window with { AlwaysOnTop = true } });    // S2
        var quitFlush = Task.Run(settings.Flush, cancel);                                    // e.g. Quit
        await Task.Delay(150, cancel);
        store.ReleaseFirstSave.Set();
        await Task.WhenAll(timerFlush, quitFlush);

        Assert.True(store.LastSaved!.Window.AlwaysOnTop);
        Assert.True(store.LastSaved.MonitoringPaused);
    }

    [Fact]
    public void A_failed_save_is_shown_in_an_open_settings_window()
    {
        var store = new GatedStore { ThrowOnSave = true };
        using var settings = new SettingsService(() => store, NullLogger<SettingsService>.Instance);
        settings.Load();
        using var viewModel = SettingsViewModels.Create(settings, new MonitorHost(TimeProvider.System), new FakeShell());
        Assert.False(viewModel.HasStorageProblem);

        viewModel.AlwaysOnTop = true;
        settings.Flush();

        Assert.True(viewModel.HasStorageProblem);
        Assert.Contains("not writable", viewModel.StorageProblem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(LinkOpenResult.Refused, "isn't on GitHub")]
    [InlineData(LinkOpenResult.Failed, "Couldn't open your web browser")]
    public async Task Links_that_do_not_open_are_reported(LinkOpenResult result, string expected)
    {
        var (widget, monitor, browser, _) = Widget(TimeProvider.System);
        monitor.Publish([Repo(DateTimeOffset.UtcNow)]);
        browser.Result = result;

        await widget.Repositories.Single().Actions.Items.Single().OpenCommand.ExecuteAsync(null);

        Assert.True(widget.HasNotice);
        Assert.Contains(expected, widget.Notice, StringComparison.Ordinal);

        browser.Result = LinkOpenResult.Opened;
        await widget.Repositories.Single().Actions.Items.Single().OpenCommand.ExecuteAsync(null);
        Assert.False(widget.HasNotice);
    }

    [Fact]
    public async Task A_data_folder_that_cannot_be_opened_is_reported()
    {
        var shell = new FakeShell { DataFolderOpens = false };
        using var viewModel = SettingsViewModels.Create(TestServices.Settings(), new MonitorHost(TimeProvider.System), shell);

        await viewModel.OpenDataFolderCommand.ExecuteAsync(null);

        Assert.True(viewModel.HasActionMessage);
    }

    [Fact]
    public void Refresh_intervals_are_saved_in_seconds_and_reset_to_defaults()
    {
        var settings = TestServices.Settings();
        using var viewModel = SettingsViewModels.Create(settings, new MonitorHost(TimeProvider.System), new FakeShell());
        viewModel.RefreshSaveDelay = TimeSpan.Zero;
        Assert.Equal(90, viewModel.PullRequestsSeconds); // shows the default
        Assert.True(viewModel.RefreshIsDefault);

        viewModel.PullRequestsSeconds = 45;
        viewModel.IssuesSeconds = 3; // below the floor
        viewModel.QuietSeconds = 180; // equal to the default: stays "default"

        Assert.Equal(45, settings.App.Refresh.PullRequestsSeconds);
        Assert.Equal(RefreshIntervals.MinSeconds, settings.App.Refresh.IssuesSeconds);
        Assert.Null(settings.App.Refresh.QuietSeconds);
        Assert.False(viewModel.RefreshIsDefault);

        viewModel.ResetRefreshIntervalsCommand.Execute(null);

        Assert.True(settings.App.Refresh.IsDefault);
        Assert.Equal(90, viewModel.PullRequestsSeconds);
        Assert.Equal(120, viewModel.IssuesSeconds);
        Assert.True(viewModel.RefreshIsDefault);
    }

    [Fact]
    public async Task Typing_a_value_saves_only_the_final_number()
    {
        var settings = TestServices.Settings();
        var saves = new List<int?>();
        settings.AppChanged += (_, e) => saves.Add(e.Current.Refresh.PullRequestsSeconds);
        using var viewModel = SettingsViewModels.Create(settings, new MonitorHost(TimeProvider.System), new FakeShell());
        viewModel.RefreshSaveDelay = TimeSpan.FromMilliseconds(150);

        viewModel.PullRequestsSeconds = 3; // the box parses each keystroke: "3", "30", "300"
        viewModel.PullRequestsSeconds = 30;
        viewModel.PullRequestsSeconds = 300;
        Assert.Empty(saves); // nothing applied mid-typing (no burst of refreshes for "30")

        await Task.Delay(500, TestContext.Current.CancellationToken);

        Assert.Equal([300], saves);
        Assert.Equal(300, settings.App.Refresh.PullRequestsSeconds);
    }

    [Fact]
    public async Task An_emptied_box_keeps_the_saved_value_and_isnt_refilled_while_editing()
    {
        var settings = TestServices.Settings();
        settings.UpdateApp(s => s with { Refresh = new RefreshIntervals { PullRequestsSeconds = 45 } });
        using var viewModel = SettingsViewModels.Create(settings, new MonitorHost(TimeProvider.System), new FakeShell());
        viewModel.RefreshSaveDelay = TimeSpan.FromMilliseconds(100);

        viewModel.PullRequestsSeconds = null; // backspaced, about to type a new number
        await Task.Delay(300, TestContext.Current.CancellationToken);

        Assert.Null(viewModel.PullRequestsSeconds); // not snapped back to the default
        Assert.Equal(45, settings.App.Refresh.PullRequestsSeconds); // empty is "not decided yet"
    }

    [Fact]
    public void Closing_settings_right_after_typing_still_saves()
    {
        var settings = TestServices.Settings();
        var viewModel = SettingsViewModels.Create(settings, new MonitorHost(TimeProvider.System), new FakeShell());
        viewModel.RefreshSaveDelay = TimeSpan.FromMinutes(1);

        viewModel.IssuesSeconds = 300;
        viewModel.Dispose();

        Assert.Equal(300, settings.App.Refresh.IssuesSeconds);
    }

    [Fact]
    public void Running_workflows_slower_than_quiet_ones_gets_a_hint()
    {
        using var viewModel = SettingsViewModels.Create(TestServices.Settings(), new MonitorHost(TimeProvider.System), new FakeShell());
        viewModel.RefreshSaveDelay = TimeSpan.Zero;
        Assert.False(viewModel.HasRefreshHint);

        viewModel.RunningSeconds = 600; // quiet stays 180

        Assert.True(viewModel.HasRefreshHint);
        viewModel.RunningSeconds = 20;
        Assert.False(viewModel.HasRefreshHint);
    }

    [Theory]
    [InlineData(59.7, "59m ago")]
    [InlineData(60.1, "1h ago")]
    [InlineData(0.5, "just now")]
    [InlineData(1.2, "1m ago")]
    public void Relative_times_truncate(double minutes, string expected)
    {
        Assert.Equal(expected, TimeText.Ago(T0, T0.AddMinutes(minutes)));
    }
}
