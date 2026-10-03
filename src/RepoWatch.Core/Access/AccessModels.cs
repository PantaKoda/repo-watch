using RepoWatch.Core.Identity;
using RepoWatch.Core.State;

namespace RepoWatch.Core.Access;

public enum RepositorySelection
{
    /// <summary>The installation grants every repository of the account (now and future).</summary>
    All,
    /// <summary>Only repositories the owner selected on GitHub.</summary>
    Selected,
}

/// <summary>A Repo Watch GitHub App installation the signed-in user can see.</summary>
public sealed record AppInstallation
{
    public required long Id { get; init; }

    public required string AccountLogin { get; init; }

    public required long AccountId { get; init; }

    public required RepositoryOwnerKind AccountKind { get; init; }

    public required RepositorySelection Selection { get; init; }

    public DateTimeOffset? SuspendedAt { get; init; }

    /// <summary>GitHub page where the owner manages this installation's repository access.</summary>
    public Uri? ManageUrl { get; init; }

    /// <summary>Granted permissions, e.g. "actions" → "read".</summary>
    public IReadOnlyDictionary<string, string> Permissions { get; init; } = new Dictionary<string, string>();

    /// <summary>Read permissions monitoring needs that this installation has not granted (e.g. after a permission change).</summary>
    public IReadOnlyList<string> MissingPermissions => RequiredPermissions.All
        .Where(p => !Permissions.TryGetValue(p, out var level) || level is not ("read" or "write" or "admin"))
        .ToList();
}

public static class RequiredPermissions
{
    /// <summary>Read permissions the app requests (see docs/github-app-setup.md).</summary>
    public static IReadOnlyList<string> All { get; } = ["metadata", "actions", "checks", "statuses", "issues", "pull_requests"];
}

/// <summary>A repository the user and the app can both access.</summary>
public sealed record AccessibleRepository
{
    public required long Id { get; init; }

    public required string Owner { get; init; }

    public required string Name { get; init; }

    public required RepositoryOwnerKind OwnerKind { get; init; }

    public required bool IsPrivate { get; init; }

    public required bool IsArchived { get; init; }

    public required string DefaultBranch { get; init; }

    public required Uri HtmlUrl { get; init; }

    /// <summary>Untrusted repository text; display only.</summary>
    public string? Description { get; init; }

    public required long InstallationId { get; init; }

    public string FullName => $"{Owner}/{Name}";
}

public enum InstallationHealth
{
    Ok,
    Suspended,
    SsoRequired,
    /// <summary>Repositories could not be listed (403, network, server error...).</summary>
    Unavailable,
}

/// <summary>
/// One installation and whether its repositories could be listed. <see cref="SsoPartial"/>: GitHub left out
/// repositories that require SAML single sign-on, so the list is incomplete.
/// </summary>
public sealed record InstallationAccess(AppInstallation Installation, InstallationHealth Health, bool RepositoriesComplete, ResourceError? Error = null)
{
    public bool SsoPartial { get; init; }
}

/// <summary>Everything the signed-in user has granted Repo Watch, as of <see cref="LoadedAt"/>.</summary>
public sealed record AccessCatalog(
    IReadOnlyList<InstallationAccess> Installations,
    IReadOnlyList<AccessibleRepository> Repositories,
    bool InstallationsComplete,
    DateTimeOffset LoadedAt)
{
    /// <summary>
    /// GitHub left out installations of organizations that require SAML single sign-on
    /// (<c>X-GitHub-SSO: partial-results</c>). Their repositories are hidden, not ungranted.
    /// </summary>
    public bool SsoHidesInstallations { get; init; }

    /// <summary>IDs of the organizations GitHub left out, when it named them.</summary>
    public IReadOnlyList<long> SsoHiddenOrganizationIds { get; init; } = [];

    /// <summary>True when every installation and every repository page was loaded, and nothing was hidden by SSO.</summary>
    public bool IsComplete => InstallationsComplete && !SsoHidesInstallations
        && Installations.All(i => i.Health == InstallationHealth.Ok && i.RepositoriesComplete && !i.SsoPartial);

    public IReadOnlyList<string> Owners => Repositories.Select(r => r.Owner).Distinct(StringComparer.OrdinalIgnoreCase)
        .Order(StringComparer.OrdinalIgnoreCase).ToList();
}

public enum WatchedAccess
{
    /// <summary>GitHub currently grants Repo Watch access.</summary>
    Granted,
    /// <summary>The full list was loaded and this repository is not in it: access was not granted or was removed.</summary>
    NotGranted,
    /// <summary>The owner's installation is suspended.</summary>
    Suspended,
    /// <summary>The owner's organization requires SSO before Repo Watch can see its repositories.</summary>
    SsoRequired,
    /// <summary>The list could not be fully loaded; access is not known. Never treated as "does not exist".</summary>
    Unknown,
}

public static class AccessClassifier
{
    /// <summary>
    /// Classifies a watched repository against the catalog. "Not granted" is only claimed when the
    /// list is complete and the owner's installation is healthy; otherwise the result names the
    /// problem or is Unknown.
    /// </summary>
    public static (WatchedAccess Access, InstallationAccess? Installation) Classify(long repositoryId, string lastKnownOwner, AccessCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (catalog.Repositories.FirstOrDefault(r => r.Id == repositoryId) is { } repository)
        {
            return (WatchedAccess.Granted, catalog.Installations.FirstOrDefault(i => i.Installation.Id == repository.InstallationId));
        }

        var ownerInstallation = catalog.Installations.FirstOrDefault(i =>
            string.Equals(i.Installation.AccountLogin, lastKnownOwner, StringComparison.OrdinalIgnoreCase));
        return ownerInstallation?.Health switch
        {
            InstallationHealth.Suspended => (WatchedAccess.Suspended, ownerInstallation),
            InstallationHealth.SsoRequired => (WatchedAccess.SsoRequired, ownerInstallation),
            InstallationHealth.Unavailable => (WatchedAccess.Unknown, ownerInstallation),
            _ => (catalog.IsComplete ? WatchedAccess.NotGranted : WatchedAccess.Unknown, ownerInstallation),
        };
    }
}
