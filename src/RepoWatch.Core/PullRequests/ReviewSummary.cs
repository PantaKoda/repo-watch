namespace RepoWatch.Core.PullRequests;

/// <summary>
/// Review outcomes and outstanding requests, kept separate from checks and mergeability.
/// Approvals do not imply the pull request can be merged.
/// </summary>
public sealed record ReviewSummary
{
    /// <summary>Each reviewer's latest decisive state (approved or changes requested).</summary>
    public IReadOnlyDictionary<string, ReviewState> LatestByReviewer { get; init; } = new Dictionary<string, ReviewState>();

    public IReadOnlyList<ReviewRequest> PendingRequests { get; init; } = [];

    /// <summary>Approvals submitted against a commit other than the current head.</summary>
    public int ApprovalsOnOlderCommits { get; init; }

    public int Approvals => LatestByReviewer.Values.Count(s => s == ReviewState.Approved);

    public int ChangesRequested => LatestByReviewer.Values.Count(s => s == ReviewState.ChangesRequested);

    /// <summary>
    /// Mirrors GitHub's rules: comments do not change a reviewer's standing decision,
    /// a dismissal clears it, and unsubmitted reviews are ignored.
    /// </summary>
    public static ReviewSummary From(IEnumerable<PullRequestReview> reviews, IEnumerable<ReviewRequest> pendingRequests, string headSha)
    {
        ArgumentNullException.ThrowIfNull(reviews);
        ArgumentNullException.ThrowIfNull(pendingRequests);

        var latest = new Dictionary<string, PullRequestReview>(StringComparer.OrdinalIgnoreCase);
        foreach (var review in reviews.OrderBy(r => r.SubmittedAt).ThenBy(r => r.Id))
        {
            switch (review.State)
            {
                case ReviewState.Approved or ReviewState.ChangesRequested:
                    latest[review.AuthorLogin] = review;
                    break;
                case ReviewState.Dismissed:
                    latest.Remove(review.AuthorLogin);
                    break;
            }
        }

        return new ReviewSummary
        {
            LatestByReviewer = latest.ToDictionary(p => p.Key, p => p.Value.State, StringComparer.OrdinalIgnoreCase),
            PendingRequests = pendingRequests.ToList(),
            ApprovalsOnOlderCommits = latest.Values.Count(r =>
                r.State == ReviewState.Approved && !string.Equals(r.CommitSha, headSha, StringComparison.OrdinalIgnoreCase)),
        };
    }
}
