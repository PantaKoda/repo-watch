using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using RepoWatch.Core.Accounts;
using RepoWatch.Core.Identity;

namespace RepoWatch.GitHub.Users;

public enum UserLookupStatus
{
    Success,
    /// <summary>401: the token is invalid, expired or revoked.</summary>
    Unauthorized,
    /// <summary>Network, rate limit or server problem; nothing is known about the token.</summary>
    Unavailable,
}

public sealed record UserLookupResult(UserLookupStatus Status, GitHubIdentity? Identity = null, string? Detail = null);

/// <summary>Resolves the signed-in user (GET /user) so the account is keyed by GitHub's stable user ID.</summary>
public sealed class GitHubUserClient(HttpClient http, GitHubEndpoints endpoints)
{
    public async Task<UserLookupResult> GetAuthenticatedUserAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var request = GitHubHttp.ApiRequest(HttpMethod.Get, new Uri(endpoints.ApiBase, "user"), accessToken);
        try
        {
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return new UserLookupResult(UserLookupStatus.Unauthorized);
            }

            if (!response.IsSuccessStatusCode)
            {
                return new UserLookupResult(UserLookupStatus.Unavailable, Detail: $"GitHub returned {(int)response.StatusCode}.");
            }

            var user = await response.Content.ReadFromJsonAsync(UserJsonContext.Default.UserResponse, cancellationToken).ConfigureAwait(false);
            if (user is not { Id: > 0, Login.Length: > 0 })
            {
                return new UserLookupResult(UserLookupStatus.Unavailable, Detail: "GitHub returned an incomplete user profile.");
            }

            var avatar = Uri.TryCreate(user.AvatarUrl, UriKind.Absolute, out var parsed) && parsed.Scheme == Uri.UriSchemeHttps ? parsed : null;
            return new UserLookupResult(UserLookupStatus.Success,
                new GitHubIdentity(AccountKey.ForWebBase(endpoints.WebBase, user.Id), user.Login, user.Name, avatar));
        }
        catch (HttpRequestException ex)
        {
            return new UserLookupResult(UserLookupStatus.Unavailable, Detail: ex.Message);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new UserLookupResult(UserLookupStatus.Unavailable, Detail: "GitHub took too long to respond.");
        }
        catch (JsonException)
        {
            return new UserLookupResult(UserLookupStatus.Unavailable, Detail: "GitHub returned an unreadable user profile.");
        }
    }
}

internal sealed record UserResponse
{
    [JsonPropertyName("id")] public long Id { get; init; }

    [JsonPropertyName("login")] public string Login { get; init; } = "";

    [JsonPropertyName("name")] public string? Name { get; init; }

    [JsonPropertyName("avatar_url")] public string? AvatarUrl { get; init; }
}

[JsonSerializable(typeof(UserResponse))]
internal sealed partial class UserJsonContext : JsonSerializerContext;
