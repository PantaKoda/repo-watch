using System.Globalization;
using RepoWatch.Core.Identity;
using RepoWatch.Core.PullRequests;
using RepoWatch.Core.Settings;
using RepoWatch.Core.State;
using RepoWatch.Core.Status;

namespace RepoWatch.Core.Notifications;

public enum NotificationKind
{
    CiFailure,
    CiRecovery,
    ReviewRequested,
    PullRequestMerged,
}

/// <summary>
/// Something worth one notification. <see cref="Key"/> identifies the event itself (repository, entity,
/// commit or run attempt, transition), so the same event is never announced twice, also across restarts.
/// <see cref="IsBaseline"/> marks events that were already true when Repo Watch first saw the data: they are
/// recorded silently instead of announced, so starting up never produces a burst of old news.
/// Titles are untrusted repository content: notification text is plain text only.
/// </summary>
public sealed record NotificationEvent(
    NotificationKind Kind,
    RepositoryKey Repository,
    string Key,
    string RepositoryName,
    string Subject,
    Uri Url,
    bool IsPrivate,
    bool IsBaseline);

/// <summary>The text actually shown, after privacy settings.</summary>
public sealed record NotificationText(string Title, string Body);

public static class NotificationPolicy
{
    /// <summary>
    /// Events implied by moving from <paramref name="previous"/> to <paramref name="current"/>. A section
    /// that had no data before (first observation) yields baseline events only.
    /// <list type="bullet">
    /// <item>CI failure: a tracked branch's current commit is failing. Keyed by commit and the failing runs'
    /// attempts, so a re-run that fails again is a new event, while an old failed run reappearing in a list
    /// is not (only the branch head counts).</item>
    /// <item>CI recovery: a tracked branch was failing and its current commit now passes.</item>
    /// <item>Review requested: a pull request asks <paramref name="login"/> directly for review (team
    /// requests are not "for me" without established membership). Keyed by head commit.</item>
    /// <item>Merged: a pull request that was in the tracked list is now among the recently merged.</item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<NotificationEvent> Detect(RepositorySnapshot? previous, RepositorySnapshot current, string login)
    {
        ArgumentNullException.ThrowIfNull(current);
        var events = new List<NotificationEvent>();
        if (current.Metadata.Value is not { } metadata)
        {
            return events;
        }

        var id = current.Key.RepositoryId.ToString(CultureInfo.InvariantCulture);
        var name = metadata.FullName;

        if (current.Actions.Value is { } actions)
        {
            var ciBaseline = previous?.Actions.Value is null;
            var before = previous?.Actions.Value?.TrackedBranches.GroupBy(b => b.Branch ?? "", StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal) ?? [];
            foreach (var branch in actions.TrackedBranches)
            {
                var branchName = branch.Branch ?? metadata.DefaultBranch;
                if (branch.Rollup.State is RollupState.Failing)
                {
                    var failing = branch.Runs.Where(r => r.Outcome is CheckOutcome.Failure or CheckOutcome.TimedOut).OrderBy(r => r.Id).ToList();
                    var attempts = string.Join(",", failing.Select(r => string.Create(CultureInfo.InvariantCulture, $"{r.Id}.{r.RunAttempt}")));
                    var url = failing.FirstOrDefault()?.HtmlUrl ?? new Uri(metadata.HtmlUrl, "actions");
                    events.Add(new NotificationEvent(NotificationKind.CiFailure, current.Key, $"ci-failure:{id}:{branchName}:{branch.HeadSha}:{attempts}",
                        name, branchName, url, metadata.IsPrivate, ciBaseline));
                }
                else if (branch.Rollup.State is RollupState.Passing
                    && before.TryGetValue(branch.Branch ?? "", out var earlier) && earlier.Rollup.State is RollupState.Failing)
                {
                    events.Add(new NotificationEvent(NotificationKind.CiRecovery, current.Key, $"ci-recovery:{id}:{branchName}:{branch.HeadSha}",
                        name, branchName, branch.Runs.FirstOrDefault()?.HtmlUrl ?? new Uri(metadata.HtmlUrl, "actions"), metadata.IsPrivate, ciBaseline));
                }
            }
        }

        if (current.PullRequests.Value is { } pulls)
        {
            var prBaseline = previous?.PullRequests.Value is null;
            foreach (var entry in pulls.Items.Where(e => e.PullRequest.RequestedReviewers.Any(r =>
                r.Kind == ReviewerKind.User && string.Equals(r.Login, login, StringComparison.OrdinalIgnoreCase))))
            {
                var pr = entry.PullRequest;
                events.Add(new NotificationEvent(NotificationKind.ReviewRequested, current.Key,
                    string.Create(CultureInfo.InvariantCulture, $"review:{id}:{pr.Number}:{pr.HeadSha}"),
                    name, string.Create(CultureInfo.InvariantCulture, $"#{pr.Number} {pr.Title}"), pr.HtmlUrl, metadata.IsPrivate, prBaseline));
            }

            var tracked = previous?.PullRequests.Value?.Items.Select(e => e.PullRequest.Number).ToHashSet() ?? [];
            foreach (var merged in pulls.RecentlyMerged.Where(m => tracked.Contains(m.Number)))
            {
                events.Add(new NotificationEvent(NotificationKind.PullRequestMerged, current.Key,
                    string.Create(CultureInfo.InvariantCulture, $"merged:{id}:{merged.Number}"),
                    name, string.Create(CultureInfo.InvariantCulture, $"#{merged.Number} {merged.Title}"), merged.HtmlUrl, metadata.IsPrivate, prBaseline));
            }
        }

        return events;
    }

    /// <summary>Whether the user's settings allow announcing this event now (quiet hours are checked separately).</summary>
    public static bool IsWanted(NotificationEvent notification, NotificationSettings settings, bool repositoryEnabled)
    {
        ArgumentNullException.ThrowIfNull(notification);
        ArgumentNullException.ThrowIfNull(settings);
        return settings.Enabled && repositoryEnabled && notification.Kind switch
        {
            NotificationKind.CiFailure => settings.CiFailure,
            NotificationKind.CiRecovery => settings.CiRecovery,
            NotificationKind.ReviewRequested => settings.ReviewRequested,
            _ => settings.PullRequestMerged,
        };
    }

    /// <summary>Notification text. With private details hidden, a private repository's name and titles are left out.</summary>
    public static NotificationText Text(NotificationEvent notification, bool hidePrivateDetails)
    {
        ArgumentNullException.ThrowIfNull(notification);
        var hide = hidePrivateDetails && notification.IsPrivate;
        var where = hide ? "a private repository" : notification.RepositoryName;
        return notification.Kind switch
        {
            NotificationKind.CiFailure => new($"CI failing in {where}", hide ? "A tracked branch is failing." : $"{notification.Subject} is failing."),
            NotificationKind.CiRecovery => new($"CI recovered in {where}", hide ? "A tracked branch passes again." : $"{notification.Subject} passes again."),
            NotificationKind.ReviewRequested => new($"Review requested in {where}", hide ? "A pull request asks for your review." : notification.Subject),
            _ => new($"Pull request merged in {where}", hide ? "A pull request you track was merged." : notification.Subject),
        };
    }
}
