using RepoWatch.Core.Actions;
using RepoWatch.Core.Identity;
using RepoWatch.Core.Issues;
using RepoWatch.Core.Monitoring;
using RepoWatch.Core.PullRequests;
using RepoWatch.Core.Settings;
using RepoWatch.Core.State;
using RepoWatch.Core.Status;
using RepoWatch.Desktop.Presentation;
using RepoWatch.Desktop.Services;
using RepoWatch.Desktop.ViewModels;

namespace RepoWatch.Desktop.Tests.Presentation;

public sealed class WidgetFilterTests
{
    private static readonly AccountKey Account = new("github.com", 1001);
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private const string Sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static MonitoredRepository Repo(long id, string name, int index, bool isPrivate = false, int openIssues = 0,
        CheckOutcome outcome = CheckOutcome.Success, DateTimeOffset? pushedAt = null, bool loaded = true,
        ItemCount? issues = null, ItemCount? pullRequests = null, bool fromCache = false, bool actionsFailing = false)
    {
        var key = new RepositoryKey(Account, id);
        var watch = new WatchedRepository { RepositoryId = id, Owner = "octo", Name = name };
        if (!loaded)
        {
            return new MonitoredRepository(watch, new RepositorySnapshot(key), index);
        }

        var run = new WorkflowRun
        {
            Id = id, WorkflowId = 1, WorkflowName = "CI", RunNumber = 1, RunAttempt = 1, HeadSha = Sha, HeadBranch = "main",
            Event = "push", Outcome = outcome, HtmlUrl = new Uri("https://github.com/o/r/actions/runs/1"),
            CreatedAt = Now.AddDays(-30), UpdatedAt = Now.AddDays(-30),
        };
        var snapshot = new RepositorySnapshot(key)
        {
            Metadata = Resource<RepositoryMetadata>.NotLoaded.Succeeded(new RepositoryMetadata
            {
                Key = key, Owner = "octo", Name = name, OwnerKind = RepositoryOwnerKind.User, IsPrivate = isPrivate, IsArchived = false,
                DefaultBranch = "main", HtmlUrl = new Uri($"https://github.com/octo/{name}"), PushedAt = pushedAt,
            }, Now),
            Actions = actionsFailing
                ? Resource<ActionsState>.NotLoaded.Failed(new ResourceError(ResourceErrorKind.Forbidden, "no access to Actions", Now))
                : Section(new ActionsState { RecentRuns = [run], DefaultBranch = WorkflowRunSelection.ForCommit([run], Sha) }, fromCache),
            PullRequests = Section(new PullRequestsState { OpenCount = pullRequests ?? ItemCount.Exact(0) }, fromCache),
            Issues = Section(new IssuesState { OpenCount = issues ?? ItemCount.Exact(openIssues) }, fromCache),
        };
        return new MonitoredRepository(watch, snapshot, index);
    }

    /// <summary>A section loaded live, or restored from the local cache at startup.</summary>
    private static Resource<T> Section<T>(T value, bool fromCache) where T : class =>
        fromCache ? Resource<T>.FromCache(value, Now.AddHours(-1)) : Resource<T>.NotLoaded.Succeeded(value, Now);

    private static (WidgetViewModel Widget, FakeMonitor Monitor, SettingsService Settings) Create(WatchlistService? watchlist = null, SettingsService? settings = null)
    {
        var monitors = new MonitorHost(TimeProvider.System);
        var monitor = new FakeMonitor();
        monitors.SetBase(monitor);
        settings ??= TestServices.Settings();
        var widget = new WidgetViewModel(monitors, settings, new FakeShell(), new RecordingBrowser(), TimeProvider.System, new ImmediateDispatcher(),
            new RepoWatch.Core.Configuration.RepoWatchOptions(), watchlist);
        return (widget, monitor, settings);
    }

    private static List<string> Names(WidgetViewModel widget) => widget.Repositories.Select(r => r.Name).ToList();

    [Fact]
    public void The_name_filter_narrows_the_list_and_Escape_clears_it_first()
    {
        var (widget, monitor, _) = Create();
        monitor.Publish([Repo(1, "repo-watch", 0), Repo(2, "dotfiles", 1), Repo(3, "Watchtower", 2)]);

        widget.SearchText = "WATCH";

        Assert.Equal(["octo/repo-watch", "octo/Watchtower"], Names(widget));
        Assert.Equal("2 of 3 shown", widget.SummaryText);

        widget.CollapseCommand.Execute(null);

        Assert.Equal("", widget.SearchText);
        Assert.Equal(3, widget.Repositories.Count);
    }

    [Fact]
    public void Hiding_idle_repositories_keeps_those_with_something_to_look_at_and_is_remembered()
    {
        var (widget, monitor, settings) = Create();
        monitor.Publish([
            Repo(1, "quiet", 0),
            Repo(2, "has-issues", 1, openIssues: 3),
            Repo(3, "failing", 2, outcome: CheckOutcome.Failure),
            Repo(4, "loading", 3, loaded: false),
        ]);

        widget.HideIdle = true;

        Assert.Equal(["octo/failing", "octo/has-issues", "octo/loading"], Names(widget)); // not-yet-loaded is never hidden
        Assert.Equal(1, widget.HiddenCount);
        Assert.True(settings.App.Window.HideIdleRepositories);

        // A new widget (e.g. after restart) starts with the remembered choice.
        var (again, otherMonitor, _) = Create(settings: settings);
        otherMonitor.Publish([Repo(1, "quiet", 0)]);
        Assert.True(again.HideIdle);
        Assert.True(again.ShowNoMatches);
    }

    [Fact]
    public void A_row_that_becomes_idle_stays_until_the_interaction_ends()
    {
        var (widget, monitor, _) = Create();
        monitor.Publish([Repo(1, "busy", 0, openIssues: 2), Repo(2, "other", 1, openIssues: 1)]);
        widget.HideIdle = true;
        var focused = widget.Repositories[0];
        widget.SelectedRepository = focused;

        widget.IsInteracting = true;
        monitor.Publish([Repo(1, "busy", 0), Repo(2, "other", 1, openIssues: 1)]); // the last issue was closed

        Assert.Same(focused, widget.Repositories[0]);
        Assert.Same(focused, widget.SelectedRepository);
        Assert.True(widget.HasPendingReorder);

        widget.IsInteracting = false;

        Assert.Equal(["octo/other"], Names(widget));
    }

    [Fact]
    public void Filter_changes_made_by_the_user_apply_at_once_even_during_interaction()
    {
        var (widget, monitor, _) = Create();
        monitor.Publish([Repo(1, "quiet", 0), Repo(2, "busy", 1, openIssues: 1)]);
        widget.IsInteracting = true;

        widget.HideIdle = true;

        Assert.Equal(["octo/busy"], Names(widget));
    }

    [Fact]
    public void The_footer_counts_failures_hidden_by_the_name_filter()
    {
        var (widget, monitor, _) = Create();
        monitor.Publish([Repo(1, "broken", 0, outcome: CheckOutcome.Failure), Repo(2, "fine", 1)]);

        widget.SearchText = "fine";

        Assert.Equal("1 of 2 shown · 1 failing", widget.SummaryText);
    }

    [Fact]
    public void Repositories_that_never_get_metadata_do_not_keep_the_header_animating()
    {
        var (widget, monitor, _) = Create();
        var key = new RepositoryKey(Account, 9);
        var failed = new RepositorySnapshot(key) with
        {
            Metadata = Resource<RepositoryMetadata>.NotLoaded.Failed(new ResourceError(ResourceErrorKind.Forbidden, "SSO required", Now)),
        };
        var repository = new MonitoredRepository(new WatchedRepository { RepositoryId = 9, Owner = "octo", Name = "sso" }, failed, 0);

        monitor.IsRefreshing = true;
        monitor.Publish([repository]);

        Assert.False(widget.ShowActivity); // the first attempt already has an outcome: later polls are background work
    }

    [Fact]
    public void When_every_repository_is_filtered_out_Show_all_brings_them_back()
    {
        var (widget, monitor, _) = Create();
        monitor.Publish([Repo(1, "quiet", 0), Repo(2, "calm", 1)]);
        widget.HideIdle = true;

        Assert.True(widget.ShowNoMatches);
        Assert.True(widget.HasRepositories); // still watched: no "add repositories" state
        Assert.False(widget.ShowEmptyWatchlist);

        widget.ClearFiltersCommand.Execute(null);

        Assert.False(widget.ShowNoMatches);
        Assert.Equal(2, widget.Repositories.Count);
    }

    [Fact]
    public void Recent_activity_sort_puts_the_most_recently_active_first()
    {
        var (widget, monitor, _) = Create();
        monitor.Ordering = RepositoryOrdering.RecentActivity;
        monitor.Publish([
            Repo(1, "old", 0, pushedAt: Now.AddDays(-20)),
            Repo(2, "today", 1, pushedAt: Now.AddHours(-2)),
            Repo(3, "last-week", 2, pushedAt: Now.AddDays(-6)),
        ]);

        Assert.Equal(["octo/today", "octo/last-week", "octo/old"], Names(widget));
        Assert.Equal(RepositoryOrdering.RecentActivity, widget.SelectedSort?.Ordering);
        Assert.Equal("2h ago", widget.Repositories[0].ActivityText);
    }

    [Fact]
    public async Task Choosing_a_sort_in_the_widget_saves_it_for_the_account()
    {
        var kit = new AccountKit().Start();
        await kit.SignInAsync(AccountKit.TokenJson(), AccountKit.UserJson());
        var (widget, monitor, _) = Create(kit.Watchlist, kit.Settings);
        monitor.Publish([Repo(1, "a", 0)]);

        Assert.True(widget.CanChangeSort);
        widget.SelectedSort = widget.SortOptions.Single(o => o.Ordering == RepositoryOrdering.RecentActivity);

        Assert.Equal(RepositoryOrdering.RecentActivity, kit.Watchlist.Current.Ordering);
    }

    [Fact]
    public void In_demo_mode_the_sort_menu_reorders_for_the_session()
    {
        var (widget, monitor, _) = Create();
        monitor.State = ConnectionState.Demo;
        monitor.Publish([
            Repo(1, "old", 0, pushedAt: Now.AddDays(-20)),
            Repo(2, "today", 1, pushedAt: Now.AddHours(-2)),
        ]);
        Assert.True(widget.CanChangeSort);

        widget.SelectedSort = widget.SortOptions.Single(o => o.Ordering == RepositoryOrdering.RecentActivity);

        Assert.Equal(["octo/today", "octo/old"], Names(widget));
        Assert.Equal(RepositoryOrdering.RecentActivity, widget.SelectedSort?.Ordering); // not reset by the next sync
    }

    [Fact]
    public void Rows_show_whether_a_repository_is_private_or_public()
    {
        var (widget, monitor, _) = Create();
        monitor.Publish([Repo(1, "secret", 0, isPrivate: true), Repo(2, "open", 1), Repo(3, "unknown", 2, loaded: false)]);

        var rows = widget.Repositories.ToDictionary(r => r.Name);
        Assert.Equal("Private", rows["octo/secret"].Visibility);
        Assert.True(rows["octo/secret"].IsPrivate);
        Assert.Equal("Public", rows["octo/open"].Visibility);
        Assert.False(rows["octo/unknown"].HasVisibility); // not guessed before metadata loads
    }

    [Fact]
    public void Background_polling_does_not_animate_the_header()
    {
        var (widget, monitor, _) = Create();
        monitor.Publish([Repo(1, "a", 0)]);

        monitor.IsRefreshing = true;
        monitor.Publish([Repo(1, "a", 0)]);

        Assert.True(widget.IsRefreshing);
        Assert.False(widget.ShowActivity);
    }

    [Fact]
    public async Task The_first_load_and_a_manual_refresh_animate_the_header_until_done()
    {
        var (widget, monitor, _) = Create();
        monitor.IsRefreshing = true;
        monitor.Publish([Repo(1, "a", 0, loaded: false)]);
        Assert.True(widget.ShowActivity); // first load: nothing to show yet

        monitor.IsRefreshing = false;
        monitor.Publish([Repo(1, "a", 0)]);
        Assert.False(widget.ShowActivity);

        monitor.HoldRefresh = new TaskCompletionSource();
        var refresh = widget.RefreshCommand.ExecuteAsync(null);
        Assert.True(widget.ShowActivity);

        monitor.HoldRefresh.SetResult();
        await refresh;
        Assert.False(widget.ShowActivity);
    }

    [Fact]
    public void Rows_show_chips_only_for_what_is_there()
    {
        var (widget, monitor, _) = Create();
        monitor.Publish([
            Repo(1, "quiet", 0),
            Repo(2, "busy", 1, openIssues: 1, outcome: CheckOutcome.Running),
            Repo(3, "broken", 2, openIssues: 31, outcome: CheckOutcome.Failure),
        ]);
        var rows = widget.Repositories.ToDictionary(r => r.Name);

        Assert.False(rows["octo/quiet"].HasChips); // a quiet repository stays calm
        Assert.Equal("1 running", rows["octo/busy"].RunningText);
        Assert.Equal("1 issue", rows["octo/busy"].IssuesText);
        Assert.True(rows["octo/broken"].IsFailing);
        Assert.Equal("31 issues", rows["octo/broken"].IssuesText);
        Assert.False(rows["octo/broken"].HasPullRequests); // 0 open PRs: no chip
    }

    [Fact]
    public void The_first_load_sets_a_silent_baseline_and_later_changes_are_marked()
    {
        var (widget, monitor, _) = Create();
        monitor.Publish([Repo(1, "repo", 0, loaded: false)]);
        monitor.Publish([Repo(1, "repo", 0, openIssues: 2)]); // first complete load: baseline
        var row = widget.Repositories.Single();
        Assert.False(row.HasUnseenActivity);
        Assert.False(row.IsFlashing);

        monitor.Publish([Repo(1, "repo", 0, openIssues: 2)]); // nothing changed
        Assert.False(row.HasUnseenActivity);

        monitor.Publish([Repo(1, "repo", 0, openIssues: 3)]); // a new issue
        Assert.True(row.HasUnseenActivity);
        Assert.True(row.IsFlashing);
    }

    [Fact]
    public void A_workflow_starting_or_failing_marks_the_row()
    {
        var (widget, monitor, _) = Create();
        monitor.Publish([Repo(1, "repo", 0)]);
        var row = widget.Repositories.Single();

        monitor.Publish([Repo(1, "repo", 0, outcome: CheckOutcome.Running)]);
        Assert.True(row.HasUnseenActivity);

        widget.ShowRepositoryCommand.Execute(row); // opened: seen
        Assert.False(row.HasUnseenActivity);
        widget.BackCommand.Execute(null);

        monitor.Publish([Repo(1, "repo", 0, outcome: CheckOutcome.Failure)]);
        Assert.True(row.HasUnseenActivity);
    }

    [Fact]
    public void A_change_while_its_details_are_open_is_shown_but_not_left_unseen()
    {
        var (widget, monitor, _) = Create();
        monitor.Publish([Repo(1, "repo", 0)]);
        var row = widget.Repositories.Single();
        widget.ShowRepositoryCommand.Execute(row);

        monitor.Publish([Repo(1, "repo", 0, openIssues: 1)]);

        Assert.True(row.IsFlashing);
        Assert.False(row.HasUnseenActivity); // the user is looking at it
    }

    [Fact]
    public void A_hidden_idle_repository_that_gets_activity_comes_back_marked()
    {
        var (widget, monitor, _) = Create();
        widget.HideIdle = true;
        monitor.Publish([Repo(1, "quiet", 0), Repo(2, "busy", 1, openIssues: 1)]);
        Assert.Equal(["octo/busy"], Names(widget));

        monitor.Publish([Repo(1, "quiet", 0, openIssues: 1), Repo(2, "busy", 1, openIssues: 1)]);

        var quiet = widget.Repositories.Single(r => r.Name == "octo/quiet");
        Assert.True(quiet.HasUnseenActivity);
        Assert.True(quiet.IsFlashing);
    }

    [Fact]
    public void A_name_filter_doesnt_erase_the_marker_of_rows_it_hides()
    {
        var (widget, monitor, _) = Create();
        monitor.Publish([Repo(1, "alpha", 0), Repo(2, "beta", 1)]);
        monitor.Publish([Repo(1, "alpha", 0, openIssues: 1), Repo(2, "beta", 1)]);

        widget.SearchText = "beta"; // alpha's row goes away
        widget.SearchText = "";      // and comes back as a new row

        Assert.True(widget.Repositories.Single(r => r.Name == "octo/alpha").HasUnseenActivity);
    }

    [Fact]
    public void Cached_data_at_startup_is_not_the_baseline()
    {
        var (widget, monitor, _) = Create();
        monitor.Publish([Repo(1, "repo", 0, openIssues: 5, fromCache: true)]); // restored from last session
        monitor.Publish([Repo(1, "repo", 0, openIssues: 7)]);                  // first live refresh

        var row = widget.Repositories.Single();
        Assert.False(row.HasUnseenActivity); // starting the app never lights everything up
        Assert.False(row.IsFlashing);

        monitor.Publish([Repo(1, "repo", 0, openIssues: 8)]);
        Assert.True(row.HasUnseenActivity);
    }

    [Fact]
    public void A_section_that_keeps_failing_doesnt_stop_other_changes_from_being_noticed()
    {
        var (widget, monitor, _) = Create();
        monitor.Publish([Repo(1, "repo", 0, actionsFailing: true)]);
        monitor.Publish([Repo(1, "repo", 0, actionsFailing: true, pullRequests: ItemCount.Exact(1))]);

        Assert.True(widget.Repositories.Single().HasUnseenActivity);
    }

    [Fact]
    public void Chips_handle_inexact_counts_and_pull_requests()
    {
        var (widget, monitor, _) = Create();
        monitor.Publish([
            Repo(1, "many", 0, issues: ItemCount.AtLeast(30), pullRequests: ItemCount.Exact(3)),
            Repo(2, "one", 1, issues: ItemCount.AtLeast(1), pullRequests: ItemCount.Exact(1)),
        ]);
        var rows = widget.Repositories.ToDictionary(r => r.Name);

        Assert.Equal("30+ issues", rows["octo/many"].IssuesText);
        Assert.Equal("3 open PRs", rows["octo/many"].PullRequestsText);
        Assert.Equal("1+ issues", rows["octo/one"].IssuesText); // "at least one" is plural
        Assert.Equal("1 open PR", rows["octo/one"].PullRequestsText);
    }

    [Fact]
    public void Screen_readers_hear_running_work_and_the_change_marker()
    {
        var (widget, monitor, _) = Create();
        monitor.Publish([Repo(1, "repo", 0, outcome: CheckOutcome.Running)]);
        var row = widget.Repositories.Single();
        Assert.Contains("1 running", row.Summary, StringComparison.Ordinal);
        Assert.Equal(StatusTone.Running, row.RunningTone);

        monitor.Publish([Repo(1, "repo", 0, outcome: CheckOutcome.Success)]);
        Assert.EndsWith("Changed since you last opened it", row.Summary, StringComparison.Ordinal);
        Assert.Equal(StatusTone.None, row.RunningTone); // nothing running: the chip's dot doesn't pulse
    }

    [Fact]
    public void The_glow_ends_after_a_few_seconds_but_the_dot_stays_until_opened()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);
        var monitors = new MonitorHost(time);
        var monitor = new FakeMonitor();
        monitors.SetBase(monitor);
        using var widget = new WidgetViewModel(monitors, TestServices.Settings(), new FakeShell(), new RecordingBrowser(), time, new ImmediateDispatcher(),
            new RepoWatch.Core.Configuration.RepoWatchOptions());
        monitor.Publish([Repo(1, "repo", 0)]);
        monitor.Publish([Repo(1, "repo", 0, openIssues: 1)]);
        var row = widget.Repositories.Single();
        Assert.True(row.IsFlashing);

        time.Advance(RepositoryRowViewModel.FlashDuration + TimeSpan.FromMilliseconds(100));

        Assert.False(row.IsFlashing);
        Assert.True(row.HasUnseenActivity);
    }
}
