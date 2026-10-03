using RepoWatch.Core.Actions;
using RepoWatch.Core.Identity;
using RepoWatch.Core.Issues;
using RepoWatch.Core.PullRequests;

namespace RepoWatch.Core.State;

/// <summary>
/// Everything known about one watched repository. Sections are refreshed independently
/// and each keeps its own value, freshness and error.
/// </summary>
public sealed record RepositorySnapshot
{
    public RepositorySnapshot(RepositoryKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        Key = key;
    }

    public RepositoryKey Key { get; }

    public Resource<RepositoryMetadata> Metadata { get; init; } = Resource<RepositoryMetadata>.NotLoaded;

    public Resource<ActionsState> Actions { get; init; } = Resource<ActionsState>.NotLoaded;

    public Resource<PullRequestsState> PullRequests { get; init; } = Resource<PullRequestsState>.NotLoaded;

    public Resource<IssuesState> Issues { get; init; } = Resource<IssuesState>.NotLoaded;

    /// <summary>
    /// Loss of repository access withholds every section's cached content, not just the
    /// endpoint that reported it.
    /// </summary>
    public RepositorySnapshot WithAccessLost(ResourceError error) => this with
    {
        Metadata = Metadata.AccessLost(error),
        Actions = Actions.AccessLost(error),
        PullRequests = PullRequests.AccessLost(error),
        Issues = Issues.AccessLost(error),
    };
}
