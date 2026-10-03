using System.Text.RegularExpressions;

namespace RepoWatch.Core.Configuration;

public static partial class RepoWatchOptionsValidator
{
    // Bounds keep misconfiguration from causing request storms or a frozen widget.
    public const int MinIntervalSeconds = 5;
    public const int MaxIntervalSeconds = 3600;

    public static IReadOnlyList<ConfigurationError> Validate(RepoWatchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = new List<ConfigurationError>();

        ValidateHttpsUrl(errors, "GitHub:WebBaseUrl", options.GitHub.WebBaseUrl, required: true, allowLoopbackHttp: false);
        ValidateHttpsUrl(errors, "GitHub:ApiBaseUrl", options.GitHub.ApiBaseUrl, required: true, allowLoopbackHttp: false);

        if (options.GitHub.ClientId is { } clientId && !string.IsNullOrWhiteSpace(clientId) && !ClientIdPattern().IsMatch(clientId))
        {
            errors.Add(new("GitHub:ClientId",
                "is not a valid GitHub App client ID.",
                "Copy the Client ID from the GitHub App settings page (it looks like 'Iv23li...'). Do not use the App ID or a client secret."));
        }

        if (options.GitHub.AppSlug is { } slug && !string.IsNullOrWhiteSpace(slug) && !SlugPattern().IsMatch(slug))
        {
            errors.Add(new("GitHub:AppSlug",
                "is not a valid GitHub App slug.",
                "Use the lowercase name from the app's public URL, e.g. 'repo-watch' from https://github.com/apps/repo-watch."));
        }

        ValidateInterval(errors, "Polling:ActiveWorkflowSeconds", options.Polling.ActiveWorkflowSeconds);
        ValidateInterval(errors, "Polling:PullRequestSeconds", options.Polling.PullRequestSeconds);
        ValidateInterval(errors, "Polling:IssueSeconds", options.Polling.IssueSeconds);
        ValidateInterval(errors, "Polling:QuietSeconds", options.Polling.QuietSeconds);

        if (options.Cache.RetentionDays is < 1 or > 365)
        {
            errors.Add(new("Cache:RetentionDays", $"{options.Cache.RetentionDays} is outside the allowed range.", "Use a value between 1 and 365 days."));
        }

        if (options.Cache.MaxCachedResponses is < 100 or > 100_000)
        {
            errors.Add(new("Cache:MaxCachedResponses", $"{options.Cache.MaxCachedResponses} is outside the allowed range.", "Use a value between 100 and 100000."));
        }

        // Plain HTTP is accepted only for a relay on this machine during development.
        ValidateHttpsUrl(errors, "Relay:BaseUrl", options.Relay.BaseUrl, required: false, allowLoopbackHttp: true);

        return errors;
    }

    private static void ValidateHttpsUrl(List<ConfigurationError> errors, string key, string? value, bool required, bool allowLoopbackHttp)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            if (required)
            {
                errors.Add(new(key, "is empty.", "Remove the setting to use the default, or provide an absolute https:// URL."));
            }

            return;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            errors.Add(new(key, $"'{value}' is not an absolute URL.", "Provide an absolute https:// URL."));
            return;
        }

        var isHttps = uri.Scheme == Uri.UriSchemeHttps;
        var isLoopbackHttp = uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback;
        if (!isHttps && !(allowLoopbackHttp && isLoopbackHttp))
        {
            errors.Add(new(key, $"'{value}' must use https.",
                allowLoopbackHttp ? "Use https://, or http://localhost only for local development." : "Use an https:// URL."));
            return;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            errors.Add(new(key, "must not contain credentials, a query string or a fragment.", "Provide only the scheme, host and optional base path."));
        }
    }

    private static void ValidateInterval(List<ConfigurationError> errors, string key, int seconds)
    {
        if (seconds is < MinIntervalSeconds or > MaxIntervalSeconds)
        {
            errors.Add(new(key, $"{seconds} is outside the allowed range.",
                $"Use a value between {MinIntervalSeconds} and {MaxIntervalSeconds} seconds."));
        }
    }

    // GitHub App client IDs are 'Iv' followed by alphanumerics (e.g. Iv1.0123abcd..., Iv23li...).
    [GeneratedRegex(@"^Iv[0-9A-Za-z.]{6,40}$")]
    private static partial Regex ClientIdPattern();

    [GeneratedRegex(@"^[a-z0-9]+(?:-[a-z0-9]+)*$")]
    private static partial Regex SlugPattern();
}
