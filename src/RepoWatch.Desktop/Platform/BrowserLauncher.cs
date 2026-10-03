using System.Diagnostics;
using Microsoft.Extensions.Logging;
using RepoWatch.Core.Platform;
using RepoWatch.GitHub;

namespace RepoWatch.Desktop.Platform;

/// <summary>Opens validated GitHub URLs in the system browser.</summary>
public sealed class BrowserLauncher(GitHubEndpoints endpoints, ILogger<BrowserLauncher> logger) : IExternalBrowser
{
    public Task<bool> OpenAsync(Uri url)
    {
        if (!ExternalLinkPolicy.IsAllowed(url, endpoints.WebBase))
        {
            logger.LogWarning("Refused to open a link outside {Host}", endpoints.WebBase.Host);
            return Task.FromResult(false);
        }

        try
        {
            // AbsoluteUri is percent-encoded, so it cannot inject shell arguments.
            var start = OperatingSystem.IsWindows() ? new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true }
                : OperatingSystem.IsMacOS() ? new ProcessStartInfo("open", [url.AbsoluteUri])
                : new ProcessStartInfo("xdg-open", [url.AbsoluteUri]);
            using var process = Process.Start(start);
            return Task.FromResult(true);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            logger.LogWarning(ex, "Could not launch the system browser");
            return Task.FromResult(false);
        }
    }
}
