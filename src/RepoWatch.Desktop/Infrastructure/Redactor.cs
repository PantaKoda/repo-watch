using System.Text.RegularExpressions;

namespace RepoWatch.Desktop.Infrastructure;

/// <summary>
/// Removes secrets from text leaving the app (diagnostics). Repo Watch never logs tokens or device codes;
/// this is a second line of defense for anything that slipped through, including Bearer headers in
/// exception messages.
/// </summary>
public static partial class Redactor
{
    public static string Redact(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        text = GitHubToken().Replace(text, "[redacted token]");
        text = BearerHeader().Replace(text, "${prefix}[redacted]");
        text = SecretField().Replace(text, "${name}${separator}[redacted]");
        return UserCode().Replace(text, "[redacted code]");
    }

    // ghp_ (classic PAT), gho_ (OAuth), ghu_ (user-to-server), ghs_ (server-to-server), ghr_ (refresh), and fine-grained PATs.
    [GeneratedRegex(@"\b(?:gh[opusr]_[A-Za-z0-9_]{16,}|github_pat_[A-Za-z0-9_]{20,})\b")]
    private static partial Regex GitHubToken();

    [GeneratedRegex(@"(?<prefix>\b(?:Bearer|token)\s+)[A-Za-z0-9._\-]{8,}", RegexOptions.IgnoreCase)]
    private static partial Regex BearerHeader();

    // "device_code": "...", access_token=..., refresh_token: ...
    [GeneratedRegex(@"(?<name>""?\b(?:device_code|access_token|refresh_token|client_secret|code)\b""?)(?<separator>\s*[:=]\s*""?)[^""&\s,}]+", RegexOptions.IgnoreCase)]
    private static partial Regex SecretField();

    // Device-flow user codes look like WDJB-MJHT.
    [GeneratedRegex(@"\b[A-Z0-9]{4}-[A-Z0-9]{4}\b")]
    private static partial Regex UserCode();
}
