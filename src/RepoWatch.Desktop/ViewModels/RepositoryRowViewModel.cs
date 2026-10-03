using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RepoWatch.Core.Actions;
using RepoWatch.Core.Identity;
using RepoWatch.Core.Issues;
using RepoWatch.Core.Monitoring;
using RepoWatch.Core.Platform;
using RepoWatch.Core.PullRequests;
using RepoWatch.Core.State;
using RepoWatch.Desktop.Presentation;

namespace RepoWatch.Desktop.ViewModels;

/// <summary>One watched repository: a compact summary row plus the expanded detail sections.</summary>
public sealed partial class RepositoryRowViewModel : ObservableObject
{
    private readonly IExternalBrowser _browser;
    private readonly Func<RepositoryKey, Task> _refresh;

    public RepositoryRowViewModel(RepositoryKey key, IExternalBrowser browser, Func<RepositoryKey, Task> refresh)
    {
        Key = key;
        _browser = browser;
        _refresh = refresh;
    }

    public RepositoryKey Key { get; }

    public ActionsSectionViewModel Actions { get; } = new();

    public PullRequestsSectionViewModel PullRequests { get; } = new();

    public IssuesSectionViewModel Issues { get; } = new();

    [ObservableProperty]
    public partial string Name { get; private set; } = "";

    [ObservableProperty]
    public partial string Badges { get; private set; } = "";

    [ObservableProperty]
    public partial bool HasBadges { get; private set; }

    [ObservableProperty]
    public partial AttentionLevel Attention { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActive))]
    public partial StatusTone Tone { get; private set; }

    /// <summary>Work is running on the default branch: the row shows a scan line and its dot pulses.</summary>
    public bool IsActive => Tone == StatusTone.Running;

    /// <summary>Default-branch workflow health, e.g. "main: Failing".</summary>
    [ObservableProperty]
    public partial string BranchStatus { get; private set; } = "";

    [ObservableProperty]
    public partial string PullRequestCount { get; private set; } = "";

    [ObservableProperty]
    public partial string IssueCount { get; private set; } = "";

    /// <summary>Worst freshness across sections, shown as a compact warning on the summary row.</summary>
    [ObservableProperty]
    public partial string? FreshnessWarning { get; private set; }

    [ObservableProperty]
    public partial bool HasFreshnessWarning { get; private set; }

    [ObservableProperty]
    public partial Uri? Url { get; private set; }

    /// <summary>Full description for tooltips and screen readers.</summary>
    [ObservableProperty]
    public partial string Summary { get; private set; } = "";

    public void Update(MonitoredRepository repository, DateTimeOffset now, bool isRefreshing, bool allowReorder)
    {
        ArgumentNullException.ThrowIfNull(repository);
        var snapshot = repository.Snapshot;
        var metadata = snapshot.Metadata.Value;

        Name = repository.DisplayName;
        var badges = new List<string>();
        if (metadata?.IsPrivate == true)
        {
            badges.Add("private");
        }

        if (metadata?.IsArchived == true)
        {
            badges.Add("archived");
        }

        Badges = string.Join(" · ", badges);
        HasBadges = badges.Count > 0;
        Url = metadata?.HtmlUrl;

        Attention = AttentionPolicy.Evaluate(snapshot);
        var rollup = snapshot.Actions.Value?.DefaultBranch?.Rollup;
        Tone = Attention == AttentionLevel.Quiet && rollup is not null ? StatusPresentation.Tone(rollup.State) : StatusPresentation.Tone(Attention);
        BranchStatus = snapshot.Actions switch
        {
            { Availability: ResourceAvailability.AccessLost } => "No access",
            { Availability: ResourceAvailability.FeatureUnavailable } => "Actions unavailable",
            { Value: { } actions } when actions.TrackedBranches.Any() || actions.MissingBranches.Count > 0 => string.Join(" · ", actions.TrackedBranches
                .Select(b => $"{b.Branch ?? metadata?.DefaultBranch ?? "default branch"}: {StatusPresentation.Label(b.Rollup.State)}")
                .Concat(actions.MissingBranches.Select(b => $"{b}: not found"))),
            { Value: not null } => "No tracked branch",
            { LastError: not null } => "Actions: couldn't load",
            _ => "Actions: not loaded",
        };

        PullRequestCount = Count("PRs", snapshot.PullRequests, p => p.OpenCount);
        IssueCount = Count("Issues", snapshot.Issues, i => i.OpenCount);

        Actions.Apply(snapshot.Actions, now, isRefreshing, allowReorder,
            a => a.RecentRuns, r => r.Id, vm => vm.Id, r => Create(new RunItemViewModel(_browser, r.Id), vm => vm.Update(r, now)), (vm, r) => vm.Update(r, now));
        PullRequests.Apply(snapshot.PullRequests, now, isRefreshing, allowReorder,
            p => p.Items, e => e.PullRequest.Number, vm => vm.Number, e => Create(new PullRequestItemViewModel(_browser, e.PullRequest.Number), vm => vm.Update(e)), (vm, e) => vm.Update(e), p => p.OpenCount);
        Issues.Apply(snapshot.Issues, now, isRefreshing, allowReorder,
            i => i.Items, i => i.Number, vm => vm.Number, i => Create(new IssueItemViewModel(_browser, i.Number), vm => vm.Update(i, now)), (vm, i) => vm.Update(i, now), i => i.OpenCount);

        var worst = new[] { Actions.Freshness, PullRequests.Freshness, Issues.Freshness };
        FreshnessWarning = worst.Contains(Freshness.Failed) ? "Some data couldn't be loaded"
            : worst.Contains(Freshness.Stale) ? "Some data is stale"
            : null;
        HasFreshnessWarning = FreshnessWarning is not null;

        Summary = string.Join(". ", new[] { Name, Badges, BranchStatus, PullRequestCount, IssueCount, FreshnessWarning }.Where(s => !string.IsNullOrEmpty(s)));
    }

    [RelayCommand]
    private Task RefreshAsync() => _refresh(Key);

    [RelayCommand]
    private async Task OpenOnGitHubAsync()
    {
        if (Url is not null)
        {
            await _browser.OpenAsync(Url);
        }
    }

    private static string Count<T>(string label, Resource<T> resource, Func<T, ItemCount> count) where T : class => resource switch
    {
        { Availability: ResourceAvailability.FeatureUnavailable } => $"{label} off",
        { Value: { } value } => string.Create(CultureInfo.InvariantCulture, $"{label} {count(value)}"),
        _ => $"{label} ?",
    };

    private static T Create<T>(T item, Action<T> initialize)
    {
        initialize(item);
        return item;
    }
}
