using Microsoft.Extensions.Logging;
using RepoWatch.Core.Configuration;
using RepoWatch.Core.Identity;
using RepoWatch.Core.Settings;
using RepoWatch.GitHub;
using RepoWatch.GitHub.Api;
using RepoWatch.GitHub.Relay;
using RepoWatch.GitHub.Repositories;

namespace RepoWatch.Desktop.Services;

/// <summary>
/// Builds the polling monitor on the current account session. The monitor runs on the session's
/// lifetime, so signing out cancels its requests even before the coordinator disposes it.
/// </summary>
public sealed class GitHubMonitorFactory(AccountService accounts, HttpClient http, GitHubEndpoints endpoints, TimeProvider time, ILoggerFactory loggers,
    RepoWatchOptions options, PollingConditions conditions, Storage.RepositoryCache cache)
    : IRepositoryMonitorFactory
{
    public IWatchlistAwareMonitor? Create(AccountKey account, AccountSettings settings)
    {
        if (accounts.Session is not { } session || session.Account != account || accounts.Identity is not { } identity || identity.Account != account)
        {
            return null;
        }

        // One budget and one ETag cache per account: the scheduler slows down on the budget GitHub reports.
        // The cache handle belongs to this sign-in: after sign-out clears the account, it writes nothing more.
        var budget = new RateBudget(time);
        var accountCache = cache.ForAccount(account);
        var client = new RepositoryDataClient(new GitHubApiClient(http, endpoints, session, time, accountCache, budget));
        var monitor = new PollingRepositoryMonitor(account, settings, new RepositoryDataSource(client), identity.Login, time,
            loggers.CreateLogger<PollingRepositoryMonitor>(), session.Lifetime, PollingIntervals.From(options.Polling), conditions, accountCache, budget,
            cacheOptions: options.Cache);

        // Optional live updates: the relay pushes "what changed"; polling remains the fallback and reconciles.
        if (options.Relay.IsConfigured && Uri.TryCreate(options.Relay.BaseUrl!.TrimEnd('/') + "/", UriKind.Absolute, out var relay))
        {
            monitor.Attach(new RelayLink(monitor, new RelayClient(RelayHttp.Value, relay), session, time, loggers.CreateLogger<RelayLink>(), session.Lifetime, conditions));
        }

        return monitor;
    }

    /// <summary>The event stream stays open, so the relay gets its own client without a request timeout.</summary>
    private static readonly Lazy<HttpClient> RelayHttp = new(() =>
    {
        var client = GitHubHttp.CreateClient();
        client.Timeout = Timeout.InfiniteTimeSpan;
        return client;
    });
}
