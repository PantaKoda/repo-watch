using RepoWatch.Core.Actions;
using RepoWatch.Core.PullRequests;
using RepoWatch.Core.State;
using RepoWatch.Core.Status;

namespace RepoWatch.Core.Tests.Status;

public sealed class RunPullRequestTests
{
    private const string Sha = "1111111111111111111111111111111111111111";

    private static WorkflowRun Run(string @event, IReadOnlyList<int>? numbers = null, string sha = Sha, string? branch = "feature", string? owner = "forker") => new()
    {
        Id = 7,
        WorkflowId = 1,
        WorkflowName = "CI",
        RunNumber = 3,
        RunAttempt = 1,
        HeadSha = sha,
        HeadBranch = branch,
        HeadOwner = owner,
        Event = @event,
        PullRequestNumbers = numbers ?? [],
        Outcome = CheckOutcome.Success,
        HtmlUrl = new Uri("https://github.com/octo/hello/actions/runs/7"),
        CreatedAt = DateTimeOffset.UnixEpoch,
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    private static PullRequestsState Open(params (int Number, string Sha, string Branch, string Owner)[] pulls) => new()
    {
        OpenCount = ItemCount.Exact(pulls.Length),
        Items = pulls.Select(p => new PullRequestEntry
        {
            PullRequest = new PullRequest
            {
                Id = p.Number, Number = p.Number, Title = $"Title {p.Number}", AuthorLogin = "a", State = PullRequestState.Open, IsDraft = false,
                HeadSha = p.Sha, HeadRef = p.Branch, HeadOwner = p.Owner, BaseRef = "main", HtmlUrl = new Uri($"https://github.com/octo/hello/pull/{p.Number}"),
                CreatedAt = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch,
            },
        }).ToList(),
    };

    [Fact]
    public void A_listed_pull_request_gets_its_title_and_link()
    {
        var link = RunPullRequest.For(Run("pull_request", [12]), Open((12, Sha, "feature", "octo")));

        Assert.Equal(new RunPullRequest(12, "Title 12", new Uri("https://github.com/octo/hello/pull/12")), link);
    }

    [Fact]
    public void A_pull_request_outside_the_loaded_list_still_gets_a_link_from_the_run()
    {
        var link = RunPullRequest.For(Run("pull_request", [40, 41]), pullRequests: null);

        Assert.Equal(40, link!.Number);
        Assert.Null(link.Title);
        Assert.Equal(new Uri("https://github.com/octo/hello/pull/40"), link.HtmlUrl);
        Assert.Equal(1, link.Others);
    }

    [Fact]
    public void A_fork_pull_request_is_matched_by_commit_then_by_branch_and_owner()
    {
        // GitHub lists no pull requests on runs from forks.
        var byCommit = RunPullRequest.For(Run("pull_request", sha: Sha, branch: "main"), Open((5, "other", "main", "forker"), (6, Sha, "patch-1", "forker")));
        var byBranch = RunPullRequest.For(Run("pull_request", sha: "pushed-since", branch: "patch-1", owner: "forker"),
            Open((6, "older", "patch-1", "someone-else"), (7, "older", "patch-1", "forker")));

        Assert.Equal(6, byCommit!.Number);
        Assert.Equal(7, byBranch!.Number); // the same branch name in another contributor's fork isn't it
    }

    [Fact]
    public void Uncertain_matches_show_no_pull_request_rather_than_a_wrong_one()
    {
        // Another fork's branch with the same name; and a run whose fork owner isn't known.
        Assert.Null(RunPullRequest.For(Run("pull_request", sha: "x", branch: "main", owner: "forker"), Open((6, "y", "main", "someone-else"))));
        Assert.Null(RunPullRequest.For(Run("pull_request", sha: "x", branch: "main", owner: null), Open((6, "y", "main", "forker"))));

        // pull_request_target runs on the base branch: its commit and branch belong to main, not to a pull request.
        Assert.Null(RunPullRequest.For(Run("pull_request_target", sha: Sha, branch: "main"), Open((6, Sha, "main", "forker"))));
    }

    [Fact]
    public void Runs_without_a_pull_request_show_none()
    {
        // A push to a branch that also has an open pull request isn't attributed by guesswork.
        Assert.Null(RunPullRequest.For(Run("push"), Open((6, Sha, "feature", "forker"))));
        Assert.Null(RunPullRequest.For(Run("pull_request"), Open((6, "other", "elsewhere", "forker"))));
    }
}
