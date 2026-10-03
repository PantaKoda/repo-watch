using System.Text.Json;
using RepoWatch.Core.Relay;

namespace RepoWatch.Relay;

/// <summary>What one delivery means for clients: repositories whose data changed, and access that ended.</summary>
public sealed record MappedDelivery(
    IReadOnlyList<RelayInvalidation> Invalidations,
    IReadOnlyList<long> RemovedRepositoryIds,
    long? RevokedInstallationId,
    long? RevokedUserId)
{
    public static MappedDelivery None { get; } = new([], [], null, null);
}

/// <summary>
/// Maps a webhook to invalidations. Only IDs and the action are read from the payload: its content is
/// untrusted and never acted on beyond "this repository's X changed". Clients then read current state
/// from GitHub themselves.
/// <para>Subscribed events (configure exactly these on the GitHub App):</para>
/// <list type="bullet">
/// <item>workflow_run, workflow_job → Actions</item>
/// <item>check_run, check_suite, status → Actions and pull requests (pull request checks)</item>
/// <item>pull_request, pull_request_review → pull requests</item>
/// <item>issues → issues</item>
/// <item>repository → metadata (renamed, transferred, archived, visibility), or access ended (deleted)</item>
/// <item>installation, installation_repositories, github_app_authorization → access changes</item>
/// </list>
/// </summary>
public static class EventMapper
{
    public static IReadOnlyList<string> SubscribedEvents { get; } =
    [
        "workflow_run", "workflow_job", "check_run", "check_suite", "status", "pull_request", "pull_request_review", "issues", "repository",
        "installation", "installation_repositories", "github_app_authorization",
    ];

    public static MappedDelivery Map(string eventName, ReadOnlySpan<byte> payload)
    {
        using var document = JsonDocument.Parse(payload.ToArray());
        var root = document.RootElement;
        var action = root.TryGetProperty("action", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
        var repositoryId = Id(root, "repository");

        return eventName switch
        {
            "workflow_run" or "workflow_job" => Invalidate(repositoryId, RelayProtocol.Parts.Actions),
            "check_run" or "check_suite" or "status" => Invalidate(repositoryId, RelayProtocol.Parts.Actions, RelayProtocol.Parts.PullRequests),
            "pull_request" or "pull_request_review" => Invalidate(repositoryId, RelayProtocol.Parts.PullRequests),
            "issues" => Invalidate(repositoryId, RelayProtocol.Parts.Issues),
            "repository" when action == "deleted" && repositoryId is { } deleted => new([], [deleted], null, null),
            "repository" => Invalidate(repositoryId, RelayProtocol.Parts.Metadata),
            "installation" when action is "deleted" or "suspend" => new([], [], Id(root, "installation"), null),
            "installation_repositories" when action == "removed" => new([], Ids(root, "repositories_removed"), null, null),
            "github_app_authorization" when action == "revoked" => new([], [], null, Id(root, "sender")),
            _ => MappedDelivery.None,
        };
    }

    private static MappedDelivery Invalidate(long? repositoryId, params string[] parts) =>
        repositoryId is { } id ? new([new RelayInvalidation(id, parts)], [], null, null) : MappedDelivery.None;

    private static long? Id(JsonElement root, string property) =>
        root.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty("id", out var id) && id.TryGetInt64(out var value) && value > 0 ? value : null;

    private static List<long> Ids(JsonElement root, string property) =>
        root.TryGetProperty(property, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(e => e.TryGetProperty("id", out var id) && id.TryGetInt64(out var value) ? value : 0).Where(v => v > 0).ToList()
            : [];
}
