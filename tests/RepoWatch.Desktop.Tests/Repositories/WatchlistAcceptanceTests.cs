using RepoWatch.Core.Access;
using RepoWatch.Core.Identity;
using RepoWatch.Core.Monitoring;
using RepoWatch.Core.Settings;
using RepoWatch.Desktop.Services;
using RepoWatch.Desktop.ViewModels;
using static RepoWatch.Desktop.Tests.Repositories.CatalogFixtures;

namespace RepoWatch.Desktop.Tests.Repositories;

/// <summary>Stage 05 acceptance: selection persists, only watched repositories are monitored, multi-page/private/org lists work.</summary>
public sealed class WatchlistAcceptanceTests
{
    private static RepositoryPickerViewModel Picker(AccountKit kit) => new(kit.Catalog, kit.Watchlist, new RecordingBrowser(), kit.Time);

    [Fact]
    public async Task Catalog_combines_private_and_organization_repositories()
    {
        var kit = await SignedInWithCatalogAsync();

        Assert.Equal(CatalogStatus.Loaded, kit.Catalog.Status);
        Assert.True(kit.Catalog.Catalog!.IsComplete);
        Assert.Equal(["acme-org/api", "acme-org/legacy", "acme-org/web", "octo-test/blog", "octo-test/dotfiles"],
            kit.Catalog.Catalog.Repositories.Select(r => r.FullName));

        using var picker = Picker(kit);
        var api = picker.Shown.Single(c => c.FullName == "acme-org/api");
        Assert.Equal("private · organization", api.Badges);
        Assert.Equal("organization · archived", picker.Shown.Single(c => c.FullName == "acme-org/legacy").Badges);
        Assert.Equal("0 of 5 repositories selected for the widget", picker.SelectedCountText);
    }

    [Fact]
    public async Task Granted_access_does_not_add_repositories_to_the_widget()
    {
        var kit = await SignedInWithCatalogAsync();

        Assert.Empty(kit.Watchlist.Repositories);
        Assert.Empty(kit.Monitors.Current.Repositories);
    }

    [Fact]
    public async Task Selected_repositories_survive_a_restart_in_order_and_per_account()
    {
        var kit = await SignedInWithCatalogAsync();
        using (var picker = Picker(kit))
        {
            picker.Shown.Single(c => c.FullName == "octo-test/dotfiles").IsSelected = true;
            picker.Shown.Single(c => c.FullName == "acme-org/api").IsSelected = true;
        }

        kit.Watchlist.SetOrdering(RepositoryOrdering.Manual);

        kit.Start(); // restart over the same stores
        kit.Http.Json(AccountKit.UserJson());
        await kit.Accounts.RestoreAsync(TestContext.Current.CancellationToken);

        Assert.Equal([11L, 21L], kit.Watchlist.Repositories.Select(w => w.RepositoryId));
        Assert.Equal(RepositoryOrdering.Manual, kit.Monitors.Current.Ordering);
        Assert.Equal(["octo-test/dotfiles", "acme-org/api"], kit.Monitors.Current.Repositories.Select(r => r.DisplayName));

        // Another account on the same machine has its own (empty) watchlist.
        Assert.Empty(kit.Settings.GetAccount(new AccountKey("github.com", 9999)).Watchlist);
    }

    [Fact]
    public async Task Only_watched_repositories_reach_the_monitor_and_removal_is_immediate()
    {
        var kit = await SignedInWithCatalogAsync();
        kit.Watchlist.Add(kit.Catalog.Catalog!.Repositories.Where(r => r.Owner == "octo-test").ToList());
        var before = kit.Monitors.Current;

        Assert.Equal([11L, 12L], kit.Monitors.Current.Repositories.Select(r => r.Key.RepositoryId).Order());
        Assert.Equal(ConnectionState.SignedInIdle, kit.Monitors.Current.State);

        kit.Watchlist.Remove([11]);

        Assert.NotSame(before, kit.Monitors.Current); // the monitor holding the removed repository was replaced
        Assert.Equal([12L], kit.Monitors.Current.Repositories.Select(r => r.Key.RepositoryId));
    }

    [Fact]
    public async Task Picker_filters_and_bulk_actions_say_what_they_apply_to()
    {
        var kit = await SignedInWithCatalogAsync();
        using var picker = Picker(kit);

        Assert.Equal("Bulk actions apply to all 5 repositories in the list.", picker.BulkScopeText);
        Assert.Equal("Add all (5)", picker.AddShownLabel);

        picker.OwnerFilter = "octo-test";
        Assert.Equal(["octo-test/blog", "octo-test/dotfiles"], picker.Shown.Select(c => c.FullName));
        Assert.Equal("Bulk actions apply only to the 2 repositories shown by the current search and filters.", picker.BulkScopeText);
        Assert.Equal("Add 2 shown", picker.AddShownLabel);

        picker.AddShownCommand.Execute(null);
        Assert.Equal([12L, 11L], kit.Watchlist.Repositories.Select(w => w.RepositoryId));

        picker.OwnerFilter = "acme-org";
        picker.SearchText = "legacy";
        Assert.Equal(["acme-org/legacy"], picker.Shown.Select(c => c.FullName));
        Assert.Equal("Bulk actions apply only to the 1 repository shown by the current search and filters.", picker.BulkScopeText);

        picker.ClearFiltersCommand.Execute(null);
        picker.ShowSelectedOnly = true;
        Assert.Equal(["octo-test/blog", "octo-test/dotfiles"], picker.Shown.Select(c => c.FullName));
        Assert.Equal("2 of 5 repositories selected for the widget", picker.SelectedCountText);

        picker.RemoveShownCommand.Execute(null);
        Assert.Empty(kit.Watchlist.Repositories);
    }

    [Fact]
    public async Task No_installation_offers_grant_access_and_no_repositories_offers_manage_access()
    {
        var kit = new AccountKit().Start();
        await kit.SignInAsync(AccountKit.TokenJson(), AccountKit.UserJson());
        kit.Http.Json(InstallationsJson());
        await kit.Catalog.RefreshAsync();
        using var picker = Picker(kit);
        Assert.True(picker.ShowGrantAccess);
        Assert.False(picker.ShowEmpty);

        kit.Http.Json(InstallationsJson(Installation(1, "octo-test"))).Json(RepositoriesJson());
        await kit.Catalog.RefreshAsync();
        Assert.False(picker.ShowGrantAccess);
        Assert.True(picker.ShowEmpty);
        Assert.Contains("Manage access", picker.EmptyText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Watchlist_editor_orders_removes_and_explains_access()
    {
        var kit = await SignedInWithCatalogAsync();
        kit.Watchlist.Add(kit.Catalog.Catalog!.Repositories.Where(r => r.Id is 11 or 12).ToList());
        // A repository GitHub no longer grants (e.g. access removed on GitHub).
        kit.Settings.UpdateAccount(AccountKit.Octo, a => a with { Watchlist = [.. a.Watchlist, new WatchedRepository { RepositoryId = 99, Owner = "octo-test", Name = "gone" }] });
        kit.Watchlist.Move(99, 0); // reload from settings
        using var editor = new WatchlistViewModel(kit.Watchlist, kit.Catalog, new RecordingBrowser());

        Assert.Equal(["octo-test/blog", "octo-test/dotfiles", "octo-test/gone"], editor.Items.Select(i => i.Name));
        Assert.Equal("Access granted.", editor.Items[0].AccessText);
        Assert.StartsWith("GitHub hasn't granted Repo Watch access", editor.Items[2].AccessText, StringComparison.Ordinal);
        Assert.True(editor.Items[2].CanManageAccess);
        Assert.True(editor.Items[0].IsFirst);

        editor.Items[1].MoveUpCommand.Execute(null);
        Assert.Equal(["octo-test/dotfiles", "octo-test/blog", "octo-test/gone"], editor.Items.Select(i => i.Name));

        editor.Items[2].RemoveCommand.Execute(null);
        Assert.Equal(2, editor.Items.Count);

        editor.ManualOrder = true;
        Assert.Equal(RepositoryOrdering.Manual, kit.Watchlist.Current.Ordering);

        editor.Items[0].PullRequests = PullRequestScope.All;
        editor.Items[0].Branches = "main, release , main";
        editor.Items[0].SaveBranchesCommand.Execute(null);
        var saved = kit.Watchlist.Repositories[0];
        Assert.Equal(PullRequestScope.All, saved.PullRequests);
        Assert.Equal(["main", "release"], saved.Branches);
    }

    [Fact]
    public async Task Renamed_repositories_keep_their_place_after_a_refresh()
    {
        var kit = await SignedInWithCatalogAsync();
        kit.Watchlist.Add(kit.Catalog.Catalog!.Repositories.Where(r => r.Id == 21).ToList());

        kit.Http.Json(InstallationsJson(Installation(2, "acme-renamed", "Organization")))
            .Json(RepositoriesJson(Repository(21, "acme-renamed", "api-v2", isPrivate: true, ownerType: "Organization")));
        await kit.Catalog.RefreshAsync();

        var entry = Assert.Single(kit.Watchlist.Repositories);
        Assert.Equal((21L, "acme-renamed", "api-v2"), (entry.RepositoryId, entry.Owner, entry.Name));
    }

    [Fact]
    public async Task Onboarding_walks_sign_in_access_choose_appearance_then_opens_the_widget()
    {
        var kit = new AccountKit().Start();
        using var onboarding = Onboarding(kit);
        Assert.Equal(OnboardingStep.SignIn, onboarding.Step);
        Assert.False(onboarding.CanGoNext);

        await kit.SignInAsync(AccountKit.TokenJson(), AccountKit.UserJson());
        Assert.True(onboarding.CanGoNext);

        QueueTypicalCatalog(kit.Http);
        onboarding.NextCommand.Execute(null);
        await kit.Catalog.EnsureLoadedAsync(); // the load the step started
        Assert.Equal(OnboardingStep.GrantAccess, onboarding.Step);
        Assert.Contains("already has access through: octo-test, acme-org", onboarding.GrantSummary, StringComparison.Ordinal);

        onboarding.NextCommand.Execute(null);
        Assert.Equal(OnboardingStep.ChooseRepositories, onboarding.Step);
        Assert.Equal("Skip for now", onboarding.NextLabel);
        onboarding.Picker.Shown.First().IsSelected = true;
        Assert.Equal("Next", onboarding.NextLabel);

        onboarding.NextCommand.Execute(null);
        Assert.Equal(OnboardingStep.Appearance, onboarding.Step);
        onboarding.Theme = ThemePreference.Dark;
        var completed = false;
        onboarding.Completed += (_, _) => completed = true;
        Assert.Equal("Open widget", onboarding.NextLabel);
        onboarding.NextCommand.Execute(null);

        Assert.True(completed);
        Assert.True(kit.Settings.App.OnboardingCompleted);
        Assert.Equal(ThemePreference.Dark, kit.Settings.App.Appearance.Theme);
        Assert.Single(kit.Monitors.Current.Repositories);
    }

    [Fact]
    public void Widget_empty_watchlist_offers_add_repositories()
    {
        var shell = new FakeShell();
        var monitors = new MonitorHost(TimeProvider.System);
        monitors.SetBase(new StatusOnlyMonitor(ConnectionState.SignedInIdle));
        var widget = new WidgetViewModel(monitors, TestServices.Settings(), shell, new RecordingBrowser(), TimeProvider.System, new ImmediateDispatcher(), new RepoWatch.Core.Configuration.RepoWatchOptions());

        Assert.True(widget.ShowEmptyWatchlist);
        widget.AddRepositoriesCommand.Execute(null);

        Assert.Equal([RepositoriesTab.Add], shell.RepositoryRequests);
    }

    internal static OnboardingViewModel Onboarding(AccountKit kit)
    {
        var http = RepoWatch.GitHub.GitHubHttp.CreateClient(new QueueHandler());
        var account = new AccountViewModel(kit.Accounts, new FakeShell(), new RecordingBrowser(),
            new RepoWatch.Desktop.Platform.AvatarLoader(http, Microsoft.Extensions.Logging.Abstractions.NullLogger<RepoWatch.Desktop.Platform.AvatarLoader>.Instance),
            new RepoWatch.GitHub.GitHubEndpoints(kit.Options.GitHub), new ImmediateDispatcher(), kit.Time);
        return new OnboardingViewModel(account, new AccessViewModel(kit.Catalog, new RecordingBrowser()), Picker(kit),
            kit.Accounts, kit.Catalog, kit.Watchlist, kit.Settings);
    }
}
