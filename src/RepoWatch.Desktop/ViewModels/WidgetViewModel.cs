using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RepoWatch.Core.Configuration;
using RepoWatch.Core.Identity;
using RepoWatch.Core.Monitoring;
using RepoWatch.Core.Platform;
using RepoWatch.Core.Settings;
using RepoWatch.Core.State;
using RepoWatch.Desktop.Presentation;
using RepoWatch.Desktop.Services;

namespace RepoWatch.Desktop.ViewModels;

/// <summary>A choice in the widget's sort menu.</summary>
public sealed record SortOption(RepositoryOrdering Ordering, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// The floating widget. Observes the current monitor and issues commands; it never polls.
/// While the user interacts with a list, rows keep their positions and reorder afterwards.
/// The list can be narrowed by a name filter and by hiding idle repositories; both only affect
/// what is shown, never what is monitored.
/// </summary>
public sealed partial class WidgetViewModel : ObservableObject, IDisposable
{
    private readonly MonitorHost _monitors;
    private readonly SettingsService _settings;
    private readonly IShell _shell;
    private readonly IExternalBrowser _browser;
    private readonly TimeProvider _time;
    private readonly IUiDispatcher _dispatcher;
    private readonly WatchlistService? _watchlist;
    private IRepositoryMonitor _monitor;
    private bool _syncing;

    // Demo data ignores the account's saved order, so the sort menu applies for this session only.
    private RepositoryOrdering? _demoOrdering;

    // The first load ends once any repository's metadata has an outcome (data, error or lost access)
    // or a refresh pass finishes, so repositories that never get metadata can't keep the line running.
    private bool _firstLoadDone;
    private bool _wasRefreshing;

    private static readonly TimeSpan NoticeDuration = TimeSpan.FromSeconds(8);

    public WidgetViewModel(MonitorHost monitors, SettingsService settings, IShell shell, IExternalBrowser browser, TimeProvider time, IUiDispatcher dispatcher, RepoWatchOptions options,
        WatchlistService? watchlist = null)
    {
        _watchlist = watchlist;
        IsSignInConfigured = options.GitHub.IsSignInConfigured;
        _monitors = monitors;
        _settings = settings;
        _shell = shell;
        _browser = new ReportingBrowser(browser, ReportLinkResult);
        _time = time;
        _dispatcher = dispatcher;
        _monitor = monitors.Current;

        _monitor.Changed += OnMonitorChanged;
        _monitors.CurrentChanged += OnCurrentMonitorChanged;
        _settings.AppChanged += OnSettingsChanged;

        ApplySettings();
        Sync();
    }

    public ObservableCollection<RepositoryRowViewModel> Repositories { get; } = [];

    [ObservableProperty]
    public partial RepositoryRowViewModel? SelectedRepository { get; set; }

    [ObservableProperty]
    public partial string ConnectionLabel { get; private set; } = "";

    [ObservableProperty]
    public partial StatusTone ConnectionTone { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChangeSort))]
    public partial bool IsDemo { get; private set; }

    /// <summary>The monitor is refreshing something, including routine background polls.</summary>
    [ObservableProperty]
    public partial bool IsRefreshing { get; private set; }

    /// <summary>A refresh the user asked for (Refresh / F5) is still running.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowActivity))]
    public partial bool IsManualRefreshing { get; private set; }

    /// <summary>Repositories are being loaded for the first time (no data and no cache yet).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowActivity))]
    public partial bool IsLoadingFirstData { get; private set; }

    /// <summary>
    /// The header's moving line: only while the user waits for something (a manual refresh or the
    /// first load). Routine background polling stays silent; freshness labels report its results.
    /// </summary>
    public bool ShowActivity => IsManualRefreshing || IsLoadingFirstData;

    /// <summary>Filters the list by repository name (owner/name), case-insensitively.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSearchText))]
    public partial string SearchText { get; set; } = "";

    public bool HasSearchText => !string.IsNullOrWhiteSpace(SearchText);

    /// <summary>Hide repositories with no open pull requests or issues, nothing running and no problems.</summary>
    [ObservableProperty]
    public partial bool HideIdle { get; set; }

    public IReadOnlyList<SortOption> SortOptions { get; } =
    [
        new(RepositoryOrdering.AttentionFirst, "Needs attention"),
        new(RepositoryOrdering.RecentActivity, "Recent activity"),
        new(RepositoryOrdering.Manual, "My order"),
    ];

    [ObservableProperty]
    public partial SortOption? SelectedSort { get; set; }

    /// <summary>Sorting is changed through the watchlist (it is a per-account preference).</summary>
    public bool CanChangeSort => _watchlist is not null || _monitor.State == ConnectionState.Demo;

    /// <summary>Watched repositories hidden by the filters.</summary>
    [ObservableProperty]
    public partial int HiddenCount { get; private set; }

    /// <summary>Repositories are watched, but the filters hide all of them.</summary>
    [ObservableProperty]
    public partial bool ShowNoMatches { get; private set; }

    [ObservableProperty]
    public partial string NoMatchesText { get; private set; } = "";

    [ObservableProperty]
    public partial string SummaryText { get; private set; } = "";

    [ObservableProperty]
    public partial bool ShowSignedOutState { get; private set; }

    [ObservableProperty]
    public partial bool ShowEmptyWatchlist { get; private set; }

    /// <summary>The GitHub session expired or was revoked: show a reconnect action instead of retrying.</summary>
    [ObservableProperty]
    public partial bool ShowReconnectState { get; private set; }

    [ObservableProperty]
    public partial bool ShowConnectingState { get; private set; }

    public bool IsSignInConfigured { get; }

    public string SignedOutText => IsSignInConfigured
        ? "Repo Watch shows GitHub Actions, pull requests and issues for repositories you choose. Sign in with GitHub to get started."
        : "Repo Watch shows GitHub Actions, pull requests and issues for repositories you choose. GitHub sign-in is not configured in this build (no GitHub App client ID).";

    /// <summary>At least one repository is watched (it may be hidden by the filters).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowList))]
    [NotifyPropertyChangedFor(nameof(ShowToolbar))]
    public partial bool HasRepositories { get; private set; }

    [ObservableProperty]
    public partial bool IsExpanded { get; private set; }

    /// <summary>True when the selected repository's details replace the list (expanded mode only).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowList))]
    [NotifyPropertyChangedFor(nameof(ShowToolbar))]
    public partial bool ShowDetails { get; private set; }

    public bool ShowList => HasRepositories && !ShowDetails;

    /// <summary>The filter bar above the list.</summary>
    public bool ShowToolbar => HasRepositories && !ShowDetails;

    [ObservableProperty]
    public partial bool AlwaysOnTop { get; private set; }

    /// <summary>Opacity of the background layer only (set by the shell from the achieved material). Text stays opaque.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsTextHalo))]
    public partial double SurfaceOpacity { get; set; } = 1;

    /// <summary>Below this the background no longer guarantees contrast, so content gets a halo.</summary>
    public bool NeedsTextHalo => SurfaceOpacity < 0.75;

    [ObservableProperty]
    public partial bool PositionLocked { get; private set; }

    /// <summary>True while a pointer is over, or keyboard focus is inside, a list. Set by the view.</summary>
    public bool IsInteracting
    {
        get;
        set
        {
            if (field == value)
            {
                return;
            }

            field = value;
            if (!value && HasPendingReorder)
            {
                Sync();
            }
        }
    }

    /// <summary>True when rows are out of their desired order because an interaction deferred reordering.</summary>
    public bool HasPendingReorder { get; private set; }

    public string HideTooltip => _shell.CanHideToTray ? "Hide to tray" : "Minimize";

    /// <summary>A short-lived message about an action that did not work (e.g. a link that could not be opened).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    public partial string? Notice { get; private set; }

    public bool HasNotice => Notice is not null;

    /// <summary>
    /// Called periodically by the shell. Re-evaluates freshness against the current time so data
    /// that stops arriving turns stale even when the monitor raises no events.
    /// </summary>
    public void Tick() => Sync();

    public void Dispose()
    {
        _monitor.Changed -= OnMonitorChanged;
        _monitors.CurrentChanged -= OnCurrentMonitorChanged;
        _settings.AppChanged -= OnSettingsChanged;
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsManualRefreshing = true;
        try
        {
            await _monitor.RefreshAsync();
        }
        finally
        {
            IsManualRefreshing = false;
        }
    }

    [RelayCommand]
    private void ClearFilters()
    {
        SearchText = "";
        HideIdle = false;
    }

    partial void OnSearchTextChanged(string value) => Sync(filtersChanged: true);

    partial void OnHideIdleChanged(bool value)
    {
        if (!_syncing && _settings.App.Window.HideIdleRepositories != value)
        {
            _settings.UpdateApp(s => s with { Window = s.Window with { HideIdleRepositories = value } });
        }

        Sync(filtersChanged: true);
    }

    partial void OnSelectedSortChanged(SortOption? value)
    {
        if (_syncing || value is null || value.Ordering == Ordering)
        {
            return;
        }

        if (_monitor.State == ConnectionState.Demo)
        {
            _demoOrdering = value.Ordering;
            Sync();
        }
        else
        {
            _watchlist?.SetOrdering(value.Ordering); // the monitor picks it up and raises Changed
        }
    }

    private RepositoryOrdering Ordering => _monitor.State == ConnectionState.Demo ? _demoOrdering ?? _monitor.Ordering : _monitor.Ordering;

    [RelayCommand]
    private void ToggleExpanded() => _settings.UpdateApp(s => s with { Window = s.Window with { Expanded = !s.Window.Expanded } });

    /// <summary>Opens a repository's details (expanding the widget if needed).</summary>
    [RelayCommand]
    private void ShowRepository(RepositoryRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        SelectedRepository = row;
        if (!IsExpanded)
        {
            ToggleExpanded();
            IsExpanded = true;
        }

        ShowDetails = true;
    }

    [RelayCommand]
    private void Back() => ShowDetails = false;

    partial void OnShowDetailsChanged(bool value) => UpdateFocus();

    partial void OnSelectedRepositoryChanged(RepositoryRowViewModel? value) => UpdateFocus();

    /// <summary>The repository whose details are open is refreshed first and more often.</summary>
    private void UpdateFocus() => _monitor.SetFocus(ShowDetails ? SelectedRepository?.Key : null);

    /// <summary>Escape: leave details first, then clear the name filter, then collapse the widget.</summary>
    [RelayCommand]
    private void Collapse()
    {
        if (ShowDetails)
        {
            ShowDetails = false;
        }
        else if (HasSearchText)
        {
            SearchText = "";
        }
        else if (IsExpanded)
        {
            ToggleExpanded();
        }
    }

    [RelayCommand]
    private void OpenSettings() => _shell.OpenSettings();

    [RelayCommand]
    private void SignIn() => _shell.BeginSignIn();

    [RelayCommand]
    private void AddRepositories() => _shell.OpenRepositories(RepositoriesTab.Add);

    [RelayCommand]
    private void Hide() => _shell.HideWidget();

    [RelayCommand]
    private void EnterDemo() => _monitors.EnterDemo();

    [RelayCommand]
    private void ExitDemo() => _monitors.ExitDemo();

    private void OnMonitorChanged(object? sender, EventArgs e) => _dispatcher.Post(() => Sync());

    private void OnCurrentMonitorChanged(object? sender, EventArgs e)
    {
        _monitor.Changed -= OnMonitorChanged;
        _monitor = _monitors.Current;
        _monitor.Changed += OnMonitorChanged;
        Repositories.Clear();
        _demoOrdering = null;
        _firstLoadDone = false;
        _wasRefreshing = false;
        SelectedRepository = null;
        ShowDetails = false;
        Sync();
    }

    // Placement saves arrive several times per second while dragging; only the flags shown here matter.
    private void OnSettingsChanged(object? sender, AppSettingsChangedEventArgs e)
    {
        var (before, after) = (e.Previous.Window, e.Current.Window);
        if (before.Expanded != after.Expanded || before.AlwaysOnTop != after.AlwaysOnTop || before.PositionLocked != after.PositionLocked
            || before.HideIdleRepositories != after.HideIdleRepositories)
        {
            _dispatcher.Post(ApplySettings);
        }
    }

    private void ReportLinkResult(LinkOpenResult result)
    {
        var message = result switch
        {
            LinkOpenResult.Refused => "That link isn't on GitHub, so it wasn't opened.",
            LinkOpenResult.Failed => "Couldn't open your web browser.",
            _ => null,
        };

        _dispatcher.Post(() => Notice = message);
        if (message is not null)
        {
            _ = ClearNoticeLaterAsync(message);
        }
    }

    private async Task ClearNoticeLaterAsync(string message)
    {
        await Task.Delay(NoticeDuration, _time).ConfigureAwait(false);
        _dispatcher.Post(() =>
        {
            if (Notice == message)
            {
                Notice = null;
            }
        });
    }

    /// <summary>Reports link results to the widget so failures are never silent.</summary>
    private sealed class ReportingBrowser(IExternalBrowser inner, Action<LinkOpenResult> report) : IExternalBrowser
    {
        public async Task<LinkOpenResult> OpenAsync(Uri url)
        {
            var result = await inner.OpenAsync(url).ConfigureAwait(false);
            report(result);
            return result;
        }
    }

    private void ApplySettings()
    {
        var window = _settings.App.Window;
        IsExpanded = window.Expanded;
        AlwaysOnTop = window.AlwaysOnTop;
        PositionLocked = window.PositionLocked;
        _syncing = true;
        HideIdle = window.HideIdleRepositories;
        _syncing = false;
        if (!IsExpanded)
        {
            ShowDetails = false;
        }
    }

    /// <param name="filtersChanged">
    /// The user changed the filters: their result applies at once. Otherwise (a status change) a row
    /// that just became idle stays while the user interacts with the list, like a deferred reorder.
    /// </param>
    private void Sync(bool filtersChanged = false)
    {
        var now = _time.GetUtcNow();
        var refreshing = _monitor.IsRefreshing;
        var allowReorder = !IsInteracting;
        var all = _monitor.Repositories;
        var search = SearchText.Trim();
        var keepShownIdle = !allowReorder && !filtersChanged;
        var shown = keepShownIdle ? Repositories.Select(r => r.Key).ToHashSet() : [];
        var deferredRemoval = false;

        bool Visible(MonitoredRepository r)
        {
            if (search.Length > 0 && !r.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!HideIdle || !AttentionPolicy.IsIdle(r.Snapshot) || (ShowDetails && SelectedRepository?.Key == r.Key))
            {
                return true; // a repository whose details are open stays visible
            }

            if (shown.Contains(r.Key))
            {
                deferredRemoval = true; // became idle under the pointer or focus: remove after the interaction
                return true;
            }

            return false;
        }

        var ordered = AttentionPolicy.Order(all, Ordering).Where(Visible).ToList();
        HasPendingReorder = deferredRemoval | !CollectionReconciler.Reconcile(
            Repositories,
            ordered,
            r => r.Key,
            vm => vm.Key,
            r =>
            {
                var row = new RepositoryRowViewModel(r.Key, _browser, key => _monitor.RefreshAsync(key));
                row.Update(r, now, refreshing, allowReorder);
                return row;
            },
            (vm, r) => vm.Update(r, now, refreshing, allowReorder),
            allowReorder);

        if (SelectedRepository is not null && !Repositories.Contains(SelectedRepository))
        {
            SelectedRepository = null;
            ShowDetails = false;
        }

        var (label, tone) = StatusPresentation.Connection(_monitor.State);
        ConnectionLabel = StatusPresentation.WithRetry(label, _monitor.IsRateLimited, _monitor.RetryAt);
        ConnectionTone = _monitor.IsRateLimited ? StatusTone.Warning : tone;
        IsDemo = _monitor.State == ConnectionState.Demo;
        IsRefreshing = refreshing;
        _firstLoadDone |= (_wasRefreshing && !refreshing)
            || all.Any(r => r.Snapshot.Metadata.Value is not null || r.Snapshot.Metadata.LastError is not null
                || r.Snapshot.Metadata.Availability == ResourceAvailability.AccessLost);
        _wasRefreshing = refreshing;
        IsLoadingFirstData = !_firstLoadDone && refreshing && all.Count > 0;
        HasRepositories = all.Count > 0;
        HiddenCount = all.Count - Repositories.Count;
        ShowNoMatches = HasRepositories && Repositories.Count == 0;
        NoMatchesText = HasSearchText
            ? $"No watched repository matches \u201c{search}\u201d."
            : "All quiet: the hidden repositories have no open pull requests or issues, nothing running and no problems.";
        ShowSignedOutState = _monitor.State == ConnectionState.NotSignedIn;
        ShowReconnectState = _monitor.State == ConnectionState.ReconnectRequired && !HasRepositories;
        ShowConnectingState = _monitor.State == ConnectionState.Connecting && !HasRepositories;
        ShowEmptyWatchlist = !ShowSignedOutState && !ShowReconnectState && !ShowConnectingState && !HasRepositories;

        _syncing = true;
        SelectedSort = SortOptions.FirstOrDefault(o => o.Ordering == Ordering);
        _syncing = false;

        // Counted over every watched repository, so filtering never hides that something is failing.
        var levels = all.Select(r => AttentionPolicy.Evaluate(r.Snapshot)).ToList();
        var failing = levels.Count(l => l == AttentionLevel.Failure);
        var warnings = levels.Count(l => l == AttentionLevel.Warning);
        SummaryText = HasRepositories
            ? (HiddenCount > 0
                ? string.Create(CultureInfo.InvariantCulture, $"{Repositories.Count} of {all.Count} shown")
                : string.Create(CultureInfo.InvariantCulture, $"{all.Count} repositories"))
                + (failing > 0 ? $" · {failing} failing" : "")
                + (warnings > 0 ? $" · {warnings} need attention" : "")
            : "";
    }
}
