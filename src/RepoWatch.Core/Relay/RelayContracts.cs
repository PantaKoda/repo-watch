using System.Text.Json.Serialization;

namespace RepoWatch.Core.Relay;

/// <summary>
/// The wire contract between the desktop app and the relay (Stage 10). The relay only ever tells the
/// app *what changed* (repository and part); the app then reads the current state from GitHub itself.
/// </summary>
public static class RelayProtocol
{
    /// <summary>Server-sent event names.</summary>
    public const string Invalidate = "invalidate";

    /// <summary>The requested replay point is gone (relay restart or buffer gap): refresh everything.</summary>
    public const string Reset = "reset";

    /// <summary>The relay session ended; create a new one.</summary>
    public const string Expired = "expired";

    /// <summary>GitHub access changed (app uninstalled, repositories removed, authorization revoked).</summary>
    public const string Revoked = "revoked";

    public static class Parts
    {
        public const string Metadata = "metadata";
        public const string Actions = "actions";
        public const string PullRequests = "pulls";
        public const string Issues = "issues";
    }
}

/// <summary>POST /sessions body. The caller's GitHub user token goes in the Authorization header, never here.</summary>
public sealed record RelaySessionRequest([property: JsonPropertyName("repositoryIds")] IReadOnlyList<long> RepositoryIds);

/// <summary>
/// A short-lived relay session. <see cref="Allowed"/> holds the requested repositories GitHub confirmed this
/// user and the app can access; <see cref="Rejected"/> the rest. Events are only ever sent for allowed ones.
/// </summary>
public sealed record RelaySessionResponse(
    [property: JsonPropertyName("sessionToken")] string SessionToken,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt,
    [property: JsonPropertyName("allowed")] IReadOnlyList<long> Allowed,
    [property: JsonPropertyName("rejected")] IReadOnlyList<long> Rejected);

/// <summary>Data of an <see cref="RelayProtocol.Invalidate"/> or <see cref="RelayProtocol.Revoked"/> event.</summary>
public sealed record RelayInvalidation(
    [property: JsonPropertyName("repositoryId")] long RepositoryId,
    [property: JsonPropertyName("parts")] IReadOnlyList<string> Parts);

[JsonSerializable(typeof(RelaySessionRequest))]
[JsonSerializable(typeof(RelaySessionResponse))]
[JsonSerializable(typeof(RelayInvalidation))]
[JsonSerializable(typeof(RelayInvalidation[]))]
public sealed partial class RelayJsonContext : JsonSerializerContext;
