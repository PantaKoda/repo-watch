namespace RepoWatch.Core.Platform;

/// <summary>Opens a URL in the user's system browser.</summary>
public interface IExternalBrowser
{
    /// <returns>False if the URL was rejected by <see cref="ExternalLinkPolicy"/> or could not be launched.</returns>
    Task<bool> OpenAsync(Uri url);
}

/// <summary>
/// Links come from API responses and repository content, so they are untrusted. Only HTTPS URLs
/// on the configured GitHub web host (or its subdomains, e.g. docs) are opened.
/// </summary>
public static class ExternalLinkPolicy
{
    public static bool IsAllowed(Uri? url, Uri gitHubWebBase)
    {
        ArgumentNullException.ThrowIfNull(gitHubWebBase);
        if (url is null || !url.IsAbsoluteUri || url.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(url.UserInfo))
        {
            return false;
        }

        var host = url.IdnHost;
        var allowed = gitHubWebBase.IdnHost;
        return (string.Equals(host, allowed, StringComparison.OrdinalIgnoreCase)
                || host.EndsWith("." + allowed, StringComparison.OrdinalIgnoreCase))
            && url.Port == gitHubWebBase.Port;
    }
}
