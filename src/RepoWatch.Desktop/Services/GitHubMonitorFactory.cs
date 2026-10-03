using Microsoft.Extensions.Logging;
using RepoWatch.Core.Identity;
using RepoWatch.Core.Settings;
using RepoWatch.GitHub;
using RepoWatch.GitHub.Api;
using RepoWatch.GitHub.Repositories;

namespace RepoWatch.Desktop.Services;

/// <summary>
/// Builds the polling monitor on the current account session. The monitor runs on the session's
/// lifetime, so signing out cancels its requests even before the coordinator disposes it.
/// </summary>
public sealed class GitHubMonitorFactory(AccountService accounts, HttpClient http, GitHubEndpoints endpoints, TimeProvider time, ILoggerFactory loggers)
    : IRepositoryMonitorFactory
{
    public IWatchlistAwareMonitor? Create(AccountKey account, AccountSettings settings)
    {
        if (accounts.Session is not { } session || session.Account != account || accounts.Identity is not { } identity || identity.Account != account)
        {
            return null;
        }

        var client = new RepositoryDataClient(new GitHubApiClient(http, endpoints, session, time));
        return new PollingRepositoryMonitor(account, settings, new RepositoryDataSource(client), identity.Login, time,
            loggers.CreateLogger<PollingRepositoryMonitor>(), session.Lifetime);
    }
}
