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
using RepoWatch.Core.Status;
using RepoWatch.Desktop.Presentation;

namespace RepoWatch.Desktop.ViewModels;

/// <summary>One watched repository: a compact summary row plus the expanded detail sections.</summary>
public sealed partial class RepositoryRowViewModel : ObservableObject
{
    /// <summary>How long a row glows after something changed.</summary>
    public static readonly TimeSpan FlashDuration = TimeSpan.FromSeconds(3);

    private readonly IExternalBrowser _browser;
    private readonly Func<RepositoryKey, Task> _refresh;
    private readonly Action<TimeSpan, Action>? _schedule;
    private readonly Action<RepositoryKey>? _markSeen;
    private string _summary = "";
    private int _flashes;

    /// <param name="schedule">Runs an action after a delay on the UI thread (ends the change glow); null in tests that don't need it.</param>
    /// <param name="markSeen">Tells the widget's activity tracker this repository was opened.</param>
    public RepositoryRowViewModel(RepositoryKey key, IExternalBrowser browser, Func<RepositoryKey, Task> refresh, Action<TimeSpan, Action>? schedule = null,
        Action<RepositoryKey>? markSeen = null)
    {
        Key = key;
        _browser = browser;
        _refresh = refresh;
        _schedule = schedule;
        _markSeen = markSeen;
    }

    /// <summary>Workflows queued or running (any branch), shown as a pulsing chip.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRunning), nameof(HasChips), nameof(RunningTone))]
    public partial string? RunningText { get; private set; }

    public bool HasRunning => RunningText is not null;

    /// <summary>The chip's dot pulses only while something runs: a hidden chip must never keep an animation going.</summary>
    public StatusTone RunningTone => HasRunning ? StatusTone.Running : StatusTone.None;

    /// <summary>A tracked branch's latest commit fails CI.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChips))]
    public partial bool IsFailing { get; private set; }

    /// <summary>"3 open PRs"; null when there are none (or they aren't watched).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPullRequests), nameof(HasChips))]
    public partial string? PullRequestsText { get; private set; }

    public bool HasPullRequests => PullRequestsText is not null;

    /// <summary>"30+ issues"; null when there are none (or they are turned off).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIssues), nameof(HasChips))]
    public partial string? IssuesText { get; private set; }

    public bool HasIssues => IssuesText is not null;

    /// <summary>Something to show at a glance; quiet repositories show no chips.</summary>
    public bool HasChips => HasRunning || IsFailing || HasPullRequests || HasIssues;

    /// <summary>Something changed since the user last opened this repository (a dot next to the name).</summary>
    [ObservableProperty]
    public partial bool HasUnseenActivity { get; private set; }

    partial void OnHasUnseenActivityChanged(bool value) => UpdateSummary();

    /// <summary>The row glows briefly after a change (not with reduced motion).</summary>
    [ObservableProperty]
    public partial bool IsFlashing { get; private set; }

    /// <summary>The user opened this repository: the "changed" dot goes away.</summary>
    public void MarkSeen()
    {
        HasUnseenActivity = false;
        _markSeen?.Invoke(Key);
    }

    /// <summary>
    /// Applies the widget's activity tracking: whether something changed since the user last opened this
    /// repository, and whether it just changed (the row glows).
    /// </summary>
    public void ApplyActivity(bool unseen, bool changed)
    {
        HasUnseenActivity = unseen;
        if (changed)
        {
            Flash();
        }
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

    /// <summary>"Private" or "Public" for the row's tag; null until the repository's metadata is known.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVisibility))]
    public partial string? Visibility { get; private set; }

    public bool HasVisibility => Visibility is not null;

    [ObservableProperty]
    public partial bool IsPrivate { get; private set; }

    /// <summary>When something last happened (push, run, pull request or issue update), e.g. "3h ago".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActivity))]
    public partial string? ActivityText { get; private set; }

    public bool HasActivity => ActivityText is not null;

    public DateTimeOffset? LastActivity { get; private set; }

    /// <summary>Nothing to look at: see <see cref="AttentionPolicy.IsIdle"/>.</summary>
    public bool IsIdle { get; private set; }

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
    [NotifyPropertyChangedFor(nameof(HasUrl))]
    public partial Uri? Url { get; private set; }

    /// <summary>The repository's GitHub page is known, so the row can offer to open it.</summary>
    public bool HasUrl => Url is not null;

    /// <summary>Full description for tooltips and screen readers.</summary>
    [ObservableProperty]
    public partial string Summary { get; private set; } = "";

    public void Update(MonitoredRepository repository, DateTimeOffset now, bool isRefreshing, bool allowReorder)
    {
        ArgumentNullException.ThrowIfNull(repository);
        var snapshot = repository.Snapshot;
        var metadata = snapshot.Metadata.Value;

        Name = repository.DisplayName;
        Visibility = metadata is null ? null : metadata.IsPrivate ? "Private" : "Public";
        IsPrivate = metadata?.IsPrivate == true;
        var badges = new List<string>();
        if (metadata?.IsArchived == true)
        {
            badges.Add("archived");
        }

        Badges = string.Join(" · ", badges);
        HasBadges = badges.Count > 0;
        Url = metadata?.HtmlUrl;

        Attention = AttentionPolicy.Evaluate(snapshot);
        IsIdle = AttentionPolicy.IsIdle(snapshot);
        LastActivity = AttentionPolicy.LastActivity(snapshot);
        ActivityText = LastActivity is { } at ? TimeText.Ago(at, now) : null;
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

        var running = snapshot.Actions.Value?.RecentRuns.Count(r => r.Outcome is CheckOutcome.Queued or CheckOutcome.Waiting or CheckOutcome.Running) ?? 0;
        RunningText = running > 0 ? $"{running} running" : null;
        IsFailing = Attention == AttentionLevel.Failure;
        PullRequestsText = snapshot.PullRequests.Value is { OpenCount: { Value: > 0 } prs } ? $"{prs} open PR{(prs is { Value: 1, IsExact: true } ? "" : "s")}" : null;
        IssuesText = snapshot.Issues.Value is { OpenCount: { Value: > 0 } issues } ? $"{issues} issue{(issues is { Value: 1, IsExact: true } ? "" : "s")}" : null;

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

        var activity = ActivityText is null ? null : $"Last activity {ActivityText}";
        _summary = string.Join(". ", new[] { Name, Visibility?.ToLowerInvariant(), Badges, BranchStatus, RunningText, PullRequestCount, IssueCount, activity, FreshnessWarning }.Where(s => !string.IsNullOrEmpty(s)));
        UpdateSummary();
    }

    /// <summary>What screen readers announce: everything on the row, including "changed since you last opened it".</summary>
    private void UpdateSummary() => Summary = HasUnseenActivity ? _summary + ". Changed since you last opened it" : _summary;

    private void Flash()
    {
        if (_schedule is null)
        {
            IsFlashing = true;
            return;
        }

        // Restart the glow for a change during a glow; only the latest one ends it.
        IsFlashing = false;
        IsFlashing = true;
        var flash = ++_flashes;
        _schedule(FlashDuration, () =>
        {
            if (flash == _flashes)
            {
                IsFlashing = false;
            }
        });
    }

    [RelayCommand]
    private Task RefreshAsync() => _refresh(Key);

    [RelayCommand]
    private async Task OpenOnGitHubAsync()
    {
        MarkSeen();
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
