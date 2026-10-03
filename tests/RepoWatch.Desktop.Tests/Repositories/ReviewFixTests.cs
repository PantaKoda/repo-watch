using RepoWatch.Core.Access;
using RepoWatch.Core.Platform;
using RepoWatch.Core.Settings;
using RepoWatch.Desktop.ViewModels;
using static RepoWatch.Desktop.Tests.Repositories.CatalogFixtures;

namespace RepoWatch.Desktop.Tests.Repositories;

/// <summary>Regression tests for the PR #4 review findings.</summary>
public sealed class ReviewFixTests
{
    private static RepositoryPickerViewModel Picker(AccountKit kit, RecordingBrowser? browser = null) =>
        new(kit.Catalog, kit.Watchlist, browser ?? new RecordingBrowser(), kit.Time);

    [Fact]
    public async Task Picker_rows_keep_tracking_the_watchlist_after_a_refresh()
    {
        var kit = await SignedInWithCatalogAsync();
        using var picker = Picker(kit);
        var shownBefore = picker.Shown.ToList();

        QueueTypicalCatalog(kit.Http);
        await kit.Catalog.RefreshAsync(); // "Refresh list"

        Assert.Equal(shownBefore, picker.Shown); // same row instances, reused by ID
        picker.AddShownCommand.Execute(null);
        Assert.All(picker.Shown, c => Assert.True(c.IsSelected));
        Assert.Equal("Add all (0)", picker.AddShownLabel);

        kit.Watchlist.Remove([21]); // e.g. Remove on the Watched tab
        Assert.False(picker.Shown.Single(c => c.Id == 21).IsSelected);
        Assert.Equal("Add all (1)", picker.AddShownLabel);
    }

    [Fact]
    public async Task Picker_rows_show_renamed_repositories_and_add_them_under_the_new_name()
    {
        var kit = await SignedInWithCatalogAsync();
        using var picker = Picker(kit);
        var row = picker.Shown.Single(c => c.Id == 21);

        kit.Http.Json(InstallationsJson(Installation(2, "acme-renamed", "Organization")))
            .Json(RepositoriesJson(Repository(21, "acme-renamed", "api-v2", ownerType: "Organization")));
        await kit.Catalog.RefreshAsync();

        Assert.Same(row, picker.Shown.Single());
        Assert.Equal("acme-renamed/api-v2", row.FullName);
        row.IsSelected = true;
        Assert.Equal(("acme-renamed", "api-v2"), (kit.Watchlist.Repositories[0].Owner, kit.Watchlist.Repositories[0].Name));
    }

    [Fact]
    public async Task Watched_repositories_missing_from_an_incomplete_list_are_not_called_ungranted()
    {
        var kit = await SignedInWithCatalogAsync();
        kit.Settings.UpdateAccount(AccountKit.Octo, a => a with { Watchlist = [new WatchedRepository { RepositoryId = 99, Owner = "sso-org", Name = "hidden" }] });
        kit.Watchlist.Move(99, 0); // reload

        kit.Http.Json(InstallationsJson(Installation(1, "octo-test"), Installation(3, "broken-org", "Organization")))
            .Json(RepositoriesJson(Repository(11, "octo-test", "dotfiles")))
            .Status(System.Net.HttpStatusCode.BadGateway);
        await kit.Catalog.RefreshAsync();
        using var picker = Picker(kit);

        Assert.False(kit.Catalog.Catalog!.IsComplete);
        Assert.EndsWith("(plus 1 watched repository whose access couldn't be confirmed)", picker.SelectedCountText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unsaved_branch_text_survives_other_option_changes()
    {
        var kit = await SignedInWithCatalogAsync();
        kit.Watchlist.Add(kit.Catalog.Catalog!.Repositories.Where(r => r.Id == 11).ToList());
        using var editor = new WatchlistViewModel(kit.Watchlist, kit.Catalog, new RecordingBrowser());
        var item = editor.Items.Single();

        item.Branches = "main, release"; // typed, not saved
        item.ShowIssues = false;         // another option changes the watchlist

        Assert.Equal("main, release", item.Branches);
        Assert.Empty(kit.Watchlist.Repositories[0].Branches);

        item.SaveBranchesCommand.Execute(null);
        Assert.Equal(["main", "release"], kit.Watchlist.Repositories[0].Branches);
    }

    [Fact]
    public async Task Workflows_are_requested_under_the_current_name_and_discarded_after_sign_out()
    {
        var kit = await SignedInWithCatalogAsync();
        kit.Settings.UpdateAccount(AccountKit.Octo, a => a with { Watchlist = [new WatchedRepository { RepositoryId = 11, Owner = "old-owner", Name = "old-name" }] });
        kit.Watchlist.Move(11, 0); // reload the stale last-known name
        using var editor = new WatchlistViewModel(kit.Watchlist, kit.Catalog, new RecordingBrowser());
        var item = editor.Items.Single();

        kit.Http.Json("""{"total_count":1,"workflows":[{"id":7,"name":"CI","path":".github/workflows/ci.yml","html_url":"https://github.com/octo-test/dotfiles/blob/main/.github/workflows/ci.yml"}]}""");
        await item.LoadWorkflowsCommand.ExecuteAsync(null);

        Assert.Contains("GET /repos/octo-test/dotfiles/actions/workflows", kit.Http.Requests);
        Assert.Single(item.Workflows);

        await kit.Accounts.SignOutAsync();
        var result = await kit.Catalog.ListWorkflowsAsync("octo-test", "dotfiles", TestContext.Current.CancellationToken);
        Assert.False(result.IsSuccess); // no session: a result, never an exception
    }

    [Fact]
    public async Task Option_changes_update_the_monitor_in_place_and_removals_replace_it()
    {
        var kit = await SignedInWithCatalogAsync();
        kit.Watchlist.Add(kit.Catalog.Catalog!.Repositories.Where(r => r.Id is 11 or 12).ToList());
        var monitor = kit.Monitors.Current;

        kit.Watchlist.UpdateRepository(11, w => w with { PullRequests = PullRequestScope.All });
        kit.Watchlist.SetOrdering(RepositoryOrdering.Manual);
        kit.Watchlist.Add(kit.Catalog.Catalog.Repositories.Where(r => r.Id == 21).ToList());

        Assert.Same(monitor, kit.Monitors.Current);
        Assert.Equal(RepositoryOrdering.Manual, monitor.Ordering);
        Assert.Equal(PullRequestScope.All, monitor.Repositories.Single(r => r.Key.RepositoryId == 11).Watch.PullRequests);
        Assert.Equal(3, monitor.Repositories.Count);

        kit.Watchlist.Remove([12]);
        Assert.NotSame(monitor, kit.Monitors.Current);
        Assert.DoesNotContain(kit.Monitors.Current.Repositories, r => r.Key.RepositoryId == 12);
    }

    [Fact]
    public async Task Onboarding_opened_while_signed_in_loads_existing_access()
    {
        var kit = new AccountKit().Start();
        await kit.SignInAsync(AccountKit.TokenJson(), AccountKit.UserJson());
        QueueTypicalCatalog(kit.Http);

        using var onboarding = WatchlistAcceptanceTests.Onboarding(kit);
        await kit.Catalog.EnsureLoadedAsync();

        Assert.Equal(OnboardingStep.GrantAccess, onboarding.Step);
        Assert.Contains("already has access through", onboarding.GrantSummary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(LinkOpenResult.Refused, "isn't on GitHub")]
    [InlineData(LinkOpenResult.Failed, "Couldn't open your web browser")]
    public async Task Link_buttons_report_links_that_do_not_open(LinkOpenResult result, string expected)
    {
        var kit = new AccountKit().Start();
        await kit.SignInAsync(AccountKit.TokenJson(), AccountKit.UserJson());
        kit.Http.Json(InstallationsJson());
        await kit.Catalog.RefreshAsync();
        var browser = new RecordingBrowser { Result = result };
        using var picker = Picker(kit, browser);
        using var access = new AccessViewModel(kit.Catalog, browser);

        await picker.GrantAccessCommand.ExecuteAsync(null);
        await access.GrantAccessCommand.ExecuteAsync(null);

        Assert.Contains(expected, picker.Links.Notice, StringComparison.Ordinal);
        Assert.Contains(expected, access.Links.Notice, StringComparison.Ordinal);

        browser.Result = LinkOpenResult.Opened;
        await picker.GrantAccessCommand.ExecuteAsync(null);
        Assert.False(picker.Links.HasNotice);
    }
}
