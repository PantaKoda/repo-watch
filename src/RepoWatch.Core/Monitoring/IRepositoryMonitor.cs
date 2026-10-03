using RepoWatch.Core.Identity;
using RepoWatch.Core.Settings;
using RepoWatch.Core.State;

namespace RepoWatch.Core.Monitoring;

/// <summary>How the widget is currently getting data. Shown to the user as-is.</summary>
public enum ConnectionState
{
    NotSignedIn,
    /// <summary>Checking the stored sign-in at startup; nothing is monitored yet.</summary>
    Connecting,
    /// <summary>Signed in, but nothing is being monitored (no repositories chosen, or monitoring not started).</summary>
    SignedInIdle,
    /// <summary>Showing labeled fixture data, not GitHub.</summary>
    Demo,
    Polling,
    Live,
    Offline,
    Paused,
    ReconnectRequired,
}

/// <summary>A watched repository with its current snapshot. <see cref="ManualIndex"/> is its position in the user's order.</summary>
public sealed record MonitoredRepository(WatchedRepository Watch, RepositorySnapshot Snapshot, int ManualIndex)
{
    public RepositoryKey Key => Snapshot.Key;

    /// <summary>Current owner/name if known, otherwise the last known name from the watchlist.</summary>
    public string DisplayName => Snapshot.Metadata.Value?.FullName
        ?? (Watch.Owner.Length > 0 && Watch.Name.Length > 0 ? $"{Watch.Owner}/{Watch.Name}" : $"Repository {Watch.RepositoryId}");
}

/// <summary>
/// Source of repository state for the UI. The UI observes it and issues refresh commands;
/// polling, HTTP and persistence live behind this interface, never in views.
/// </summary>
public interface IRepositoryMonitor
{
    ConnectionState State { get; }

    /// <summary>Watched repositories in manual order.</summary>
    IReadOnlyList<MonitoredRepository> Repositories { get; }

    bool IsRefreshing { get; }

    /// <summary>Raised when any of the above changes. May be raised on any thread.</summary>
    event EventHandler? Changed;

    /// <summary>Requests a refresh of one repository, or all when null. Completes when the refresh ends.</summary>
    Task RefreshAsync(RepositoryKey? repository = null, CancellationToken cancellationToken = default);
}
