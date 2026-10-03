using System.Text.Json.Serialization;
using RepoWatch.Core.Access;
using RepoWatch.Core.Actions;
using RepoWatch.Core.Identity;
using RepoWatch.Core.State;
using RepoWatch.GitHub.Api;

namespace RepoWatch.GitHub.Access;

/// <summary>
/// Lists what the signed-in user has granted Repo Watch: the app's installations they can access
/// (GET /user/installations) and each installation's repositories
/// (GET /user/installations/{id}/repositories), following every page. Only repositories both the
/// user and the app can access are returned.
/// </summary>
public sealed class AccessCatalogClient(GitHubApiClient api, TimeProvider time)
{
    public async Task<ApiResult<AccessCatalog>> LoadAsync(CancellationToken cancellationToken)
    {
        var installations = await api.GetAllPagesAsync(api.ApiUri("user/installations"), AccessJsonContext.Default.InstallationsPage,
            p => p.Installations, cancellationToken).ConfigureAwait(false);
        if (!installations.IsSuccess)
        {
            return ApiResult<AccessCatalog>.Fail(installations.Error!);
        }

        var access = new List<InstallationAccess>();
        var repositories = new Dictionary<long, AccessibleRepository>();
        ResourceError? rateLimit = null;
        foreach (var dto in installations.Value!.Items)
        {
            var installation = ToInstallation(dto);
            if (installation.SuspendedAt is not null)
            {
                access.Add(new InstallationAccess(installation, InstallationHealth.Suspended, RepositoriesComplete: false));
                continue;
            }

            // Once rate limited, further requests would hit the same limit: report the rest as not listed.
            if (rateLimit is not null)
            {
                access.Add(new InstallationAccess(installation, InstallationHealth.Unavailable, RepositoriesComplete: false, rateLimit));
                continue;
            }

            var page = await api.GetAllPagesAsync(api.ApiUri($"user/installations/{installation.Id}/repositories"), AccessJsonContext.Default.RepositoriesPage,
                p => p.Repositories, cancellationToken).ConfigureAwait(false);
            if (!page.IsSuccess)
            {
                var health = page.Error!.Kind == ResourceErrorKind.SsoRequired ? InstallationHealth.SsoRequired : InstallationHealth.Unavailable;
                access.Add(new InstallationAccess(installation, health, RepositoriesComplete: false, page.Error));
                if (page.Error.Kind == ResourceErrorKind.RateLimited)
                {
                    rateLimit = page.Error;
                }

                continue;
            }

            foreach (var repository in page.Value!.Items.Select(r => ToRepository(r, installation.Id)).OfType<AccessibleRepository>())
            {
                repositories.TryAdd(repository.Id, repository); // de-duplicate across installations by ID
            }

            access.Add(new InstallationAccess(installation, InstallationHealth.Ok, page.Value.IsComplete) { SsoPartial = page.Value.SsoPartial });
        }

        var ordered = repositories.Values
            .OrderBy(r => r.Owner, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return ApiResult<AccessCatalog>.Ok(new AccessCatalog(access, ordered, installations.Value.IsComplete, time.GetUtcNow())
        {
            SsoHidesInstallations = installations.Value.SsoPartial,
            SsoHiddenOrganizationIds = installations.Value.SsoHiddenOrganizationIds,
        });
    }

    /// <summary>Workflows of one repository (GET /repos/{owner}/{repo}/actions/workflows), for per-repository filters.</summary>
    public async Task<ApiResult<PagedList<Workflow>>> ListWorkflowsAsync(string owner, string name, CancellationToken cancellationToken)
    {
        var uri = api.ApiUri($"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/actions/workflows");
        var result = await api.GetAllPagesAsync(uri, AccessJsonContext.Default.WorkflowsPage, p => p.Workflows, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return ApiResult<PagedList<Workflow>>.Fail(result.Error!);
        }

        var workflows = result.Value!.Items
            .Where(w => w.Id > 0 && Uri.TryCreate(w.HtmlUrl, UriKind.Absolute, out _))
            .Select(w => new Workflow { Id = w.Id, Name = w.Name ?? w.Path ?? $"Workflow {w.Id}", Path = w.Path ?? "", HtmlUrl = new Uri(w.HtmlUrl!) })
            .ToList();
        return ApiResult<PagedList<Workflow>>.Ok(new PagedList<Workflow>(workflows, result.Value.IsComplete));
    }

    private static AppInstallation ToInstallation(InstallationDto dto) => new()
    {
        Id = dto.Id,
        AccountLogin = dto.Account?.Login ?? $"account {dto.TargetId}",
        AccountId = dto.Account?.Id ?? dto.TargetId,
        AccountKind = string.Equals(dto.TargetType ?? dto.Account?.Type, "Organization", StringComparison.OrdinalIgnoreCase)
            ? RepositoryOwnerKind.Organization
            : RepositoryOwnerKind.User,
        Selection = dto.RepositorySelection == "all" ? RepositorySelection.All : RepositorySelection.Selected,
        SuspendedAt = dto.SuspendedAt,
        ManageUrl = Uri.TryCreate(dto.HtmlUrl, UriKind.Absolute, out var manage) && manage.Scheme == Uri.UriSchemeHttps ? manage : null,
        Permissions = dto.Permissions ?? new Dictionary<string, string>(),
    };

    private static AccessibleRepository? ToRepository(RepositoryDto dto, long installationId)
    {
        if (dto.Id <= 0 || dto.Owner?.Login is not { Length: > 0 } owner || string.IsNullOrEmpty(dto.Name)
            || !Uri.TryCreate(dto.HtmlUrl, UriKind.Absolute, out var html))
        {
            return null;
        }

        return new AccessibleRepository
        {
            Id = dto.Id,
            Owner = owner,
            Name = dto.Name,
            OwnerKind = string.Equals(dto.Owner.Type, "Organization", StringComparison.OrdinalIgnoreCase) ? RepositoryOwnerKind.Organization : RepositoryOwnerKind.User,
            IsPrivate = dto.Private,
            IsArchived = dto.Archived,
            DefaultBranch = string.IsNullOrEmpty(dto.DefaultBranch) ? "main" : dto.DefaultBranch,
            HtmlUrl = html,
            Description = dto.Description,
            InstallationId = installationId,
        };
    }
}

internal sealed record InstallationsPage([property: JsonPropertyName("installations")] List<InstallationDto> Installations);

internal sealed record RepositoriesPage([property: JsonPropertyName("repositories")] List<RepositoryDto> Repositories);

internal sealed record WorkflowsPage([property: JsonPropertyName("workflows")] List<WorkflowDto> Workflows);

internal sealed record AccountDto(
    [property: JsonPropertyName("login")] string? Login,
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("type")] string? Type);

internal sealed record InstallationDto
{
    [JsonPropertyName("id")] public long Id { get; init; }

    [JsonPropertyName("account")] public AccountDto? Account { get; init; }

    [JsonPropertyName("target_type")] public string? TargetType { get; init; }

    [JsonPropertyName("target_id")] public long TargetId { get; init; }

    [JsonPropertyName("repository_selection")] public string? RepositorySelection { get; init; }

    [JsonPropertyName("suspended_at")] public DateTimeOffset? SuspendedAt { get; init; }

    [JsonPropertyName("html_url")] public string? HtmlUrl { get; init; }

    [JsonPropertyName("permissions")] public Dictionary<string, string>? Permissions { get; init; }
}

internal sealed record RepositoryDto
{
    [JsonPropertyName("id")] public long Id { get; init; }

    [JsonPropertyName("name")] public string? Name { get; init; }

    [JsonPropertyName("owner")] public AccountDto? Owner { get; init; }

    [JsonPropertyName("private")] public bool Private { get; init; }

    [JsonPropertyName("archived")] public bool Archived { get; init; }

    [JsonPropertyName("default_branch")] public string? DefaultBranch { get; init; }

    [JsonPropertyName("html_url")] public string? HtmlUrl { get; init; }

    [JsonPropertyName("description")] public string? Description { get; init; }
}

internal sealed record WorkflowDto
{
    [JsonPropertyName("id")] public long Id { get; init; }

    [JsonPropertyName("name")] public string? Name { get; init; }

    [JsonPropertyName("path")] public string? Path { get; init; }

    [JsonPropertyName("html_url")] public string? HtmlUrl { get; init; }
}

[JsonSerializable(typeof(InstallationsPage))]
[JsonSerializable(typeof(RepositoriesPage))]
[JsonSerializable(typeof(WorkflowsPage))]
internal sealed partial class AccessJsonContext : JsonSerializerContext;
