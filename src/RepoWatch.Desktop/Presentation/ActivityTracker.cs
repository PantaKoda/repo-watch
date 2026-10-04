using System.Globalization;
using RepoWatch.Core.Identity;
using RepoWatch.Core.Monitoring;
using RepoWatch.Core.State;
using RepoWatch.Core.Status;

namespace RepoWatch.Desktop.Presentation;

/// <summary>
/// Remembers, per repository, what the user last saw at a glance (open PRs and issues, their comments, running
/// work, CI state, latest run) and whether it changed since they last opened it. It lives in the widget, not in the row, so
/// rows recreated by the filters keep their marker, and repositories the filters hide are still tracked.
/// <para>
/// The baseline is taken from live data only: values restored from the local cache at startup don't count, so
/// starting the app never lights everything up. A section that keeps failing still counts as settled, so the
/// other sections' changes are noticed.
/// </para>
/// </summary>
public sealed class ActivityTracker
{
    private readonly Dictionary<RepositoryKey, (string Fingerprint, bool Unseen)> _seen = [];

    /// <summary>
    /// Records <paramref name="snapshot"/>. Returns true when it differs from the last settled state (not for the
    /// first one, which is the silent baseline). While <paramref name="watching"/> (its details are open), a
    /// change is shown but not left as "unseen".
    /// </summary>
    public bool Observe(RepositoryKey key, RepositorySnapshot snapshot, bool watching)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (Fingerprint(snapshot) is not { } fingerprint)
        {
            return false; // still loading, or only cached data so far: no baseline yet
        }

        if (!_seen.TryGetValue(key, out var last))
        {
            _seen[key] = (fingerprint, false);
            return false;
        }

        var changed = last.Fingerprint != fingerprint;
        _seen[key] = (fingerprint, !watching && (last.Unseen || changed));
        return changed;
    }

    public bool IsUnseen(RepositoryKey key) => _seen.TryGetValue(key, out var state) && state.Unseen;

    public void MarkSeen(RepositoryKey key)
    {
        if (_seen.TryGetValue(key, out var state))
        {
            _seen[key] = (state.Fingerprint, false);
        }
    }

    /// <summary>Forgets repositories that are no longer watched.</summary>
    public void Retain(IEnumerable<RepositoryKey> keys)
    {
        var keep = keys.ToHashSet();
        foreach (var key in _seen.Keys.Where(k => !keep.Contains(k)).ToList())
        {
            _seen.Remove(key);
        }
    }

    /// <summary>Another account or demo data: start over.</summary>
    public void Clear() => _seen.Clear();

    /// <summary>What matters at a glance, or null until every section has settled from live data.</summary>
    public static string? Fingerprint(RepositorySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!Settled(snapshot.Actions) || !Settled(snapshot.PullRequests) || !Settled(snapshot.Issues))
        {
            return null;
        }

        var runs = snapshot.Actions.Value?.RecentRuns ?? [];
        var running = runs.Count(r => r.Outcome is CheckOutcome.Queued or CheckOutcome.Waiting or CheckOutcome.Running);
        var latest = runs.OrderByDescending(r => r.UpdatedAt).FirstOrDefault();
        return string.Join("|",
            snapshot.PullRequests.Value?.OpenCount.ToString() ?? "-",
            snapshot.Issues.Value?.OpenCount.ToString() ?? "-",
            Comments(snapshot),
            running.ToString(CultureInfo.InvariantCulture),
            AttentionPolicy.Evaluate(snapshot).ToString(),
            latest is null ? "-" : string.Create(CultureInfo.InvariantCulture, $"{latest.Id}:{latest.RunAttempt}:{latest.Outcome}"));
    }

    /// <summary>
    /// New comments on listed pull requests and issues count as activity too. Each item's count is kept
    /// separately, so a comment added on one item and one deleted on another don't cancel out.
    /// </summary>
    private static string Comments(RepositorySnapshot snapshot) => string.Join(",",
        (snapshot.PullRequests.Value?.Items ?? []).Select(e => e.PullRequest).Where(p => p.CommentCount > 0).OrderBy(p => p.Number)
            .Select(p => string.Create(CultureInfo.InvariantCulture, $"p{p.Number}:{p.CommentCount}"))
        .Concat((snapshot.Issues.Value?.Items ?? []).Where(i => i.CommentCount > 0).OrderBy(i => i.Number)
            .Select(i => string.Create(CultureInfo.InvariantCulture, $"i{i.Number}:{i.CommentCount}"))));

    private static bool Settled<T>(Resource<T> resource) where T : class =>
        resource.Availability is ResourceAvailability.FeatureUnavailable or ResourceAvailability.AccessLost
        || resource.LastError is not null // keeps failing: settled as "failing" (Attention shows it), so others' changes count
        || (resource.Value is not null && !resource.IsFromCache);
}
