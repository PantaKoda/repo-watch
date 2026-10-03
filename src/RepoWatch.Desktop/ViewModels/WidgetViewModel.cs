using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    public WidgetViewModel(MonitorHost monitors, SettingsService settings, IShell shell, IExternalBrowser browser, TimeProvider time, IUiDispatcher dispatcher)
    {
        _monitors = monitors;
        _settings = settings;
        _shell = shell;
        _browser = browser;
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

    /// <summary>Updates relative times; called periodically by the shell.</summary>
    public void Tick()
    {
        var now = _time.GetUtcNow();
        foreach (var row in Repositories)
        {
            row.Tick(now);
        }
    }

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

    private void OnSettingsChanged(object? sender, EventArgs e) => _dispatcher.Post(() =>
    {
        ApplySettings();
        Sync();
    });

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

        // Watchlist ordering is per account (Stage 05); attention-first is the default.
        var ordered = AttentionPolicy.Order(_monitor.Repositories, RepositoryOrdering.AttentionFirst);
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
        ShowEmptyWatchlist = !ShowSignedOutState && !HasRepositories;

        var failing = Repositories.Count(r => r.Attention == AttentionLevel.Failure);
        var warnings = Repositories.Count(r => r.Attention == AttentionLevel.Warning);
        SummaryText = HasRepositories
            ? string.Create(CultureInfo.InvariantCulture, $"{Repositories.Count} repositories")
                + (failing > 0 ? $" · {failing} failing" : "")
                + (warnings > 0 ? $" · {warnings} need attention" : "")
            : "";
    }
}
