using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Serialization;

namespace RepoWatch.Relay;

/// <summary>
/// Confirms with GitHub who a desktop client is and which repositories it and the app can access, using
/// the client's own user token. The token is used only for these requests: it is never stored or logged,
/// and never accepted in a URL.
/// </summary>
public sealed class GitHubAccessClient(HttpClient http)
{
    public const string ApiVersion = "2026-03-10";
    private const int MaxPages = 50;

    public sealed record AccessResult(HttpStatusCode? Failure, long UserId, Dictionary<long, long> Repositories);

    public async Task<AccessResult> CheckAsync(string userToken, CancellationToken cancellationToken)
    {
        var user = await GetAsync(new Uri("user", UriKind.Relative), userToken, RelayAccessJson.Default.UserDto, cancellationToken).ConfigureAwait(false);
        if (user.Value is not { Id: > 0 } identity)
        {
            return new AccessResult(user.Status ?? HttpStatusCode.BadGateway, 0, []);
        }

        var repositories = new Dictionary<long, long>();
        Uri? next = new("user/installations?per_page=100", UriKind.Relative);
        var installations = new List<long>();
        for (var page = 0; next is not null && page < MaxPages; page++)
        {
            var result = await GetAsync(next, userToken, RelayAccessJson.Default.InstallationsDto, cancellationToken).ConfigureAwait(false);
            if (result.Value is null)
            {
                return new AccessResult(result.Status ?? HttpStatusCode.BadGateway, identity.Id, []);
            }

            installations.AddRange((result.Value.Installations ?? []).Where(i => i.Id > 0 && i.SuspendedAt is null).Select(i => i.Id));
            next = result.Next;
        }

        foreach (var installation in installations)
        {
            next = new Uri($"user/installations/{installation}/repositories?per_page=100", UriKind.Relative);
            for (var page = 0; next is not null && page < MaxPages; page++)
            {
                var result = await GetAsync(next, userToken, RelayAccessJson.Default.RepositoriesDto, cancellationToken).ConfigureAwait(false);
                if (result.Value is null)
                {
                    break; // an unavailable installation (e.g. SSO) grants nothing; others still count
                }

                foreach (var repository in (result.Value.Repositories ?? []).Where(r => r.Id > 0))
                {
                    repositories.TryAdd(repository.Id, installation);
                }

                next = result.Next;
            }
        }

        return new AccessResult(null, identity.Id, repositories);
    }

    private async Task<(T? Value, HttpStatusCode? Status, Uri? Next)> GetAsync<T>(Uri uri, string token, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type, CancellationToken cancellationToken)
        where T : class
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", ApiVersion);
        request.Headers.UserAgent.ParseAdd("RepoWatch-Relay/1");
        try
        {
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return (null, response.StatusCode == HttpStatusCode.Unauthorized ? HttpStatusCode.Unauthorized : HttpStatusCode.BadGateway, null);
            }

            var value = await System.Text.Json.JsonSerializer.DeserializeAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), type, cancellationToken).ConfigureAwait(false);
            return (value, null, NextLink(response));
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return (null, HttpStatusCode.BadGateway, null);
        }
    }

    /// <summary>The rel="next" link, only on the configured API host.</summary>
    private Uri? NextLink(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Link", out var values))
        {
            return null;
        }

        foreach (var part in string.Join(",", values).Split(','))
        {
            var segments = part.Split(';');
            if (segments.Length >= 2 && segments.Skip(1).Any(s => s.Trim() is "rel=\"next\"" or "rel=next")
                && Uri.TryCreate(segments[0].Trim().TrimStart('<').TrimEnd('>'), UriKind.Absolute, out var next)
                && http.BaseAddress is { } baseAddress && string.Equals(next.Host, baseAddress.Host, StringComparison.OrdinalIgnoreCase))
            {
                return next;
            }
        }

        return null;
    }
}

internal sealed record UserDto([property: JsonPropertyName("id")] long Id);

internal sealed record InstallationDto([property: JsonPropertyName("id")] long Id, [property: JsonPropertyName("suspended_at")] DateTimeOffset? SuspendedAt);

internal sealed record InstallationsDto([property: JsonPropertyName("installations")] List<InstallationDto>? Installations);

internal sealed record RepositoryIdDto([property: JsonPropertyName("id")] long Id);

internal sealed record RepositoriesDto([property: JsonPropertyName("repositories")] List<RepositoryIdDto>? Repositories);

[JsonSerializable(typeof(UserDto))]
[JsonSerializable(typeof(InstallationsDto))]
[JsonSerializable(typeof(RepositoriesDto))]
internal sealed partial class RelayAccessJson : JsonSerializerContext;
