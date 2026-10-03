using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RepoWatch.Core.Configuration;
using RepoWatch.Core.Identity;
using RepoWatch.Core.Monitoring;
using RepoWatch.Core.Platform;
using RepoWatch.Core.Settings;
using RepoWatch.Desktop.Presentation;
using RepoWatch.Desktop.Services;

namespace RepoWatch.Desktop.ViewModels;

/// <summary>
/// The floating widget. Observes the current monitor and issues commands; it never polls.
/// While the user interacts with a list, rows keep their positions and reorder afterwards.
/// </summary>
public sealed partial class WidgetViewModel : ObservableObject, IDisposable
{
    private readonly MonitorHost _monitors;
    private readonly SettingsService _settings;
    private readonly IShell _shell;
    private readonly IExternalBrowser _browser;
    private readonly TimeProvider _time;
    private readonly IUiDispatcher _dispatcher;
    private IRepositoryMonitor _monitor;

    private static readonly TimeSpan NoticeDuration = TimeSpan.FromSeconds(8);

    public WidgetViewModel(MonitorHost monitors, SettingsService settings, IShell shell, IExternalBrowser browser, TimeProvider time, IUiDispatcher dispatcher, RepoWatchOptions options)
    {
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
    public partial bool IsDemo { get; private set; }

    [ObservableProperty]
    public partial bool IsRefreshing { get; private set; }

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowList))]
    public partial bool HasRepositories { get; private set; }

    [ObservableProperty]
    public partial bool IsExpanded { get; private set; }

    /// <summary>True when the selected repository's details replace the list (expanded mode only).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowList))]
    public partial bool ShowDetails { get; private set; }

    public bool ShowList => HasRepositories && !ShowDetails;

    [ObservableProperty]
    public partial bool AlwaysOnTop { get; private set; }

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
    private Task RefreshAsync() => _monitor.RefreshAsync();

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

    /// <summary>Escape: leave details first, then collapse the widget.</summary>
    [RelayCommand]
    private void Collapse()
    {
        if (ShowDetails)
        {
            ShowDetails = false;
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

    private void OnMonitorChanged(object? sender, EventArgs e) => _dispatcher.Post(Sync);

    private void OnCurrentMonitorChanged(object? sender, EventArgs e)
    {
        _monitor.Changed -= OnMonitorChanged;
        _monitor = _monitors.Current;
        _monitor.Changed += OnMonitorChanged;
        Repositories.Clear();
        SelectedRepository = null;
        ShowDetails = false;
        Sync();
    }

    // Placement saves arrive several times per second while dragging; only the flags shown here matter.
    private void OnSettingsChanged(object? sender, AppSettingsChangedEventArgs e)
    {
        var (before, after) = (e.Previous.Window, e.Current.Window);
        if (before.Expanded != after.Expanded || before.AlwaysOnTop != after.AlwaysOnTop || before.PositionLocked != after.PositionLocked)
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
        if (!IsExpanded)
        {
            ShowDetails = false;
        }
    }

    private void Sync()
    {
        var now = _time.GetUtcNow();
        var refreshing = _monitor.IsRefreshing;
        var allowReorder = !IsInteracting;

        var ordered = AttentionPolicy.Order(_monitor.Repositories, _monitor.Ordering);
        HasPendingReorder = !CollectionReconciler.Reconcile(
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
        ConnectionLabel = label;
        ConnectionTone = tone;
        IsDemo = _monitor.State == ConnectionState.Demo;
        IsRefreshing = refreshing;
        HasRepositories = Repositories.Count > 0;
        ShowSignedOutState = _monitor.State == ConnectionState.NotSignedIn;
        ShowReconnectState = _monitor.State == ConnectionState.ReconnectRequired && !HasRepositories;
        ShowConnectingState = _monitor.State == ConnectionState.Connecting && !HasRepositories;
        ShowEmptyWatchlist = !ShowSignedOutState && !ShowReconnectState && !ShowConnectingState && !HasRepositories;

        var failing = Repositories.Count(r => r.Attention == AttentionLevel.Failure);
        var warnings = Repositories.Count(r => r.Attention == AttentionLevel.Warning);
        SummaryText = HasRepositories
            ? string.Create(CultureInfo.InvariantCulture, $"{Repositories.Count} repositories")
                + (failing > 0 ? $" · {failing} failing" : "")
                + (warnings > 0 ? $" · {warnings} need attention" : "")
            : "";
    }
}
