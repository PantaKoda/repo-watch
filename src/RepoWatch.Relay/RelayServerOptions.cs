using System.Security.Cryptography;
using System.Text;

namespace RepoWatch.Relay;

/// <summary>
/// Relay configuration, bound from the "Relay" section (appsettings, environment variables such as
/// <c>Relay__WebhookSecret</c>, or a secret store). The webhook secret is the only secret: it must come
/// from deployment secret configuration and never from the repository.
/// </summary>
public sealed class RelayServerOptions
{
    public const string Section = "Relay";

    /// <summary>The GitHub App's webhook secret, used to verify X-Hub-Signature-256. Required.</summary>
    public string WebhookSecret { get; set; } = "";

    /// <summary>
    /// The Repo Watch GitHub App's numeric ID (public). Only installations of this app count when the relay
    /// checks a user's access, so a token issued to another app can't open a session. Required.
    /// </summary>
    public long AppId { get; set; }

    /// <summary>SQLite file for durable deliveries.</summary>
    public string DatabasePath { get; set; } = "relay.db";

    /// <summary>GitHub REST API, used to confirm who a desktop client is and which repositories it may see.</summary>
    public string GitHubApiBaseUrl { get; set; } = "https://api.github.com";

    /// <summary>Lifetime of a relay session; clients create a new one before it ends.</summary>
    public int SessionMinutes { get; set; } = 15;

    /// <summary>Events kept for replay after a reconnect; older gaps cause a "reset" (full refresh).</summary>
    public int ReplayCapacity { get; set; } = 1000;

    public int MaxDeliveryAttempts { get; set; } = 5;

    public int DeliveryRetentionDays { get; set; } = 7;

    public int KeepAliveSeconds { get; set; } = 20;

    /// <summary>Validation errors; the relay refuses to start with any.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (WebhookSecret.Length < 16)
        {
            errors.Add("Relay:WebhookSecret must be set to the GitHub App's webhook secret (at least 16 characters).");
        }

        if (!Uri.TryCreate(GitHubApiBaseUrl, UriKind.Absolute, out var api) || !(api.Scheme == Uri.UriSchemeHttps || (api.Scheme == Uri.UriSchemeHttp && api.IsLoopback)))
        {
            errors.Add("Relay:GitHubApiBaseUrl must be an https:// URL.");
        }

        if (AppId <= 0)
        {
            errors.Add("Relay:AppId must be set to the GitHub App's numeric App ID (shown on the app's settings page).");
        }

        if (SessionMinutes is < 1 or > 60)
        {
            errors.Add("Relay:SessionMinutes must be between 1 and 60.");
        }

        if (ReplayCapacity is < 10 or > 100_000)
        {
            errors.Add("Relay:ReplayCapacity must be between 10 and 100000.");
        }

        return errors;
    }
}

/// <summary>GitHub's webhook signature: HMAC-SHA256 of the raw request body with the webhook secret.</summary>
public static class WebhookSignature
{
    public const string Header = "X-Hub-Signature-256";

    /// <summary>Verifies <paramref name="header"/> ("sha256=&lt;hex&gt;") against the exact bytes received, in constant time.</summary>
    public static bool IsValid(string secret, ReadOnlySpan<byte> body, string? header)
    {
        if (string.IsNullOrEmpty(secret) || header is null || !header.StartsWith("sha256=", StringComparison.Ordinal))
        {
            return false;
        }

        byte[] expected;
        try
        {
            expected = Convert.FromHexString(header.AsSpan("sha256=".Length));
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body);
        return expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    public static string Sign(string secret, ReadOnlySpan<byte> body) =>
        "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body));
}
