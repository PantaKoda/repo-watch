namespace RepoWatch.Core.Status;

/// <summary>
/// Outcome of a single workflow run, job, check run or commit status.
/// Each value is distinct on purpose: skipped, neutral and cancelled are not success,
/// and <see cref="Unknown"/> means the state could not be determined.
/// "No checks at all" is not an outcome; see <see cref="RollupState.NoChecks"/>.
/// </summary>
public enum CheckOutcome
{
    Unknown = 0,
    Queued,
    /// <summary>Waiting on something external, e.g. an environment approval or concurrency group.</summary>
    Waiting,
    Running,
    Success,
    Failure,
    TimedOut,
    Cancelled,
    Skipped,
    Neutral,
    ActionRequired,
    Stale,
}

public static class CheckOutcomeExtensions
{
    public static bool IsInProgress(this CheckOutcome outcome) =>
        outcome is CheckOutcome.Queued or CheckOutcome.Waiting or CheckOutcome.Running;

    public static bool IsFailure(this CheckOutcome outcome) =>
        outcome is CheckOutcome.Failure or CheckOutcome.TimedOut;
}
