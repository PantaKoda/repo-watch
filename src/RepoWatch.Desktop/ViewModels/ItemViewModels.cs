using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RepoWatch.Core.Actions;
using RepoWatch.Core.Issues;
using RepoWatch.Core.Platform;
using RepoWatch.Core.PullRequests;
using RepoWatch.Core.State;
using RepoWatch.Desktop.Presentation;

namespace RepoWatch.Desktop.ViewModels;

/// <summary>Base for list rows that open a GitHub page. Titles are untrusted text and are only displayed.</summary>
public abstract partial class LinkItemViewModel(IExternalBrowser browser) : ObservableObject
{
    [ObservableProperty]
    public partial Uri? Url { get; protected set; }

    [RelayCommand]
    private async Task OpenAsync()
    {
        if (Url is not null)
        {
            await browser.OpenAsync(Url);
        }
    }
}

public sealed partial class RunItemViewModel(IExternalBrowser browser, long id) : LinkItemViewModel(browser)
{
    private readonly IExternalBrowser _browser = browser;
    private DateTimeOffset _updatedAt;
    private Uri? _pullRequestUrl;

    public long Id { get; } = id;

    [ObservableProperty]
    public partial string Title { get; private set; } = "";

    [ObservableProperty]
    public partial string Detail { get; private set; } = "";

    [ObservableProperty]
    public partial string OutcomeLabel { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActive))]
    public partial StatusTone Tone { get; private set; }

    /// <summary>The run is queued or in progress.</summary>
    public bool IsActive => Tone == StatusTone.Running;

    [ObservableProperty]
    public partial string TimeText { get; private set; } = "";

    /// <summary>"PR #61 · Add login rate limiting": the pull request this run belongs to; null when none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPullRequest))]
    public partial string? PullRequestText { get; private set; }

    public bool HasPullRequest => PullRequestText is not null;

    public void Update(WorkflowRun run, DateTimeOffset now, RunPullRequest? pullRequest = null)
    {
        Title = run.WorkflowName;
        var attempt = run.RunAttempt > 1 ? string.Create(CultureInfo.InvariantCulture, $" · attempt {run.RunAttempt}") : "";
        Detail = string.Create(CultureInfo.InvariantCulture, $"#{run.RunNumber} · {run.HeadBranch ?? "(no branch)"} · {run.Event}{attempt}");
        OutcomeLabel = StatusPresentation.Label(run.Outcome);
        Tone = StatusPresentation.Tone(run.Outcome);
        Url = run.HtmlUrl;
        _updatedAt = run.UpdatedAt;
        _pullRequestUrl = pullRequest?.HtmlUrl;
        PullRequestText = pullRequest is null ? null
            : string.Create(CultureInfo.InvariantCulture, $"PR #{pullRequest.Number}")
              + (string.IsNullOrWhiteSpace(pullRequest.Title) ? "" : " · " + pullRequest.Title)
              + (pullRequest.Others > 0 ? string.Create(CultureInfo.InvariantCulture, $" (+{pullRequest.Others} more)") : "");
        OpenPullRequestCommand.NotifyCanExecuteChanged();
        Tick(now);
    }

    public void Tick(DateTimeOffset now) => TimeText = Presentation.TimeText.Ago(_updatedAt, now);

    [RelayCommand(CanExecute = nameof(CanOpenPullRequest))]
    private async Task OpenPullRequestAsync()
    {
        if (_pullRequestUrl is not null)
        {
            await _browser.OpenAsync(_pullRequestUrl);
        }
    }

    private bool CanOpenPullRequest() => _pullRequestUrl is not null;
}

/// <summary>What a screen reader says for a pull request or issue: its title and how many comments it has.</summary>
internal static class CommentText
{
    public static string Accessible(string title, int comments) => comments switch
    {
        0 => title,
        1 => title + ", 1 comment",
        _ => string.Create(CultureInfo.InvariantCulture, $"{title}, {comments} comments"),
    };
}

public sealed partial class PullRequestItemViewModel(IExternalBrowser browser, int number) : LinkItemViewModel(browser)
{
    public int Number { get; } = number;

    [ObservableProperty]
    public partial string Title { get; private set; } = "";

    [ObservableProperty]
    public partial string Detail { get; private set; } = "";

    [ObservableProperty]
    public partial string ChecksLabel { get; private set; } = "";

    [ObservableProperty]
    public partial StatusTone ChecksTone { get; private set; }

    [ObservableProperty]
    public partial string ReviewLabel { get; private set; } = "";

    [ObservableProperty]
    public partial string MergeLabel { get; private set; } = "";

    /// <summary>Conversation and inline review comments.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasComments))]
    public partial int CommentCount { get; private set; }

    public bool HasComments => CommentCount > 0;

    [ObservableProperty]
    public partial string AccessibleName { get; private set; } = "";

    public void Update(PullRequestEntry entry)
    {
        var pr = entry.PullRequest;
        Title = pr.Title;
        CommentCount = pr.CommentCount;
        AccessibleName = CommentText.Accessible(pr.Title, pr.CommentCount);
        Detail = string.Create(CultureInfo.InvariantCulture, $"#{pr.Number} by {pr.AuthorLogin}{(pr.IsDraft ? " · draft" : "")}");
        Url = pr.HtmlUrl;
        MergeLabel = StatusPresentation.Label(pr.MergeState);

        (ChecksLabel, ChecksTone) = entry.Checks switch
        {
            { Value: { } checks } => ("Checks: " + StatusPresentation.Label(checks.Rollup.State), StatusPresentation.Tone(checks.Rollup.State)),
            { LastError: not null } => ("Checks: couldn't load", StatusTone.Warning),
            _ => ("Checks: not loaded", StatusTone.Unknown), // only the most recent pull requests' checks are loaded
        };

        ReviewLabel = entry.Reviews.Value is { } reviews ? DescribeReviews(reviews) : "Reviews: unknown";
    }

    private static string DescribeReviews(ReviewSummary reviews)
    {
        var parts = new List<string>();
        if (reviews.ChangesRequested > 0)
        {
            parts.Add("changes requested");
        }

        if (reviews.Approvals > 0)
        {
            parts.Add(reviews.Approvals == 1 ? "1 approval" : $"{reviews.Approvals} approvals");
        }

        if (reviews.PendingRequests.Count > 0)
        {
            parts.Add("awaiting " + string.Join(", ", reviews.PendingRequests.Select(r => r.Login)));
        }

        return parts.Count == 0 ? "No reviews yet" : string.Join(" · ", parts);
    }
}

public sealed partial class IssueItemViewModel(IExternalBrowser browser, int number) : LinkItemViewModel(browser)
{
    private DateTimeOffset _updatedAt;

    public int Number { get; } = number;

    [ObservableProperty]
    public partial string Title { get; private set; } = "";

    [ObservableProperty]
    public partial string Detail { get; private set; } = "";

    [ObservableProperty]
    public partial string TimeText { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasComments))]
    public partial int CommentCount { get; private set; }

    public bool HasComments => CommentCount > 0;

    [ObservableProperty]
    public partial string AccessibleName { get; private set; } = "";

    public void Update(Issue issue, DateTimeOffset now)
    {
        Title = issue.Title;
        CommentCount = issue.CommentCount;
        AccessibleName = CommentText.Accessible(issue.Title, issue.CommentCount);
        var labels = issue.Labels.Count > 0 ? " · " + string.Join(", ", issue.Labels) : "";
        Detail = string.Create(CultureInfo.InvariantCulture, $"#{issue.Number} by {issue.AuthorLogin}{labels}");
        Url = issue.HtmlUrl;
        _updatedAt = issue.UpdatedAt;
        Tick(now);
    }

    public void Tick(DateTimeOffset now) => TimeText = Presentation.TimeText.Ago(_updatedAt, now);
}

// Concrete section types so XAML data templates and compiled bindings can target them.
public sealed class ActionsSectionViewModel() : SectionViewModel<RunItemViewModel>(
    "No workflow runs yet.", "GitHub Actions is disabled or unavailable for this repository.");

public sealed class PullRequestsSectionViewModel() : SectionViewModel<PullRequestItemViewModel>(
    "No open pull requests to show.", "Pull requests are turned off for this repository in Repo Watch.");

public sealed class IssuesSectionViewModel() : SectionViewModel<IssueItemViewModel>(
    "No open issues.", "Issues are turned off for this repository (on GitHub or in Repo Watch).");

/// <summary>
/// One independently refreshed section (Actions, pull requests or issues): its items plus the
/// loading, empty, unavailable, stale and error states.
/// </summary>
public abstract partial class SectionViewModel<TItem> : ObservableObject
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);

    private readonly string _emptyText;
    private readonly string _unavailableText;

    protected SectionViewModel(string emptyText, string unavailableText)
    {
        _emptyText = emptyText;
        _unavailableText = unavailableText;
    }

    public System.Collections.ObjectModel.ObservableCollection<TItem> Items { get; } = [];

    [ObservableProperty]
    public partial Freshness Freshness { get; private set; }

    /// <summary>Freshness line, e.g. "Updated 2m ago" or "Stale · updated 40m ago · server error".</summary>
    [ObservableProperty]
    public partial string StatusText { get; private set; } = "";

    [ObservableProperty]
    public partial StatusTone StatusTone { get; private set; }

    /// <summary>Message shown instead of items (loading, empty, unavailable or error); null when items are shown.</summary>
    [ObservableProperty]
    public partial string? Message { get; private set; }

    [ObservableProperty]
    public partial bool HasMessage { get; private set; }

    public void Apply<TValue, TSource, TKey>(
        Resource<TValue> resource,
        DateTimeOffset now,
        bool isRefreshing,
        bool allowReorder,
        Func<TValue, IReadOnlyList<TSource>> items,
        Func<TSource, TKey> sourceKey,
        Func<TItem, TKey> itemKey,
        Func<TSource, TItem> create,
        Action<TItem, TSource> update,
        Func<TValue, ItemCount>? count = null)
        where TValue : class
        where TKey : notnull
    {
        Freshness = resource.GetFreshness(now, StaleAfter);
        var sources = resource.Value is { } value ? items(value) : [];
        CollectionReconciler.Reconcile(Items, sources, sourceKey, itemKey, create, update, allowReorder);

        var updated = resource.LastSuccessAt is { } success ? TimeText.Ago(success, now) : null;
        var retry = resource.LastError?.RetryAt is { } retryAt && retryAt > now
            ? " · retrying at " + retryAt.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.CurrentCulture)
            : "";
        (StatusText, StatusTone) = Freshness switch
        {
            Freshness.Fresh => ($"Updated {updated}", StatusTone.None),
            Freshness.Cached => ($"Cached · updated {updated}", StatusTone.Warning),
            Freshness.Stale when resource.LastError is { } error => ($"Stale · updated {updated ?? "never"} · {error.Message}{retry}", StatusTone.Warning),
            Freshness.Stale => ($"Stale · updated {updated}", StatusTone.Warning),
            Freshness.Failed => ((resource.LastError?.Message ?? "Couldn't load") + retry, StatusTone.Failure),
            _ => (isRefreshing ? "Loading…" : "Not loaded yet", StatusTone.Unknown),
        };

        // A first page is not the whole list: say how much is shown.
        if (resource.Value is { } shown && count?.Invoke(shown) is { } total && Freshness is Freshness.Fresh or Freshness.Cached or Freshness.Stale)
        {
            var listed = sources.Count;
            var note = !total.IsExact ? $"showing {listed} · more may exist"
                : total.Value > listed ? $"showing {listed} most recent of {total.Value}"
                : null;
            if (note is not null)
            {
                StatusText += " · " + note;
            }
        }

        Message = resource switch
        {
            { Availability: ResourceAvailability.AccessLost } => "Repo Watch no longer has access to this repository. Cached content is hidden until access is restored.",
            { Availability: ResourceAvailability.FeatureUnavailable } => _unavailableText,
            { Value: null, LastError: { } error } => $"Couldn't load: {error.Message}",
            { Value: null } => isRefreshing ? "Loading…" : "Not loaded yet.",
            _ when Items.Count == 0 => _emptyText,
            _ => null,
        };
        HasMessage = Message is not null;
    }
}
