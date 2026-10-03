using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using RepoWatch.Core.Accounts;

namespace RepoWatch.GitHub.Auth;

/// <summary>Device and user codes from GitHub. <see cref="DeviceCode"/> is secret and never displayed or logged.</summary>
public sealed record DeviceAuthorization(string DeviceCode, string UserCode, Uri VerificationUri, DateTimeOffset ExpiresAt, TimeSpan Interval)
{
    public override string ToString() => $"DeviceAuthorization {{ UserCode = {UserCode}, ExpiresAt = {ExpiresAt:O} }}";
}

public enum TokenPollStatus
{
    Success,
    Pending,
    SlowDown,
    Denied,
    Expired,
    /// <summary>A non-retryable error (e.g. device flow disabled, wrong client ID). See <see cref="TokenPollResult.Error"/>.</summary>
    Rejected,
}

public sealed record TokenPollResult(TokenPollStatus Status, StoredCredential? Credential = null, string? Error = null, TimeSpan? NewInterval = null);

public enum RefreshStatus
{
    Success,
    /// <summary>GitHub refused the refresh token (revoked, expired or already used). Sign-in is required.</summary>
    Rejected,
    /// <summary>Network or server problem; the refresh token may still be valid.</summary>
    Transient,
}

public sealed record RefreshResult(RefreshStatus Status, StoredCredential? Credential = null, string? Error = null);

/// <summary>Problem talking to GitHub's OAuth endpoints that may succeed on retry.</summary>
public sealed class GitHubTransientException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// GitHub App device flow and token refresh (see docs: "Generating a user access token for a GitHub App"
/// and "Refreshing user access tokens"). Only the public client ID is sent; device-flow tokens refresh
/// without a client secret.
/// </summary>
public sealed class DeviceFlowClient(HttpClient http, GitHubEndpoints endpoints, string clientId, TimeProvider time)
{
    private const string DeviceGrant = "urn:ietf:params:oauth:grant-type:device_code";

    public async Task<DeviceAuthorization> RequestCodeAsync(CancellationToken cancellationToken)
    {
        var response = await PostAsync(endpoints.DeviceCode, [new("client_id", clientId)], cancellationToken).ConfigureAwait(false);
        if (response.Error is { } error)
        {
            throw new DeviceFlowException(error, response.ErrorDescription);
        }

        if (string.IsNullOrEmpty(response.DeviceCode) || string.IsNullOrEmpty(response.UserCode)
            || !Uri.TryCreate(response.VerificationUri, UriKind.Absolute, out var verification) || verification.Scheme != Uri.UriSchemeHttps)
        {
            throw new GitHubTransientException("GitHub returned an incomplete device code response.");
        }

        var now = time.GetUtcNow();
        return new DeviceAuthorization(
            response.DeviceCode,
            response.UserCode,
            verification,
            now.AddSeconds(response.ExpiresIn is > 0 ? response.ExpiresIn.Value : 900),
            TimeSpan.FromSeconds(Math.Max(1, response.Interval ?? 5)));
    }

    public async Task<TokenPollResult> PollAsync(string deviceCode, CancellationToken cancellationToken)
    {
        var response = await PostAsync(endpoints.AccessToken,
            [new("client_id", clientId), new("device_code", deviceCode), new("grant_type", DeviceGrant)], cancellationToken).ConfigureAwait(false);

        return response.Error switch
        {
            null => new TokenPollResult(TokenPollStatus.Success, ToCredential(response)),
            "authorization_pending" => new TokenPollResult(TokenPollStatus.Pending),
            "slow_down" => new TokenPollResult(TokenPollStatus.SlowDown, NewInterval: response.Interval is > 0 ? TimeSpan.FromSeconds(response.Interval.Value) : null),
            "access_denied" => new TokenPollResult(TokenPollStatus.Denied),
            "expired_token" => new TokenPollResult(TokenPollStatus.Expired),
            var other => new TokenPollResult(TokenPollStatus.Rejected, Error: other),
        };
    }

    public async Task<RefreshResult> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        try
        {
            var response = await PostAsync(endpoints.AccessToken,
                [new("client_id", clientId), new("grant_type", "refresh_token"), new("refresh_token", refreshToken)], cancellationToken).ConfigureAwait(false);
            return response.Error is { } error
                ? new RefreshResult(RefreshStatus.Rejected, Error: error)
                : new RefreshResult(RefreshStatus.Success, ToCredential(response));
        }
        catch (GitHubTransientException ex)
        {
            return new RefreshResult(RefreshStatus.Transient, Error: ex.Message);
        }
    }

    private StoredCredential ToCredential(OAuthResponse response)
    {
        if (string.IsNullOrEmpty(response.AccessToken))
        {
            throw new GitHubTransientException("GitHub returned a token response without an access token.");
        }

        var now = time.GetUtcNow();
        return new StoredCredential(
            response.AccessToken,
            response.ExpiresIn is > 0 ? now.AddSeconds(response.ExpiresIn.Value) : null,
            string.IsNullOrEmpty(response.RefreshToken) ? null : response.RefreshToken,
            response.RefreshTokenExpiresIn is > 0 ? now.AddSeconds(response.RefreshTokenExpiresIn.Value) : null);
    }

    // GitHub's OAuth endpoints report errors as JSON with an "error" field. Server errors and
    // unreadable responses are transient; they say nothing about the validity of the grant.
    private async Task<OAuthResponse> PostAsync(Uri uri, KeyValuePair<string, string>[] form, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new FormUrlEncodedContent(form) };
        request.Headers.Accept.ParseAdd("application/json");

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new GitHubTransientException("Couldn't reach GitHub.", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GitHubTransientException("GitHub took too long to respond.", ex);
        }

        using (response)
        {
            if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throw new GitHubTransientException($"GitHub returned {(int)response.StatusCode}.");
            }

            try
            {
                return await response.Content.ReadFromJsonAsync(OAuthJsonContext.Default.OAuthResponse, cancellationToken).ConfigureAwait(false)
                    ?? throw new GitHubTransientException("GitHub returned an empty response.");
            }
            catch (JsonException ex)
            {
                throw new GitHubTransientException($"GitHub returned an unreadable response ({(int)response.StatusCode}).", ex);
            }
        }
    }
}

public sealed class DeviceFlowException(string error, string? description)
    : Exception($"GitHub rejected the device code request: {error}{(description is null ? "" : $" ({description})")}")
{
    public string Error { get; } = error;
}

internal sealed record OAuthResponse
{
    [JsonPropertyName("device_code")] public string? DeviceCode { get; init; }

    [JsonPropertyName("user_code")] public string? UserCode { get; init; }

    [JsonPropertyName("verification_uri")] public string? VerificationUri { get; init; }

    [JsonPropertyName("interval")] public int? Interval { get; init; }

    [JsonPropertyName("access_token")] public string? AccessToken { get; init; }

    [JsonPropertyName("expires_in")] public int? ExpiresIn { get; init; }

    [JsonPropertyName("refresh_token")] public string? RefreshToken { get; init; }

    [JsonPropertyName("refresh_token_expires_in")] public int? RefreshTokenExpiresIn { get; init; }

    [JsonPropertyName("error")] public string? Error { get; init; }

    [JsonPropertyName("error_description")] public string? ErrorDescription { get; init; }

    public override string ToString() => $"OAuthResponse {{ Error = {Error} }}";
}

[JsonSerializable(typeof(OAuthResponse))]
internal sealed partial class OAuthJsonContext : JsonSerializerContext;
