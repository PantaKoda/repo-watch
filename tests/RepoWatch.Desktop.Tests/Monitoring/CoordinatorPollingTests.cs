using Microsoft.Extensions.Logging.Abstractions;
using RepoWatch.Core.Access;
using RepoWatch.Core.Identity;
using RepoWatch.Core.Monitoring;
using RepoWatch.Core.Settings;
using RepoWatch.Desktop.Services;

namespace RepoWatch.Desktop.Tests.Monitoring;

/// <summary>How the coordinator hands the watchlist to the GitHub polling monitor.</summary>
public sealed class CoordinatorPollingTests
{
    private sealed class RecordingFactory : IRepositoryMonitorFactory
    {
        public List<PollingRepositoryMonitor> Created { get; } = [];

        public FakeDataSource Source { get; } = new();

        public IWatchlistAwareMonitor? Create(AccountKey account, AccountSettings settings)
        {
            var monitor = new PollingRepositoryMonitor(account, settings, Source, "octo-test", TimeProvider.System, NullLogger.Instance, CancellationToken.None,
                PollingMonitorTests.Every(TimeSpan.FromHours(1)));
            Created.Add(monitor);
            return monitor;
        }
    }

    private static AccessibleRepository Repository(long id) => new()
    {
        Id = id, Owner = "octo-test", Name = $"repo{id}", OwnerKind = RepositoryOwnerKind.User, IsPrivate = true, IsArchived = false,
        DefaultBranch = "main", HtmlUrl = new Uri($"https://github.com/octo-test/repo{id}"), InstallationId = 1,
    };

    [Fact]
    public async Task Polling_starts_only_for_a_non_empty_watchlist_and_options_apply_in_place()
    {
        var factory = new RecordingFactory();
        var kit = new AccountKit { MonitorFactory = factory }.Start();
        await kit.SignInAsync(AccountKit.TokenJson(), AccountKit.UserJson());

        // Signed in with nothing watched: no polling monitor, so no requests.
        Assert.Empty(factory.Created);
        Assert.Equal(ConnectionState.SignedInIdle, kit.Monitors.Current.State);

        kit.Watchlist.Add([Repository(1)]);
        var monitor = Assert.Single(factory.Created);
        Assert.Same(monitor, kit.Monitors.Current);
        await monitor.RefreshAsync(cancellationToken: TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Contains("repo 1", factory.Source.Calls);

        // Adding and changing options keep the same monitor; removal replaces it.
        kit.Watchlist.Add([Repository(2)]);
        kit.Watchlist.UpdateRepository(1, w => w with { ShowIssues = false });
        Assert.Single(factory.Created);

        kit.Watchlist.Remove([1]);
        Assert.Equal(2, factory.Created.Count);
        Assert.Equal([2L], kit.Monitors.Current.Repositories.Select(r => r.Key.RepositoryId));

        // Signing out ends polling and returns to the signed-out state.
        await kit.Accounts.SignOutAsync();
        Assert.Equal(ConnectionState.NotSignedIn, kit.Monitors.Current.State);
    }
}
