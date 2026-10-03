using System.Collections.ObjectModel;
using RepoWatch.Core.Actions;
using RepoWatch.Core.Identity;
using RepoWatch.Core.Monitoring;
using RepoWatch.Core.Settings;
using RepoWatch.Core.State;
using RepoWatch.Core.Status;
using RepoWatch.Desktop.Presentation;
using RepoWatch.Desktop.Services;
using RepoWatch.Desktop.ViewModels;

namespace RepoWatch.Desktop.Tests.Presentation;

public sealed class WidgetBehaviorTests
{
    private static readonly AccountKey Account = new("github.com", 1001);
    private const string Sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private sealed record Row(string Key, string Value);

    private sealed class Item(string key)
    {
        public string Key { get; } = key;

        public string Value { get; set; } = "";
    }

    private static bool Reconcile(ObservableCollection<Item> target, IReadOnlyList<Row> rows, bool allowReorder) =>
        CollectionReconciler.Reconcile(target, rows, r => r.Key, i => i.Key, r => new Item(r.Key) { Value = r.Value }, (i, r) => i.Value = r.Value, allowReorder);

    [Fact]
    public void Reconcile_updates_items_in_place_and_preserves_instances()
    {
        var target = new ObservableCollection<Item>();
        Reconcile(target, [new("a", "1"), new("b", "1")], allowReorder: true);
        var a = target[0];

        Reconcile(target, [new("a", "2"), new("b", "2")], allowReorder: true);

        Assert.Same(a, target[0]);
        Assert.Equal("2", a.Value);
    }

    [Fact]
    public void While_interacting_rows_keep_positions_and_new_rows_append()
    {
        var target = new ObservableCollection<Item>();
        Reconcile(target, [new("a", ""), new("b", ""), new("c", "")], allowReorder: true);

        var inOrder = Reconcile(target, [new("c", "new"), new("d", ""), new("a", ""), new("b", "")], allowReorder: false);

        Assert.False(inOrder);
        Assert.Equal(["a", "b", "c", "d"], target.Select(i => i.Key));
        Assert.Equal("new", target[2].Value);
    }

    [Fact]
    public void After_interaction_the_desired_order_is_applied_with_moves()
    {
        var target = new ObservableCollection<Item>();
        Reconcile(target, [new("a", ""), new("b", ""), new("c", "")], allowReorder: true);
        var c = target[2];
        var resets = 0;
        target.CollectionChanged += (_, e) => resets += e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset ? 1 : 0;

        var inOrder = Reconcile(target, [new("c", ""), new("a", "")], allowReorder: true);

        Assert.True(inOrder);
        Assert.Equal(["c", "a"], target.Select(i => i.Key));
        Assert.Same(c, target[0]);
        Assert.Equal(0, resets);
    }

    private static MonitoredRepository Repo(long id, int index, CheckOutcome defaultBranch)
    {
        var key = new RepositoryKey(Account, id);
        var run = new WorkflowRun
        {
            Id = id, WorkflowId = 1, WorkflowName = "CI", RunNumber = 1, RunAttempt = 1, HeadSha = Sha, HeadBranch = "main",
            Event = "push", Outcome = defaultBranch, HtmlUrl = new Uri("https://github.com/o/r/actions/runs/1"),
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        var snapshot = new RepositorySnapshot(key)
        {
            Actions = Resource<ActionsState>.NotLoaded.Succeeded(
                new ActionsState { RecentRuns = [run], DefaultBranch = WorkflowRunSelection.ForCommit([run], Sha) }, DateTimeOffset.UtcNow),
        };
        return new MonitoredRepository(new WatchedRepository { RepositoryId = id, Owner = "o", Name = $"repo{id}" }, snapshot, index);
    }

    private static (WidgetViewModel Widget, FakeMonitor Monitor, FakeShell Shell) CreateWidget()
    {
        var monitors = new MonitorHost(TimeProvider.System);
        var monitor = new FakeMonitor();
        monitors.SetBase(monitor);
        var shell = new FakeShell();
        var widget = new WidgetViewModel(monitors, TestServices.Settings(), shell, new RecordingBrowser(), TimeProvider.System, new ImmediateDispatcher(), new RepoWatch.Core.Configuration.RepoWatchOptions());
        return (widget, monitor, shell);
    }

    [Fact]
    public void A_status_change_does_not_reorder_rows_while_the_user_interacts()
    {
        var (widget, monitor, _) = CreateWidget();
        monitor.Publish([Repo(1, 0, CheckOutcome.Success), Repo(2, 1, CheckOutcome.Success)]);
        var focused = widget.Repositories[1];
        widget.SelectedRepository = focused;

        widget.IsInteracting = true;
        monitor.Publish([Repo(1, 0, CheckOutcome.Success), Repo(2, 1, CheckOutcome.Failure)]);

        Assert.Equal([1L, 2L], widget.Repositories.Select(r => r.Key.RepositoryId));
        Assert.Equal(AttentionLevel.Failure, focused.Attention);
        Assert.True(widget.HasPendingReorder);
        Assert.Same(focused, widget.SelectedRepository);

        widget.IsInteracting = false;

        Assert.Equal([2L, 1L], widget.Repositories.Select(r => r.Key.RepositoryId));
        Assert.Same(focused, widget.Repositories[0]);
        Assert.Same(focused, widget.SelectedRepository);
    }

    [Fact]
    public void Empty_states_distinguish_signed_out_from_an_empty_watchlist()
    {
        var (widget, monitor, shell) = CreateWidget();
        monitor.Publish([]);

        Assert.True(widget.ShowEmptyWatchlist);
        Assert.False(widget.ShowSignedOutState);

        monitor.State = ConnectionState.NotSignedIn;
        monitor.Publish([]);

        Assert.True(widget.ShowSignedOutState);
        Assert.False(widget.ShowEmptyWatchlist);

        widget.OpenSettingsCommand.Execute(null);
        Assert.Equal(1, shell.SettingsRequests);
    }

    [Fact]
    public void Expanded_state_and_details_navigation()
    {
        var (widget, monitor, _) = CreateWidget();
        monitor.Publish([Repo(1, 0, CheckOutcome.Success)]);
        Assert.False(widget.IsExpanded);

        widget.ShowRepositoryCommand.Execute(widget.Repositories[0]);
        Assert.True(widget.IsExpanded);
        Assert.True(widget.ShowDetails);
        Assert.False(widget.ShowList);

        widget.CollapseCommand.Execute(null); // Escape: details -> list
        Assert.False(widget.ShowDetails);
        Assert.True(widget.IsExpanded);

        widget.CollapseCommand.Execute(null); // Escape again: collapse
        Assert.False(widget.IsExpanded);
    }

    [Fact]
    public void Demo_mode_is_explicit_and_labeled()
    {
        var monitors = new MonitorHost(TimeProvider.System);
        var widget = new WidgetViewModel(monitors, TestServices.Settings(), new FakeShell(), new RecordingBrowser(), TimeProvider.System, new ImmediateDispatcher(), new RepoWatch.Core.Configuration.RepoWatchOptions());

        Assert.False(widget.IsDemo);
        Assert.True(widget.ShowSignedOutState);
        Assert.Empty(widget.Repositories);

        widget.EnterDemoCommand.Execute(null);

        Assert.True(widget.IsDemo);
        Assert.Equal("Demo data", widget.ConnectionLabel);
        Assert.NotEmpty(widget.Repositories);

        widget.ExitDemoCommand.Execute(null);

        Assert.False(widget.IsDemo);
        Assert.Empty(widget.Repositories);
        monitors.Dispose();
    }

    [Fact]
    public void Hide_is_routed_to_the_shell()
    {
        var (widget, _, shell) = CreateWidget();
        shell.CanHideToTray = false;

        widget.HideCommand.Execute(null);

        Assert.Equal(1, shell.HideRequests);
        Assert.Equal("Minimize", widget.HideTooltip);
    }
}
