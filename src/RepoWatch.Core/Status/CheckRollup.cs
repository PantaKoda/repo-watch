namespace RepoWatch.Core.Status;

/// <summary>Combined state of a set of checks for one commit.</summary>
public enum RollupState
{
    /// <summary>The checks could not be determined (not loaded, or an outcome is unknown).</summary>
    Unknown = 0,
    /// <summary>Loaded successfully and there are no checks for this commit.</summary>
    NoChecks,
    Pending,
    /// <summary>Every check finished and at least one succeeded; none failed.</summary>
    Passing,
    Failing,
    ActionRequired,
    Cancelled,
    /// <summary>Every check was skipped or neutral; nothing actually passed.</summary>
    Neutral,
}

/// <summary>
/// A rollup keeps per-outcome counts so the UI can show detail behind the summary state.
/// A passing rollup says nothing about mergeability or other outstanding work.
/// </summary>
public sealed record CheckRollup(RollupState State, IReadOnlyDictionary<CheckOutcome, int> Counts)
{
    public static CheckRollup Unknown { get; } = new(RollupState.Unknown, new Dictionary<CheckOutcome, int>());

    public int Total => Counts.Values.Sum();

    public int Count(CheckOutcome outcome) => Counts.GetValueOrDefault(outcome);

    public static CheckRollup From(IEnumerable<CheckOutcome> outcomes)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        var counts = outcomes.GroupBy(o => o).ToDictionary(g => g.Key, g => g.Count());
        return new CheckRollup(Combine(counts), counts);
    }

    // Precedence: a finished negative result is known even while other checks still run.
    private static RollupState Combine(Dictionary<CheckOutcome, int> counts)
    {
        bool Any(params CheckOutcome[] outcomes) => outcomes.Any(counts.ContainsKey);

        if (counts.Count == 0)
        {
            return RollupState.NoChecks;
        }

        if (Any(CheckOutcome.Failure, CheckOutcome.TimedOut))
        {
            return RollupState.Failing;
        }

        if (Any(CheckOutcome.ActionRequired))
        {
            return RollupState.ActionRequired;
        }

        if (Any(CheckOutcome.Cancelled))
        {
            return RollupState.Cancelled;
        }

        if (Any(CheckOutcome.Queued, CheckOutcome.Waiting, CheckOutcome.Running))
        {
            return RollupState.Pending;
        }

        if (Any(CheckOutcome.Unknown, CheckOutcome.Stale))
        {
            return RollupState.Unknown;
        }

        return Any(CheckOutcome.Success) ? RollupState.Passing : RollupState.Neutral;
    }
}
