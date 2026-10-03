using RepoWatch.Core.Accounts;

namespace RepoWatch.GitHub.Auth;

public enum DeviceFlowOutcome
{
    Authorized,
    Denied,
    Expired,
    Cancelled,
    /// <summary>GitHub rejected the request; see <see cref="DeviceFlowResult.Error"/> (e.g. device_flow_disabled).</summary>
    Rejected,
}

public sealed record DeviceFlowResult(DeviceFlowOutcome Outcome, StoredCredential? Credential = null, string? Error = null);

public enum DeviceFlowProgressKind
{
    /// <summary>Waiting for the user to enter the code and approve.</summary>
    WaitingForUser,
    /// <summary>GitHub asked us to poll less often.</summary>
    SlowedDown,
    /// <summary>A poll failed for a transient reason; polling continues until the code expires.</summary>
    RetryingAfterError,
}

public sealed record DeviceFlowProgress(DeviceFlowProgressKind Kind, TimeSpan Interval, string? Detail = null);

/// <summary>
/// Polls for the user's approval. Never polls faster than GitHub's interval, adds the
/// slow-down increase, stops at expiry without further requests, and stops immediately on cancellation.
/// </summary>
public sealed class DeviceFlowSignIn(DeviceFlowClient client, TimeProvider time)
{
    public static readonly TimeSpan SlowDownIncrement = TimeSpan.FromSeconds(5);

    public async Task<DeviceFlowResult> WaitForAuthorizationAsync(
        DeviceAuthorization authorization, IProgress<DeviceFlowProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        var interval = authorization.Interval;
        progress?.Report(new DeviceFlowProgress(DeviceFlowProgressKind.WaitingForUser, interval));

        try
        {
            while (true)
            {
                var remaining = authorization.ExpiresAt - time.GetUtcNow();
                if (remaining <= TimeSpan.Zero)
                {
                    return new DeviceFlowResult(DeviceFlowOutcome.Expired);
                }

                await Task.Delay(interval < remaining ? interval : remaining, time, cancellationToken).ConfigureAwait(false);
                if (time.GetUtcNow() >= authorization.ExpiresAt)
                {
                    return new DeviceFlowResult(DeviceFlowOutcome.Expired);
                }

                TokenPollResult poll;
                try
                {
                    poll = await client.PollAsync(authorization.DeviceCode, cancellationToken).ConfigureAwait(false);
                }
                catch (GitHubTransientException ex)
                {
                    progress?.Report(new DeviceFlowProgress(DeviceFlowProgressKind.RetryingAfterError, interval, ex.Message));
                    continue;
                }

                switch (poll.Status)
                {
                    case TokenPollStatus.Success:
                        return new DeviceFlowResult(DeviceFlowOutcome.Authorized, poll.Credential);
                    case TokenPollStatus.Pending:
                        break;
                    case TokenPollStatus.SlowDown:
                        interval = poll.NewInterval is { } newInterval && newInterval > interval ? newInterval : interval + SlowDownIncrement;
                        progress?.Report(new DeviceFlowProgress(DeviceFlowProgressKind.SlowedDown, interval));
                        break;
                    case TokenPollStatus.Denied:
                        return new DeviceFlowResult(DeviceFlowOutcome.Denied);
                    case TokenPollStatus.Expired:
                        return new DeviceFlowResult(DeviceFlowOutcome.Expired);
                    default:
                        return new DeviceFlowResult(DeviceFlowOutcome.Rejected, Error: poll.Error);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new DeviceFlowResult(DeviceFlowOutcome.Cancelled);
        }
    }
}
