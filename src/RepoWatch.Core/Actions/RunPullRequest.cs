using RepoWatch.Core.PullRequests;

namespace RepoWatch.Core.Actions;

/// <summary>The pull request a workflow run belongs to, with its title and link when Repo Watch has them.</summary>
public sealed record RunPullRequest(int Number, string? Title, Uri? HtmlUrl, int Others = 0)
{
    /// <summary>
    /// GitHub lists the pull requests of a run while they are open in the same repository. It lists none for
    /// pull requests from forks (or once the pull request closed), so a <c>pull_request</c> run is then matched
    /// to an open pull request by its commit, then by its branch together with the branch's owner (forks often
    /// share branch names like <c>main</c> or <c>patch-1</c>). <c>pull_request_target</c> runs are never matched:
    /// they run on the base branch, so their commit and branch are not the pull request's. Anything uncertain
    /// shows no pull request rather than a wrong one.
    /// </summary>
    public static RunPullRequest? For(WorkflowRun run, PullRequestsState? pullRequests)
    {
        ArgumentNullException.ThrowIfNull(run);
        var open = pullRequests?.Items.Select(e => e.PullRequest).ToList() ?? [];
        if (run.PullRequestNumbers.Count > 0)
        {
            var number = run.PullRequestNumbers[0];
            var known = open.FirstOrDefault(p => p.Number == number);
            return new RunPullRequest(number, known?.Title, known?.HtmlUrl ?? PullUrl(run, number), run.PullRequestNumbers.Count - 1);
        }

        if (run.Event != "pull_request")
        {
            return null; // a push, schedule or manual run, or pull_request_target (base branch): nothing to match
        }

        var match = open.Where(p => p.HeadSha == run.HeadSha).ToList() is [var byCommit] ? byCommit
            : run is { HeadBranch: { Length: > 0 } branch, HeadOwner: { Length: > 0 } owner }
                && open.Where(p => p.HeadRef == branch && string.Equals(p.HeadOwner, owner, StringComparison.OrdinalIgnoreCase)).ToList() is [var byBranch]
                ? byBranch
                : null;
        return match is null ? null : new RunPullRequest(match.Number, match.Title, match.HtmlUrl);
    }

    /// <summary>https://github.com/{owner}/{repo}/pull/{n}, from the run's own https://github.com/{owner}/{repo}/actions/runs/{id}.</summary>
    private static Uri? PullUrl(WorkflowRun run, int number)
    {
        var path = run.HtmlUrl.AbsolutePath;
        var actions = path.IndexOf("/actions/runs/", StringComparison.Ordinal);
        return run.HtmlUrl.Scheme == Uri.UriSchemeHttps && actions > 0
            ? new Uri(run.HtmlUrl, $"{path[..actions]}/pull/{number}")
            : null;
    }
}
