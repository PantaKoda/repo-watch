using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using RepoWatch.Core.State;

namespace RepoWatch.GitHub.Api;

/// <summary>Source of access tokens for API calls; implemented by <see cref="Auth.AccountSession"/>.</summary>
public interface IAccessTokenSource
{
    /// <returns>Null when signed out or a new sign-in is required.</returns>
    Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default);

    /// <summary>Called after a 401 for <paramref name="rejectedAccessToken"/>; returns a renewed token or null.</summary>
    Task<string?> HandleUnauthorizedAsync(string rejectedAccessToken, CancellationToken cancellationToken = default);
}

/// <summary>Result of an API call: a value, or a classified error. Never both.</summary>
public sealed record ApiResult<T>(T? Value, ResourceError? Error)
{
    public bool IsSuccess => Error is null;

    public static ApiResult<T> Ok(T value) => new(value, null);

    public static ApiResult<T> Fail(ResourceError error) => new(default, error);
}

/// <summary>
/// All pages of a list, or as many as the page limit allowed. <see cref="SsoPartial"/> means GitHub
/// left out results from organizations that require SAML single sign-on (<c>X-GitHub-SSO: partial-results</c>),
/// so the list is incomplete even though every page loaded.
/// </summary>
public sealed record PagedList<T>(IReadOnlyList<T> Items, bool IsComplete)
{
    public bool SsoPartial { get; init; }

    /// <summary>IDs of the organizations GitHub left out, when it named them.</summary>
    public IReadOnlyList<long> SsoHiddenOrganizationIds { get; init; } = [];
}

/// <summary>
/// Authenticated GET requests to the REST API with error classification and safe pagination.
/// Tokens go only in the Authorization header. Pagination follows GitHub's Link "next" URLs as given,
/// but only on the configured API host.
/// </summary>
public sealed class GitHubApiClient(HttpClient http, GitHubEndpoints endpoints, IAccessTokenSource tokens, TimeProvider time)
{
    public const int PageSize = 100;
    public const int DefaultMaxPages = 50;

    public Uri ApiUri(string relative) => new(endpoints.ApiBase, relative);

    public async Task<ApiResult<T>> GetAsync<T>(Uri uri, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
    {
        var response = await SendAsync(token => GitHubHttp.ApiRequest(HttpMethod.Get, uri, token), cancellationToken).ConfigureAwait(false);
        if (response.Error is not null)
        {
            return ApiResult<T>.Fail(response.Error);
        }

        using var message = response.Message!;
        return await ReadAsync(message, typeInfo, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs a GraphQL query (POST /graphql). HTTP-level failures are classified like REST; GraphQL
    /// errors inside a 200 response are left to the caller, which knows which fields may fail.
    /// </summary>
    public async Task<ApiResult<TResponse>> PostGraphQLAsync<TRequest, TResponse>(
        TRequest request, JsonTypeInfo<TRequest> requestType, JsonTypeInfo<TResponse> responseType, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(request, requestType);
        var uri = ApiUri("graphql");
        var response = await SendAsync(token =>
        {
            var message = GitHubHttp.ApiRequest(HttpMethod.Post, uri, token);
            message.Content = new ByteArrayContent(body) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } };
            return message;
        }, cancellationToken).ConfigureAwait(false);
        if (response.Error is not null)
        {
            return ApiResult<TResponse>.Fail(response.Error);
        }

        using var reply = response.Message!;
        return await ReadAsync(reply, responseType, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A classified error for GraphQL errors returned with HTTP 200.</summary>
    public ResourceError GraphQLError(string? type) => type switch
    {
        "NOT_FOUND" => Error(ResourceErrorKind.NotFound, "GitHub returned not found, or not accessible to Repo Watch."),
        "FORBIDDEN" or "INSUFFICIENT_SCOPES" => Error(ResourceErrorKind.Forbidden, "GitHub denied access to this data."),
        "RATE_LIMITED" => Error(ResourceErrorKind.RateLimited, "GitHub's rate limit was reached; Repo Watch will wait before trying again."),
        _ => Error(ResourceErrorKind.InvalidResponse, "GitHub reported an error for this request."),
    };

    /// <summary>
    /// Follows pagination from <paramref name="first"/> (per_page=100). If <paramref name="maxPages"/> is reached
    /// the result is marked incomplete rather than presented as the full list.
    /// </summary>
    public async Task<ApiResult<PagedList<TItem>>> GetAllPagesAsync<TPage, TItem>(
        Uri first, JsonTypeInfo<TPage> typeInfo, Func<TPage, IEnumerable<TItem>> items, CancellationToken cancellationToken, int maxPages = DefaultMaxPages)
    {
        var all = new List<TItem>();
        var ssoPartial = false;
        var hiddenOrganizations = new HashSet<long>();
        Uri? next = WithPageSize(first);
        for (var page = 0; next is not null; page++)
        {
            if (page == maxPages)
            {
                return ApiResult<PagedList<TItem>>.Ok(new PagedList<TItem>(all, IsComplete: false)
                {
                    SsoPartial = ssoPartial,
                    SsoHiddenOrganizationIds = hiddenOrganizations.Order().ToList(),
                });
            }

            var current = next;
            var response = await SendAsync(token => GitHubHttp.ApiRequest(HttpMethod.Get, current, token), cancellationToken).ConfigureAwait(false);
            if (response.Error is not null)
            {
                return ApiResult<PagedList<TItem>>.Fail(response.Error);
            }

            using var message = response.Message!;
            var parsed = await ReadAsync(message, typeInfo, cancellationToken).ConfigureAwait(false);
            if (!parsed.IsSuccess)
            {
                return ApiResult<PagedList<TItem>>.Fail(parsed.Error!);
            }

            all.AddRange(items(parsed.Value!));
            if (PartialSsoResults(message.Headers) is { } hidden)
            {
                ssoPartial = true;
                hiddenOrganizations.UnionWith(hidden);
            }

            next = NextLink(message.Headers);
        }

        return ApiResult<PagedList<TItem>>.Ok(new PagedList<TItem>(all, IsComplete: !ssoPartial)
        {
            SsoPartial = ssoPartial,
            SsoHiddenOrganizationIds = hiddenOrganizations.Order().ToList(),
        });
    }

    /// <summary>
    /// Organization IDs from <c>X-GitHub-SSO: partial-results; organizations=1,2</c> on a successful response
    /// (empty when GitHub did not name them), or null when the results are not partial.
    /// </summary>
    public static IReadOnlyList<long>? PartialSsoResults(HttpResponseHeaders headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        if (!headers.TryGetValues("X-GitHub-SSO", out var values))
        {
            return null;
        }

        var header = string.Join(";", values);
        if (!header.Contains("partial-results", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var organizations = header.Split(';').Select(p => p.Trim())
            .FirstOrDefault(p => p.StartsWith("organizations=", StringComparison.OrdinalIgnoreCase));
        return organizations is null
            ? []
            : organizations["organizations=".Length..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(id => long.TryParse(id, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : 0)
                .Where(id => id > 0)
                .ToList();
    }

    /// <summary>The rel="next" URL from a Link header, if it points at the API host.</summary>
    public Uri? NextLink(HttpResponseHeaders headers)
    {
        if (!headers.TryGetValues("Link", out var values))
        {
            return null;
        }

        foreach (var part in string.Join(",", values).Split(','))
        {
            var segments = part.Split(';');
            if (segments.Length < 2 || !segments.Skip(1).Any(s => s.Trim() is "rel=\"next\"" or "rel=next"))
            {
                continue;
            }

            var target = segments[0].Trim().TrimStart('<').TrimEnd('>');
            if (Uri.TryCreate(target, UriKind.Absolute, out var uri)
                && uri.Scheme == Uri.UriSchemeHttps
                && string.Equals(uri.Host, endpoints.ApiBase.Host, StringComparison.OrdinalIgnoreCase))
            {
                return uri;
            }
        }

        return null;
    }

    private static Uri WithPageSize(Uri uri)
    {
        if (uri.Query.Contains("per_page=", StringComparison.Ordinal))
        {
            return uri;
        }

        var separator = string.IsNullOrEmpty(uri.Query) ? "?" : "&";
        return new Uri(uri + separator + "per_page=" + PageSize);
    }

    private async Task<(HttpResponseMessage? Message, ResourceError? Error)> SendAsync(Func<string, HttpRequestMessage> createRequest, CancellationToken cancellationToken)
    {
        string? token;
        try
        {
            token = await tokens.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Auth.TokenUnavailableException ex)
        {
            // Renewal was due but GitHub couldn't be reached: a classified network error, never an exception.
            return (null, Error(ResourceErrorKind.Network, ex.Message));
        }

        if (token is null)
        {
            return (null, Error(ResourceErrorKind.Unauthorized, "Not signed in to GitHub."));
        }

        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage message;
            try
            {
                using var request = createRequest(token);
                message = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                return (null, Error(ResourceErrorKind.Network, $"Couldn't reach GitHub: {ex.Message}"));
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return (null, Error(ResourceErrorKind.Timeout, "GitHub took too long to respond."));
            }

            if (message.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                message.Dispose();
                try
                {
                    token = await tokens.HandleUnauthorizedAsync(token, cancellationToken).ConfigureAwait(false);
                }
                catch (Auth.TokenUnavailableException ex)
                {
                    return (null, Error(ResourceErrorKind.Network, ex.Message));
                }

                if (token is null)
                {
                    return (null, Error(ResourceErrorKind.Unauthorized, "GitHub no longer accepts this sign-in."));
                }

                continue;
            }

            if (message.IsSuccessStatusCode)
            {
                return (message, null);
            }

            var error = Classify(message);
            message.Dispose();
            return (null, error);
        }
    }

    private async Task<ApiResult<T>> ReadAsync<T>(HttpResponseMessage message, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await message.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var value = await JsonSerializer.DeserializeAsync(stream, typeInfo, cancellationToken).ConfigureAwait(false);
            return value is null
                ? ApiResult<T>.Fail(Error(ResourceErrorKind.InvalidResponse, "GitHub returned an empty response."))
                : ApiResult<T>.Ok(value);
        }
        catch (JsonException)
        {
            return ApiResult<T>.Fail(Error(ResourceErrorKind.InvalidResponse, "GitHub returned a response Repo Watch couldn't read."));
        }
        catch (HttpRequestException ex)
        {
            return ApiResult<T>.Fail(Error(ResourceErrorKind.Network, $"The connection to GitHub was interrupted: {ex.Message}"));
        }
    }

    private ResourceError Classify(HttpResponseMessage message)
    {
        var status = (int)message.StatusCode;
        var now = time.GetUtcNow();

        if (message.Headers.TryGetValues("X-GitHub-SSO", out var sso))
        {
            // e.g. "required; url=https://github.com/orgs/acme/sso?authorization_request=..."
            var header = string.Join(",", sso);
            var urlPart = header.Split(';').Select(p => p.Trim()).FirstOrDefault(p => p.StartsWith("url=", StringComparison.Ordinal));
            Uri? url = urlPart is not null && Uri.TryCreate(urlPart[4..], UriKind.Absolute, out var parsed) && parsed.Scheme == Uri.UriSchemeHttps ? parsed : null;
            return new ResourceError(ResourceErrorKind.SsoRequired, "This organization requires single sign-on (SAML) for your account.", now) { ActionUrl = url };
        }

        var retryAt = RetryAt(message, now);
        var rateLimited = message.StatusCode == HttpStatusCode.TooManyRequests
            || (message.StatusCode == HttpStatusCode.Forbidden
                && (retryAt is not null || Header(message, "x-ratelimit-remaining") == "0"));
        if (rateLimited)
        {
            return new ResourceError(ResourceErrorKind.RateLimited, "GitHub's rate limit was reached; Repo Watch will wait before trying again.", now) { RetryAt = retryAt };
        }

        return message.StatusCode switch
        {
            HttpStatusCode.Unauthorized => Error(ResourceErrorKind.Unauthorized, "GitHub no longer accepts this sign-in."),
            HttpStatusCode.Forbidden => Error(ResourceErrorKind.Forbidden, "GitHub denied access (403)."),
            HttpStatusCode.NotFound => Error(ResourceErrorKind.NotFound, "GitHub returned 404: not found, or not accessible to Repo Watch."),
            _ when status >= 500 => Error(ResourceErrorKind.ServerError, $"GitHub had a problem ({status})."),
            _ => Error(ResourceErrorKind.Unknown, $"GitHub returned {status}."),
        };
    }

    private static DateTimeOffset? RetryAt(HttpResponseMessage message, DateTimeOffset now)
    {
        if (message.Headers.RetryAfter is { } retryAfter)
        {
            return retryAfter.Delta is { } delta ? now + delta : retryAfter.Date;
        }

        return long.TryParse(Header(message, "x-ratelimit-reset"), out var epoch) && Header(message, "x-ratelimit-remaining") == "0"
            ? DateTimeOffset.FromUnixTimeSeconds(epoch)
            : null;
    }

    private static string? Header(HttpResponseMessage message, string name) =>
        message.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private ResourceError Error(ResourceErrorKind kind, string message) => new(kind, message, time.GetUtcNow());
}
