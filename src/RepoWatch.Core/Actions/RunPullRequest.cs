using RepoWatch.Core.PullRequests;

namespace RepoWatch.Core.Actions;

/// <summary>The pull request a workflow run belongs to, with its title and link when Repo Watch has them.</summary>
public sealed record RunPullRequest(int Number, string? Title, Uri? HtmlUrl, int Others = 0)
{
    /// <summary>
    /// GitHub lists the pull requests of a run while they are open in the same repository. It lists none for
    /// pull requests from forks (or once the pull request closed), so a pull request run is then matched to
    /// an open pull request by its commit, then by its branch. Null when there is no pull request to show.
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

        if (!run.Event.StartsWith("pull_request", StringComparison.Ordinal))
        {
            return null; // a push, schedule or manual run: no pull request triggered it
        }

        var match = open.FirstOrDefault(p => p.HeadSha == run.HeadSha)
            ?? (run.HeadBranch is { Length: > 0 } branch ? open.FirstOrDefault(p => p.HeadRef == branch) : null);
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
