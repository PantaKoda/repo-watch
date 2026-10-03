using RepoWatch.Core.Actions;
using RepoWatch.Core.Identity;
using RepoWatch.Core.Monitoring;
using RepoWatch.Core.PullRequests;
using RepoWatch.Core.Settings;
using RepoWatch.Core.State;
using RepoWatch.Core.Status;
using RepoWatch.Desktop.ViewModels;

namespace RepoWatch.Desktop.Tests.Presentation;

/// <summary>Labels for partial lists, missing branches and rate-limit retries.</summary>
public sealed class PartialDataPresentationTests
{
    private static readonly RepositoryKey Key = new(new AccountKey("github.com", 4242), 1);
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    private static RepositoryRowViewModel Row(RepositorySnapshot snapshot)
    {
        var row = new RepositoryRowViewModel(Key, new RecordingBrowser(), _ => Task.CompletedTask);
        row.Update(new MonitoredRepository(new WatchedRepository { RepositoryId = 1, Owner = "octo", Name = "repo" }, snapshot, 0), Now, isRefreshing: false, allowReorder: true);
        return row;
    }

    private static PullRequestEntry Entry(int number) => new()
    {
        PullRequest = new PullRequest
        {
            Id = number, Number = number, Title = $"PR {number}", AuthorLogin = "octo", State = PullRequestState.Open, IsDraft = false,
            HeadSha = "abc", HeadRef = "f", BaseRef = "main", HtmlUrl = new Uri($"https://github.com/octo/repo/pull/{number}"), CreatedAt = Now, UpdatedAt = Now,
        },
    };

    [Fact]
    public void A_first_page_is_labeled_with_the_total()
    {
        var pulls = new PullRequestsState { Items = Enumerable.Range(1, 30).Select(Entry).ToList(), OpenCount = ItemCount.Exact(45) };
        var row = Row(new RepositorySnapshot(Key) { PullRequests = Resource<PullRequestsState>.NotLoaded.Succeeded(pulls, Now) });

        Assert.Equal("PRs 45", row.PullRequestCount);
        Assert.EndsWith("showing 30 most recent of 45", row.PullRequests.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void A_lower_bound_count_says_more_may_exist()
    {
        var pulls = new PullRequestsState { Items = [Entry(1)], OpenCount = ItemCount.AtLeast(1) };
        var row = Row(new RepositorySnapshot(Key) { PullRequests = Resource<PullRequestsState>.NotLoaded.Succeeded(pulls, Now) });

        Assert.Equal("PRs 1+", row.PullRequestCount);
        Assert.EndsWith("showing 1 · more may exist", row.PullRequests.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_tracked_branches_are_shown()
    {
        var health = WorkflowRunSelection.ForCommit([], "abc", "release");
        var actions = new ActionsState { Branches = [health], MissingBranches = ["main"] };
        var row = Row(new RepositorySnapshot(Key) { Actions = Resource<ActionsState>.NotLoaded.Succeeded(actions, Now) });

        Assert.Equal("release: No checks · main: not found", row.BranchStatus);
    }

    [Fact]
    public void Rate_limited_sections_say_when_polling_resumes()
    {
        var retryAt = Now.AddMinutes(30);
        var issues = Resource<Core.Issues.IssuesState>.NotLoaded.Failed(new ResourceError(ResourceErrorKind.RateLimited, "Rate limited.", Now) { RetryAt = retryAt });
        var row = Row(new RepositorySnapshot(Key) { Issues = issues });

        Assert.Contains("retrying at " + retryAt.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.CurrentCulture), row.Issues.StatusText, StringComparison.Ordinal);
    }
}
