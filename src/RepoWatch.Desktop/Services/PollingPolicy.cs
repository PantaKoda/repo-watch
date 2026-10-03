using RepoWatch.Core.Configuration;

namespace RepoWatch.Desktop.Services;

/// <summary>The independently scheduled parts of a repository's data.</summary>
[Flags]
public enum RefreshParts
{
    None = 0,
    Metadata = 1,
    Actions = 2,
    PullRequests = 4,
    Issues = 8,
    All = Metadata | Actions | PullRequests | Issues,
}

/// <summary>
/// Refresh targets. Server answers always win: a rate limit, Retry-After or a low request budget
/// stretch or pause these; they are never latency guarantees.
/// </summary>
public sealed record PollingIntervals
{
    /// <summary>Actions while something is queued or running, and the repository whose details are open.</summary>
    public TimeSpan Active { get; init; } = TimeSpan.FromSeconds(20);

    public TimeSpan PullRequests { get; init; } = TimeSpan.FromSeconds(90);

    public TimeSpan Issues { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>Actions with nothing running, and repository metadata.</summary>
    public TimeSpan Quiet { get; init; } = TimeSpan.FromSeconds(180);

    /// <summary>First retry after a failure; doubles with each further failure.</summary>
    public TimeSpan Failed { get; init; } = TimeSpan.FromSeconds(60);

    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Longest wait for a rate-limit reset before trying again.</summary>
    public TimeSpan MaxRateLimitWait { get; init; } = TimeSpan.FromHours(1);

    public static PollingIntervals From(PollingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new PollingIntervals
        {
            Active = TimeSpan.FromSeconds(options.ActiveWorkflowSeconds),
            PullRequests = TimeSpan.FromSeconds(options.PullRequestSeconds),
            Issues = TimeSpan.FromSeconds(options.IssueSeconds),
            Quiet = TimeSpan.FromSeconds(options.QuietSeconds),
        };
    }
}

/// <summary>Pure scheduling rules, kept separate from the polling loop so they can be tested directly.</summary>
public static class PollingPolicy
{
    private static readonly TimeSpan Longest = TimeSpan.FromHours(1);

    /// <summary>Base interval of one part before any slow-down.</summary>
    public static TimeSpan Interval(RefreshParts part, bool active, bool focused, PollingIntervals intervals)
    {
        ArgumentNullException.ThrowIfNull(intervals);
        return part switch
        {
            RefreshParts.Actions => active || focused ? intervals.Active : intervals.Quiet,
            RefreshParts.PullRequests => focused ? intervals.Active : intervals.PullRequests,
            RefreshParts.Issues => focused ? Min(intervals.PullRequests, intervals.Issues) : intervals.Issues,
            _ => intervals.Quiet,
        };
    }

    /// <summary>
    /// How much slower to poll: hidden widget ×3, battery power ×2, low request budget ×2 (at most ×8).
    /// Visible, plugged-in polling with a healthy budget is ×1.
    /// </summary>
    public static double Slowdown(bool widgetVisible, bool onBattery, bool budgetLow) =>
        Math.Min(8, (widgetVisible ? 1 : 3) * (onBattery ? 2 : 1) * (budgetLow ? 2 : 1));

    public static TimeSpan Scale(TimeSpan interval, double factor) =>
        Min(TimeSpan.FromTicks((long)(interval.Ticks * factor)), Longest);

    /// <summary>
    /// Bounded exponential backoff with jitter: <c>Failed × 2^(failures-1)</c>, at most <c>MaxBackoff</c>,
    /// then ±20% so many clients (or repositories) don't retry in lockstep.
    /// </summary>
    /// <param name="jitter">A value in [0, 1).</param>
    public static TimeSpan Backoff(int failures, PollingIntervals intervals, double jitter)
    {
        ArgumentNullException.ThrowIfNull(intervals);
        ArgumentOutOfRangeException.ThrowIfLessThan(failures, 1);
        var exponent = Math.Min(failures - 1, 20);
        var raw = Min(TimeSpan.FromTicks((long)Math.Min(intervals.Failed.Ticks * Math.Pow(2, exponent), TimeSpan.MaxValue.Ticks / 2)), intervals.MaxBackoff);
        return TimeSpan.FromTicks((long)(raw.Ticks * (0.8 + (0.4 * Math.Clamp(jitter, 0, 1)))));
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
}
