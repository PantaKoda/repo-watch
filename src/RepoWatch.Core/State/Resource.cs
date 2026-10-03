namespace RepoWatch.Core.State;

public enum ResourceAvailability
{
    /// <summary>Never loaded in this session and nothing cached.</summary>
    Unknown = 0,
    Available,
    /// <summary>GitHub answered that the feature is off (e.g. Actions disabled, issues disabled).</summary>
    FeatureUnavailable,
    /// <summary>Access was lost; cached content is withheld until access is restored.</summary>
    AccessLost,
}

/// <summary>What the UI can say about a resource's data right now.</summary>
public enum Freshness
{
    /// <summary>No data yet and no failure: loading or not requested.</summary>
    NotLoaded,
    /// <summary>Confirmed by a successful refresh within the stale threshold.</summary>
    Fresh,
    /// <summary>Restored from local cache and not yet confirmed in this session.</summary>
    Cached,
    /// <summary>Has data, but it is older than the threshold or the latest refresh failed.</summary>
    Stale,
    /// <summary>No usable data and the latest refresh failed, or access was lost.</summary>
    Failed,
}

/// <summary>
/// One independently refreshed piece of repository data (Actions, pull requests, issues, ...).
/// A failure keeps the last good value, so one failing endpoint never erases another's data.
/// Transitions return new instances; the type is immutable.
/// </summary>
public sealed record Resource<T> where T : class
{
    public static Resource<T> NotLoaded { get; } = new();

    public T? Value { get; init; }

    public ResourceAvailability Availability { get; init; }

    /// <summary>True while <see cref="Value"/> came from the local cache rather than this session's refresh.</summary>
    public bool IsFromCache { get; init; }

    public DateTimeOffset? LastSuccessAt { get; init; }

    public DateTimeOffset? LastAttemptAt { get; init; }

    /// <summary>The error from the latest attempt; null after a successful refresh.</summary>
    public ResourceError? LastError { get; init; }

    public bool HasValue => Value is not null;

    public static Resource<T> FromCache(T value, DateTimeOffset lastSuccessAt)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new Resource<T>
        {
            Value = value,
            Availability = ResourceAvailability.Available,
            IsFromCache = true,
            LastSuccessAt = lastSuccessAt,
        };
    }

    public Resource<T> Succeeded(T value, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(value);
        return this with
        {
            Value = value,
            Availability = ResourceAvailability.Available,
            IsFromCache = false,
            LastSuccessAt = at,
            LastAttemptAt = at,
            LastError = null,
        };
    }

    /// <summary>Records a failed refresh. The previous value, if any, is kept and becomes stale.</summary>
    public Resource<T> Failed(ResourceError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return this with { LastAttemptAt = error.OccurredAt, LastError = error };
    }

    /// <summary>GitHub reported that the feature is not available; this is an answer, not an error.</summary>
    public Resource<T> FeatureUnavailable(DateTimeOffset at) => this with
    {
        Value = null,
        Availability = ResourceAvailability.FeatureUnavailable,
        IsFromCache = false,
        LastSuccessAt = at,
        LastAttemptAt = at,
        LastError = null,
    };

    /// <summary>Access was revoked or the repository became inaccessible: withhold cached private content.</summary>
    public Resource<T> AccessLost(ResourceError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return this with
        {
            Value = null,
            Availability = ResourceAvailability.AccessLost,
            IsFromCache = false,
            LastAttemptAt = error.OccurredAt,
            LastError = error,
        };
    }

    public Freshness GetFreshness(DateTimeOffset now, TimeSpan staleAfter)
    {
        if (Availability == ResourceAvailability.AccessLost)
        {
            return Freshness.Failed;
        }

        if (Value is null && Availability != ResourceAvailability.FeatureUnavailable)
        {
            return LastError is null ? Freshness.NotLoaded : Freshness.Failed;
        }

        if (IsFromCache)
        {
            return Freshness.Cached;
        }

        if (LastError is not null && (LastSuccessAt is null || LastError.OccurredAt >= LastSuccessAt))
        {
            return Freshness.Stale;
        }

        return LastSuccessAt is { } success && now - success <= staleAfter ? Freshness.Fresh : Freshness.Stale;
    }
}
