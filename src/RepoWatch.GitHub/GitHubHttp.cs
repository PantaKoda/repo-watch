using System.Net.Http.Headers;
using System.Reflection;

namespace RepoWatch.GitHub;

/// <summary>Shared HTTP settings for GitHub requests.</summary>
public static class GitHubHttp
{
    /// <summary>REST API version requested by every API call (verified against GitHub docs, October 2026).</summary>
    public const string ApiVersion = "2026-03-10";

    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    public static string UserAgent { get; } =
        "RepoWatch/" + (typeof(GitHubHttp).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0");

    /// <summary>One long-lived client for the process; connections are recycled for DNS changes.</summary>
    public static HttpClient CreateClient(HttpMessageHandler? handler = null)
    {
        var client = new HttpClient(handler ?? new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        {
            Timeout = RequestTimeout,
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return client;
    }

    /// <summary>Builds an authenticated REST API request. The token goes only in the Authorization header.</summary>
    public static HttpRequestMessage ApiRequest(HttpMethod method, Uri uri, string accessToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(accessToken);
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", ApiVersion);
        return request;
    }
}
