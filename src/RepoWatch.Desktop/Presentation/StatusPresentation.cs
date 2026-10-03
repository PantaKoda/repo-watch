using System.Globalization;
using RepoWatch.Core.Monitoring;
using RepoWatch.Core.PullRequests;
using RepoWatch.Core.Status;

namespace RepoWatch.Desktop.Presentation;

/// <summary>Visual tone of a status indicator. Always paired with a text label, never color alone.</summary>
public enum StatusTone
{
    None,
    Unknown,
    Neutral,
    Running,
    Success,
    Warning,
    Failure,
}

public static class StatusPresentation
{
    public static StatusTone Tone(CheckOutcome outcome) => outcome switch
    {
        CheckOutcome.Success => StatusTone.Success,
        CheckOutcome.Failure or CheckOutcome.TimedOut => StatusTone.Failure,
        CheckOutcome.ActionRequired => StatusTone.Warning,
        CheckOutcome.Queued or CheckOutcome.Waiting or CheckOutcome.Running => StatusTone.Running,
        CheckOutcome.Cancelled or CheckOutcome.Skipped or CheckOutcome.Neutral or CheckOutcome.Stale => StatusTone.Neutral,
        _ => StatusTone.Unknown,
    };

    public static string Label(CheckOutcome outcome) => outcome switch
    {
        CheckOutcome.Queued => "Queued",
        CheckOutcome.Waiting => "Waiting",
        CheckOutcome.Running => "Running",
        CheckOutcome.Success => "Succeeded",
        CheckOutcome.Failure => "Failed",
        CheckOutcome.TimedOut => "Timed out",
        CheckOutcome.Cancelled => "Cancelled",
        CheckOutcome.Skipped => "Skipped",
        CheckOutcome.Neutral => "Neutral",
        CheckOutcome.ActionRequired => "Action required",
        CheckOutcome.Stale => "Stale",
        _ => "Unknown",
    };

    public static StatusTone Tone(RollupState state) => state switch
    {
        RollupState.Passing => StatusTone.Success,
        RollupState.Failing => StatusTone.Failure,
        RollupState.ActionRequired => StatusTone.Warning,
        RollupState.Pending => StatusTone.Running,
        RollupState.Cancelled or RollupState.Neutral => StatusTone.Neutral,
        RollupState.NoChecks => StatusTone.None,
        _ => StatusTone.Unknown,
    };

    public static string Label(RollupState state) => state switch
    {
        RollupState.Passing => "Passing",
        RollupState.Failing => "Failing",
        RollupState.ActionRequired => "Action required",
        RollupState.Pending => "In progress",
        RollupState.Cancelled => "Cancelled",
        RollupState.Neutral => "Skipped or neutral",
        RollupState.NoChecks => "No checks",
        _ => "Unknown",
    };

    public static StatusTone Tone(AttentionLevel level) => level switch
    {
        AttentionLevel.Failure => StatusTone.Failure,
        AttentionLevel.Warning => StatusTone.Warning,
        AttentionLevel.Active => StatusTone.Running,
        _ => StatusTone.None,
    };

    /// <summary>Merge state as GitHub reports it. Never phrased as "ready to merge".</summary>
    public static string Label(MergeState state) => state switch
    {
        MergeState.Clean => "No conflicts",
        MergeState.Conflicting => "Conflicts",
        MergeState.Blocked => "Merge blocked",
        MergeState.Behind => "Behind base",
        MergeState.Unstable => "Some checks failing",
        MergeState.HasHooks => "Has merge hooks",
        MergeState.Draft => "Draft",
        _ => "Merge state unknown",
    };

    /// <summary>"Rate limited · until 14:30" or "Offline · retry 14:05": what happens next, not just the state.</summary>
    public static string WithRetry(string label, bool rateLimited, DateTimeOffset? retryAt)
    {
        var at = retryAt?.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture);
        return rateLimited && at is not null ? $"Rate limited · until {at}"
            : at is not null ? $"{label} · retry {at}"
            : label;
    }

    public static (string Label, StatusTone Tone) Connection(ConnectionState state) => state switch
    {
        ConnectionState.Demo => ("Demo data", StatusTone.Neutral),
        ConnectionState.Connecting => ("Connecting…", StatusTone.Unknown),
        ConnectionState.SignedInIdle => ("Signed in", StatusTone.Neutral),
        ConnectionState.Polling => ("Polling", StatusTone.Success),
        ConnectionState.Live => ("Live", StatusTone.Success),
        ConnectionState.Offline => ("Offline", StatusTone.Warning),
        ConnectionState.Paused => ("Paused", StatusTone.Neutral),
        ConnectionState.ReconnectRequired => ("Reconnect required", StatusTone.Failure),
        _ => ("Not signed in", StatusTone.Unknown),
    };
}

public static class TimeText
{
    public static string Ago(DateTimeOffset at, DateTimeOffset now)
    {
        var elapsed = now - at;
        if (elapsed < TimeSpan.FromSeconds(45))
        {
            return "just now";
        }

        if (elapsed < TimeSpan.FromHours(1))
        {
            return $"{Math.Max(1, (int)elapsed.TotalMinutes)}m ago";
        }

        if (elapsed < TimeSpan.FromDays(1))
        {
            return $"{(int)elapsed.TotalHours}h ago";
        }

        return elapsed < TimeSpan.FromDays(30)
            ? $"{(int)elapsed.TotalDays}d ago"
            : at.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture);
    }
}
