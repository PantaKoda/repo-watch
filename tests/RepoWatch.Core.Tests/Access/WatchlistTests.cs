using RepoWatch.Core.Access;
using RepoWatch.Core.Identity;
using RepoWatch.Core.Settings;

namespace RepoWatch.Core.Tests.Access;

public sealed class WatchlistTests
{
    private static AccessibleRepository Repo(long id, string owner, string name, long installation = 1, string? description = null) => new()
    {
        Id = id, Owner = owner, Name = name, OwnerKind = RepositoryOwnerKind.User, IsPrivate = false, IsArchived = false,
        DefaultBranch = "main", HtmlUrl = new Uri($"https://github.com/{owner}/{name}"), InstallationId = installation, Description = description,
    };

    private static InstallationAccess Install(long id, string login, InstallationHealth health = InstallationHealth.Ok, bool complete = true) => new(
        new AppInstallation { Id = id, AccountLogin = login, AccountId = id, AccountKind = RepositoryOwnerKind.User, Selection = RepositorySelection.Selected },
        health, complete);

    private static AccessCatalog Catalog(IReadOnlyList<InstallationAccess> installations, params AccessibleRepository[] repositories) =>
        new(installations, repositories, InstallationsComplete: true, DateTimeOffset.UnixEpoch);

    [Fact]
    public void Adding_keeps_existing_order_and_ignores_already_watched_repositories()
    {
        IReadOnlyList<WatchedRepository> list = [new() { RepositoryId = 2, Owner = "o", Name = "b" }];

        list = Watchlist.Add(list, [Repo(1, "o", "a"), Repo(2, "o", "b"), Repo(3, "o", "c")]);

        Assert.Equal([2L, 1L, 3L], list.Select(w => w.RepositoryId));
    }

    [Fact]
    public void Moving_and_removing_by_id()
    {
        IReadOnlyList<WatchedRepository> list = Watchlist.Add([], [Repo(1, "o", "a"), Repo(2, "o", "b"), Repo(3, "o", "c")]);

        list = Watchlist.Move(list, 3, -1);
        Assert.Equal([1L, 3L, 2L], list.Select(w => w.RepositoryId));
        list = Watchlist.Move(list, 1, -5); // clamped
        Assert.Equal([1L, 3L, 2L], list.Select(w => w.RepositoryId));
        list = Watchlist.Remove(list, [3]);
        Assert.Equal([1L, 2L], list.Select(w => w.RepositoryId));
    }

    [Fact]
    public void Renamed_or_transferred_repositories_keep_their_entry_and_settings()
    {
        IReadOnlyList<WatchedRepository> list = [new() { RepositoryId = 5, Owner = "old-owner", Name = "old-name", PullRequests = PullRequestScope.All }];

        list = Watchlist.RefreshNames(list, Catalog([Install(1, "new-owner")], Repo(5, "new-owner", "new-name")));

        var entry = Assert.Single(list);
        Assert.Equal(("new-owner", "new-name"), (entry.Owner, entry.Name));
        Assert.Equal(PullRequestScope.All, entry.PullRequests);
    }

    [Fact]
    public void Access_is_classified_without_claiming_a_repository_does_not_exist()
    {
        var installations = new[]
        {
            Install(1, "me"),
            Install(2, "suspended-org", InstallationHealth.Suspended, complete: false),
            Install(3, "sso-org", InstallationHealth.SsoRequired, complete: false),
            Install(4, "broken-org", InstallationHealth.Unavailable, complete: false),
        };
        var catalog = Catalog(installations, Repo(1, "me", "watched"));

        Assert.Equal(WatchedAccess.Granted, AccessClassifier.Classify(1, "me", catalog).Access);
        Assert.Equal(WatchedAccess.Suspended, AccessClassifier.Classify(20, "suspended-org", catalog).Access);
        Assert.Equal(WatchedAccess.SsoRequired, AccessClassifier.Classify(30, "sso-org", catalog).Access);
        Assert.Equal(WatchedAccess.Unknown, AccessClassifier.Classify(40, "broken-org", catalog).Access);
        // Catalog is incomplete (other installations failed), so even a healthy owner's missing repo is Unknown.
        Assert.Equal(WatchedAccess.Unknown, AccessClassifier.Classify(99, "me", catalog).Access);

        var complete = Catalog([Install(1, "me")], Repo(1, "me", "watched"));
        Assert.Equal(WatchedAccess.NotGranted, AccessClassifier.Classify(99, "me", complete).Access);
        Assert.Equal(WatchedAccess.NotGranted, AccessClassifier.Classify(98, "uninstalled-org", complete).Access);
    }

    [Theory]
    [InlineData("api", null, false, new long[] { 2, 3 })]
    [InlineData("acme api", null, false, new long[] { 2 })]
    [InlineData(null, "octo", false, new long[] { 1, 3 })]
    [InlineData(null, null, true, new long[] { 3 })]
    [InlineData("fixture", null, false, new long[] { 1 })]
    public void Picker_filter_combines_search_owner_and_selected(string? search, string? owner, bool selectedOnly, long[] expected)
    {
        var repositories = new[] { Repo(1, "octo", "dotfiles", description: "Synthetic fixture"), Repo(2, "acme", "api"), Repo(3, "octo", "api-client") };
        var selected = new HashSet<long> { 3 };
        var filter = new PickerFilter(search, owner, selectedOnly);

        Assert.Equal(expected, repositories.Where(r => filter.Matches(r, selected.Contains(r.Id))).Select(r => r.Id));
    }
}
