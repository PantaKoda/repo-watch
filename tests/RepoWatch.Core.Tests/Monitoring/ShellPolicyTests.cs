using RepoWatch.Core.Actions;
using RepoWatch.Core.Identity;
using RepoWatch.Core.Layout;
using RepoWatch.Core.Monitoring;
using RepoWatch.Core.Platform;
using RepoWatch.Core.Settings;
using RepoWatch.Core.State;
using RepoWatch.Core.Status;
using static RepoWatch.Core.Tests.Fixtures;

namespace RepoWatch.Core.Tests.Monitoring;

public sealed class ShellPolicyTests
{
    private static readonly ScreenInfo Laptop = new(new(0, 0, 1920, 1080), new(0, 0, 1920, 1040), 1.5, IsPrimary: true);
    private static readonly ScreenInfo External = new(new(1920, 0, 2560, 1440), new(1920, 0, 2560, 1400), 1.0, IsPrimary: false);

    [Fact]
    public void Display_key_is_independent_of_screen_order_and_sensitive_to_scaling()
    {
        Assert.Equal(PlacementPolicy.DisplayKey([Laptop, External]), PlacementPolicy.DisplayKey([External, Laptop]));
        Assert.NotEqual(PlacementPolicy.DisplayKey([Laptop]), PlacementPolicy.DisplayKey([Laptop with { Scaling = 1.25 }]));
    }

    [Fact]
    public void A_window_on_a_removed_monitor_moves_to_the_primary_working_area()
    {
        var onExternal = new PixelBox(3000, 200, 600, 780);

        var restored = PlacementPolicy.EnsureReachable(onExternal, [Laptop]);

        Assert.True(restored.X >= Laptop.WorkingArea.X && restored.Right <= Laptop.WorkingArea.Right);
        Assert.True(restored.Y >= Laptop.WorkingArea.Y && restored.Bottom <= Laptop.WorkingArea.Bottom);
        Assert.Equal(600, restored.Width);
    }

    [Fact]
    public void A_window_with_a_reachable_title_strip_is_left_alone()
    {
        var partlyOffBottom = new PixelBox(100, 900, 600, 780);
        var spanning = new PixelBox(1700, 100, 600, 700);

        Assert.Equal(partlyOffBottom, PlacementPolicy.EnsureReachable(partlyOffBottom, [Laptop]));
        Assert.Equal(spanning, PlacementPolicy.EnsureReachable(spanning, [Laptop, External]));
    }

    [Fact]
    public void A_window_above_the_screen_is_recovered_and_shrunk_to_fit()
    {
        var aboveAndHuge = new PixelBox(100, -3000, 600, 5000);

        var restored = PlacementPolicy.EnsureReachable(aboveAndHuge, [Laptop]);

        Assert.True(restored.Y >= 0);
        Assert.True(restored.Height <= Laptop.WorkingArea.Height);
    }

    [Theory]
    [InlineData("https://github.com/o/r/pull/1", true)]
    [InlineData("https://docs.github.com/actions", true)]
    [InlineData("http://github.com/o/r", false)]
    [InlineData("https://github.com.evil.example/o/r", false)]
    [InlineData("https://evilgithub.com/o/r", false)]
    [InlineData("https://user:pw@github.com/o/r", false)]
    [InlineData("https://github.com:8443/o/r", false)]
    [InlineData("file:///C:/Windows/System32/calc.exe", false)]
    public void Only_https_links_on_the_github_host_open(string url, bool allowed)
    {
        Assert.Equal(allowed, ExternalLinkPolicy.IsAllowed(new Uri(url), new Uri("https://github.com/")));
    }

    [Fact]
    public void Attention_first_ordering_puts_failures_first_and_keeps_manual_order_within_a_level()
    {
        MonitoredRepository Repo(long id, int index, CheckOutcome? defaultBranch)
        {
            var key = new RepositoryKey(Account, id);
            var actions = defaultBranch is { } outcome
                ? Resource<ActionsState>.NotLoaded.Succeeded(new ActionsState { DefaultBranch = WorkflowRunSelection.ForCommit([Run(id, outcome)], HeadSha) }, T0)
                : Resource<ActionsState>.NotLoaded;
            return new MonitoredRepository(new WatchedRepository { RepositoryId = id }, new RepositorySnapshot(key) { Actions = actions }, index);
        }

        var repositories = new[]
        {
            Repo(1, 0, CheckOutcome.Success),
            Repo(2, 1, CheckOutcome.Running),
            Repo(3, 2, CheckOutcome.Failure),
            Repo(4, 3, CheckOutcome.Success),
            Repo(5, 4, CheckOutcome.Failure),
        };

        var attention = AttentionPolicy.Order(repositories, RepositoryOrdering.AttentionFirst).Select(r => r.Key.RepositoryId);
        var manual = AttentionPolicy.Order(repositories.Reverse(), RepositoryOrdering.Manual).Select(r => r.Key.RepositoryId);

        Assert.Equal([3L, 5L, 2L, 1L, 4L], attention);
        Assert.Equal([1L, 2L, 3L, 4L, 5L], manual);
    }

    [Fact]
    public void A_failed_section_without_data_needs_attention()
    {
        var snapshot = new RepositorySnapshot(new RepositoryKey(Account, 1))
        {
            Issues = Resource<Issues.IssuesState>.NotLoaded.Failed(new ResourceError(ResourceErrorKind.Network, "offline", T0)),
        };

        Assert.Equal(AttentionLevel.Warning, AttentionPolicy.Evaluate(snapshot));
        Assert.Equal(AttentionLevel.Quiet, AttentionPolicy.Evaluate(new RepositorySnapshot(new RepositoryKey(Account, 2))));
    }
}
