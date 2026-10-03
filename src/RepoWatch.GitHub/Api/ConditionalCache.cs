using System.Net.Http.Headers;

namespace RepoWatch.GitHub.Api;

/// <summary>A cached REST response body and the ETag it was served with.</summary>
public sealed record CachedResponse(string ETag, string Body);

/// <summary>
/// Stores ETags and bodies for conditional requests, for one account. A 304 Not Modified answer reuses
/// the cached body; GitHub does not count such answers against the primary rate limit when the request
/// is authorized, but they still take time and are not exempt from every limit (secondary limits apply).
/// Holds private repository content, so it must be isolated per account and cleared on sign-out.
/// </summary>
public interface IConditionalCache
{
    CachedResponse? Get(Uri uri);

    void Put(Uri uri, CachedResponse response);
}

/// <summary>
/// What GitHub reports about the account's request budget, per rate-limit resource ("core",
/// "graphql", ...). The scheduler slows down when any budget runs low, before GitHub refuses requests.
/// </summary>
public sealed class RateBudget(TimeProvider time)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (int Remaining, int Limit, DateTimeOffset ResetAt)> _resources = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Share of a budget below which polling slows down.</summary>
    public const double LowShare = 0.1;

    /// <summary>True while some resource has less than <see cref="LowShare"/> of its budget left before its reset.</summary>
    public bool IsLow
    {
        get
        {
            var now = time.GetUtcNow();
            lock (_gate)
            {
                return _resources.Values.Any(r => r.ResetAt > now && r.Limit > 0 && r.Remaining < r.Limit * LowShare);
            }
        }
    }

    public void Observe(HttpResponseHeaders headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        if (!int.TryParse(Header(headers, "x-ratelimit-remaining"), out var remaining)
            || !int.TryParse(Header(headers, "x-ratelimit-limit"), out var limit)
            || !long.TryParse(Header(headers, "x-ratelimit-reset"), out var reset))
        {
            return;
        }

        var resource = Header(headers, "x-ratelimit-resource") ?? "core";
        lock (_gate)
        {
            _resources[resource] = (remaining, limit, DateTimeOffset.FromUnixTimeSeconds(reset));
        }
    }

    private static string? Header(HttpResponseHeaders headers, string name) =>
        headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
}
