namespace RepoWatch.Core.Identity;

/// <summary>
/// Storage identity of a repository as seen by one account. Uses GitHub's stable repository ID;
/// owner/name change on rename or transfer and are kept only as <see cref="RepositoryMetadata"/>.
/// </summary>
public sealed record RepositoryKey
{
    public RepositoryKey(AccountKey account, long repositoryId)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(repositoryId);
        Account = account;
        RepositoryId = repositoryId;
    }

    public AccountKey Account { get; }

    public long RepositoryId { get; }

    public string StorageKey => $"{Account.StorageKey}/{RepositoryId}";

    public override string ToString() => StorageKey;
}

public enum RepositoryOwnerKind
{
    User,
    Organization,
}

/// <summary>Display and routing data for a repository. May change without the key changing.</summary>
public sealed record RepositoryMetadata
{
    public required RepositoryKey Key { get; init; }

    public required string Owner { get; init; }

    public required string Name { get; init; }

    public required RepositoryOwnerKind OwnerKind { get; init; }

    public required bool IsPrivate { get; init; }

    public required bool IsArchived { get; init; }

    public required string DefaultBranch { get; init; }

    public required Uri HtmlUrl { get; init; }

    /// <summary>Whether GitHub has issues turned on for the repository.</summary>
    public bool HasIssues { get; init; } = true;

    /// <summary>Last push to any branch, as GitHub reports it. Null when unknown (e.g. older cached data).</summary>
    public DateTimeOffset? PushedAt { get; init; }

    public string FullName => $"{Owner}/{Name}";
}
