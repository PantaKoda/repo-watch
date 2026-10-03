namespace RepoWatch.Core.Platform;

public enum LinkOpenResult
{
    Opened,
    /// <summary>Rejected by <see cref="ExternalLinkPolicy"/>.</summary>
    Refused,
    /// <summary>Allowed, but the system browser could not be launched.</summary>
    Failed,
}

/// <summary>Opens a URL in the user's system browser. Never throws; failures are reported in the result.</summary>
public interface IExternalBrowser
{
    Task<LinkOpenResult> OpenAsync(Uri url);
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
