using RepoWatch.Core.Configuration;

namespace RepoWatch.GitHub;

/// <summary>Well-known GitHub web and API locations derived from validated configuration.</summary>
public sealed class GitHubEndpoints
{
    public GitHubEndpoints(GitHubOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        WebBase = WithTrailingSlash(options.WebBaseUrl);
        ApiBase = WithTrailingSlash(options.ApiBaseUrl);
        AppSlug = string.IsNullOrWhiteSpace(options.AppSlug) ? null : options.AppSlug;
    }

    public Uri WebBase { get; }

    public Uri ApiBase { get; }

    public string? AppSlug { get; }

    public Uri DeviceCode => new(WebBase, "login/device/code");

    public Uri AccessToken => new(WebBase, "login/oauth/access_token");

    /// <summary>Page where a user can review or revoke GitHub App authorizations.</summary>
    public Uri AuthorizationManagement => new(WebBase, "settings/apps/authorizations");

    /// <summary>Page that lets a user install the app or change its repository access; null if no slug is configured.</summary>
    public Uri? Installation => AppSlug is null ? null : new(WebBase, $"apps/{Uri.EscapeDataString(AppSlug)}/installations/new");

    private static Uri WithTrailingSlash(string url) => new(url.EndsWith('/') ? url : url + "/", UriKind.Absolute);
}
