using RepoWatch.Core.Actions;
using RepoWatch.Core.Identity;
using RepoWatch.Core.Issues;
using RepoWatch.Core.PullRequests;
using RepoWatch.Core.State;
using static RepoWatch.Core.Tests.Fixtures;

namespace RepoWatch.Core.Tests.State;

public sealed class ResourceStateTests
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(5);

    private static ResourceError NetworkError(int minutes) => new(ResourceErrorKind.Network, "offline", T0.AddMinutes(minutes));

    private static IssuesState Issues(int count) => new() { OpenCount = ItemCount.Exact(count) };

    [Fact]
    public void Failed_refresh_keeps_the_last_good_value_and_marks_it_stale()
    {
        var resource = Resource<IssuesState>.NotLoaded
            .Succeeded(Issues(3), T0)
            .Failed(NetworkError(1));

        Assert.Equal(3, resource.Value!.OpenCount.Value);
        Assert.Equal(T0, resource.LastSuccessAt);
        Assert.Equal(Freshness.Stale, resource.GetFreshness(T0.AddMinutes(1), StaleAfter));
    }

    [Fact]
    public void Freshness_reflects_age_errors_and_recovery()
    {
        var resource = Resource<IssuesState>.NotLoaded;
        Assert.Equal(Freshness.NotLoaded, resource.GetFreshness(T0, StaleAfter));

        resource = resource.Failed(NetworkError(0));
        Assert.Equal(Freshness.Failed, resource.GetFreshness(T0, StaleAfter));

        resource = resource.Succeeded(Issues(1), T0.AddMinutes(1));
        Assert.Null(resource.LastError);
        Assert.Equal(Freshness.Fresh, resource.GetFreshness(T0.AddMinutes(2), StaleAfter));
        Assert.Equal(Freshness.Stale, resource.GetFreshness(T0.AddMinutes(10), StaleAfter));
    }

    [Fact]
    public void Cached_data_is_distinguished_until_confirmed_by_a_refresh()
    {
        var cached = Resource<IssuesState>.FromCache(Issues(4), T0.AddDays(-1));

        Assert.Equal(Freshness.Cached, cached.GetFreshness(T0, StaleAfter));
        Assert.Equal(Freshness.Cached, cached.Failed(NetworkError(0)).GetFreshness(T0, StaleAfter));
        Assert.Equal(Freshness.Fresh, cached.Succeeded(Issues(5), T0).GetFreshness(T0, StaleAfter));
    }

    [Fact]
    public void Feature_unavailable_is_an_answer_not_a_failure()
    {
        var resource = Resource<ActionsState>.NotLoaded.FeatureUnavailable(T0);

        Assert.Equal(ResourceAvailability.FeatureUnavailable, resource.Availability);
        Assert.Null(resource.LastError);
        Assert.Equal(Freshness.Fresh, resource.GetFreshness(T0, StaleAfter));
    }

    [Fact]
    public void A_failing_section_does_not_change_other_sections()
    {
        var snapshot = new RepositorySnapshot(new RepositoryKey(Account, 42))
        {
            Actions = Resource<ActionsState>.NotLoaded.Succeeded(new ActionsState(), T0),
            PullRequests = Resource<PullRequestsState>.NotLoaded.Succeeded(new PullRequestsState { OpenCount = ItemCount.Exact(2) }, T0),
            Issues = Resource<IssuesState>.NotLoaded.Succeeded(Issues(7), T0),
        };

        var updated = snapshot with { PullRequests = snapshot.PullRequests.Failed(new(ResourceErrorKind.ServerError, "502", T0.AddMinutes(1))) };

        Assert.Same(snapshot.Actions, updated.Actions);
        Assert.Same(snapshot.Issues, updated.Issues);
        Assert.Equal(Freshness.Fresh, updated.Issues.GetFreshness(T0.AddMinutes(1), StaleAfter));
        Assert.Equal(2, updated.PullRequests.Value!.OpenCount.Value);
        Assert.Equal(Freshness.Stale, updated.PullRequests.GetFreshness(T0.AddMinutes(1), StaleAfter));
    }

    [Fact]
    public void Losing_repository_access_withholds_all_cached_content()
    {
        var snapshot = new RepositorySnapshot(new RepositoryKey(Account, 42))
        {
            Issues = Resource<IssuesState>.FromCache(Issues(7), T0),
            PullRequests = Resource<PullRequestsState>.NotLoaded.Succeeded(new PullRequestsState { OpenCount = ItemCount.Exact(2) }, T0),
        };

        var lost = snapshot.WithAccessLost(new(ResourceErrorKind.NotFound, "Repository is no longer accessible.", T0.AddMinutes(1)));

        Assert.Null(lost.Issues.Value);
        Assert.Null(lost.PullRequests.Value);
        Assert.Equal(ResourceAvailability.AccessLost, lost.Actions.Availability);
        Assert.Equal(Freshness.Failed, lost.Issues.GetFreshness(T0.AddMinutes(1), StaleAfter));
    }

    [Fact]
    public void Partial_counts_are_labeled()
    {
        Assert.Equal("30+", ItemCount.AtLeast(30).ToString());
        Assert.Equal("12", ItemCount.Exact(12).ToString());
    }

    [Fact]
    public void Account_and_repository_keys_use_ids_not_names()
    {
        var a = new RepositoryKey(new AccountKey("GitHub.com/", 1001), 42);
        var b = new RepositoryKey(AccountKey.ForWebBase(new Uri("https://github.com"), 1001), 42);

        Assert.Equal(a, b);
        Assert.Equal("github.com/1001/42", a.StorageKey);
        Assert.NotEqual(a, new RepositoryKey(new AccountKey("github.com", 1002), 42));
    }
}
