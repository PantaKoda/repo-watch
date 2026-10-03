using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RepoWatch.Core.Configuration;
using RepoWatch.Core.Settings;
using RepoWatch.Desktop.Infrastructure;
using RepoWatch.Desktop.Presentation;
using RepoWatch.Desktop.Services;
using RepoWatch.Desktop.Updates;

namespace RepoWatch.Desktop.ViewModels;

/// <summary>Settings window: account, repositories, monitoring, notifications, desktop behavior, appearance and About.</summary>
public sealed partial class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly SettingsService _settings;
    private readonly MonitorHost _monitors;
    private readonly IShell _shell;
    private readonly IUiDispatcher _dispatcher;
    private readonly WatchlistService _watchlist;
    private readonly VisualStateService _visuals;
    private readonly DesktopIntegration? _integration;
    private readonly UpdateService? _updates;
    private bool _applying;

    public SettingsViewModel(SettingsService settings, MonitorHost monitors, IShell shell, IUiDispatcher dispatcher, AccountViewModel account, WatchlistService watchlist, AppPaths paths, VisualStateService visuals, DesktopIntegration? integration = null,
        UpdateService? updates = null, RepoWatchOptions? options = null)
    {
        _updates = updates;
        var polling = (options ?? new RepoWatchOptions()).Polling;
        DefaultRunningSeconds = polling.ActiveWorkflowSeconds;
        DefaultPullRequestsSeconds = polling.PullRequestSeconds;
        DefaultIssuesSeconds = polling.IssueSeconds;
        DefaultQuietSeconds = polling.QuietSeconds;
        _integration = integration;
        if (_integration is not null)
        {
            _integration.Changed += OnIntegrationChanged;
        }

        _visuals = visuals;
        _visuals.Changed += OnVisualsChanged;
        _settings = settings;
        _monitors = monitors;
        _shell = shell;
        _dispatcher = dispatcher;
        _watchlist = watchlist;
        Account = account;
        _watchlist.Changed += OnWatchlistChanged;
        UpdateRepositoriesSummary();

        Version = AppInfo.Display;
        DataDirectory = paths.DataDirectory;

        _settings.AppChanged += OnSettingsChanged;
        _settings.ProblemChanged += OnProblemChanged;
        _monitors.CurrentChanged += OnMonitorChanged;
        Load();
    }

    /// <summary>Result of the last action that could not complete, e.g. opening the data folder.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActionMessage))]
    public partial string? ActionMessage { get; private set; }

    public bool HasActionMessage => ActionMessage is not null;

    public IReadOnlyList<ThemePreference> Themes { get; } = Enum.GetValues<ThemePreference>();

    public IReadOnlyList<WindowMaterial> Materials { get; } = Enum.GetValues<WindowMaterial>();

    public IReadOnlyList<MotionPreference> MotionChoices { get; } = Enum.GetValues<MotionPreference>();

    public IReadOnlyList<AccentPreset> Accents => AccentPalette.Presets;

    public IReadOnlyList<Density> Densities { get; } = Enum.GetValues<Density>();

    /// <summary>Whole hours for quiet hours, shown as "22:00".</summary>
    public IReadOnlyList<string> Hours { get; } = Enumerable.Range(0, 24).Select(h => $"{h:00}:00").ToList();

    [ObservableProperty] public partial bool NotificationsEnabled { get; set; }

    [ObservableProperty] public partial bool NotifyCiFailure { get; set; }

    [ObservableProperty] public partial bool NotifyCiRecovery { get; set; }

    [ObservableProperty] public partial bool NotifyReviewRequested { get; set; }

    [ObservableProperty] public partial bool NotifyMerged { get; set; }

    /// <summary>Leave private repositories' names and titles out of notification text.</summary>
    [ObservableProperty] public partial bool HidePrivateDetails { get; set; }

    [ObservableProperty] public partial bool QuietHoursEnabled { get; set; }

    [ObservableProperty] public partial string QuietStart { get; set; } = "22:00";

    [ObservableProperty] public partial string QuietEnd { get; set; } = "07:00";

    [ObservableProperty] public partial string NotificationStatus { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartMinimized))]
    public partial bool StartAtLogin { get; set; }

    [ObservableProperty] public partial bool StartMinimized { get; set; }

    public bool CanStartMinimized => StartAtLogin && StartupSupported;

    public bool StartupSupported => _integration?.Startup.IsSupported == true;

    public string StartupStatus => StartupSupported
        ? "Off by default. You can also turn it off in Task Manager → Startup apps."
        : "Starting at sign-in isn't supported on this system yet.";

    [ObservableProperty] public partial bool ShowHideShortcut { get; set; }

    [ObservableProperty] public partial string ShortcutStatus { get; private set; } = "";

    public bool CanExportDiagnostics => _integration?.CanExportDiagnostics == true;

    public double MinOpacityPercent => AppearanceSettings.MinBackgroundOpacity * 100;

    public double MaxOpacityPercent => AppearanceSettings.MaxBackgroundOpacity * 100;

    public string Version { get; }

    public string DataDirectory { get; }

    public AccountViewModel Account { get; }

    [ObservableProperty]
    public partial string RepositoriesSummary { get; private set; } = "";

    public string TrayDescription => _shell.CanHideToTray
        ? "Closing or hiding the widget keeps Repo Watch running in the notification area. Use the tray icon's Quit to exit."
        : "No notification-area icon is available on this system, so hiding minimizes the widget and closing it exits.";

    [ObservableProperty]
    public partial bool AlwaysOnTop { get; set; }

    [ObservableProperty]
    public partial bool PositionLocked { get; set; }

    /// <summary>Stops all requests to GitHub until switched off; cached data stays visible.</summary>
    [ObservableProperty]
    public partial bool MonitoringPaused { get; set; }

    [ObservableProperty]
    public partial ThemePreference Theme { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OpacityAdjustable))]
    public partial WindowMaterial Material { get; set; }

    /// <summary>Background opacity in percent (20–100). Only the background layer fades; text stays opaque.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OpacityLabel))]
    public partial double OpacityPercent { get; set; }

    public string OpacityLabel => $"{Math.Round(OpacityPercent)}%";

    public bool OpacityAdjustable => Material != WindowMaterial.Solid;

    [ObservableProperty]
    public partial MotionPreference Motion { get; set; }

    /// <summary>Accent for the frame, brand and controls; status colors never change with it.</summary>
    [ObservableProperty]
    public partial AccentPreset Accent { get; set; } = AccentPalette.Presets[0];

    [ObservableProperty]
    public partial Density Density { get; set; }

    /// <summary>What the widget actually achieved, which can differ from the request.</summary>
    [ObservableProperty]
    public partial string VisualStatus { get; private set; } = "";

    [ObservableProperty]
    public partial bool IsDemo { get; private set; }

    [ObservableProperty]
    public partial string? StorageProblem { get; private set; }

    [ObservableProperty]
    public partial bool HasStorageProblem { get; private set; }

    public void Dispose()
    {
        if (_pendingRefreshSave is { } pending && !pending.IsCancellationRequested)
        {
            CommitRefresh(); // closing the window right after typing still saves the value
        }

        _settings.AppChanged -= OnSettingsChanged;
        _settings.ProblemChanged -= OnProblemChanged;
        _monitors.CurrentChanged -= OnMonitorChanged;
        _watchlist.Changed -= OnWatchlistChanged;
        _visuals.Changed -= OnVisualsChanged;
        if (_integration is not null)
        {
            _integration.Changed -= OnIntegrationChanged;
        }

        Account.Dispose();
    }

    [RelayCommand]
    private void ManageRepositories() => _shell.OpenRepositories(RepositoriesTab.Watched);

    private void OnWatchlistChanged(object? sender, WatchlistChangedEventArgs e) => _dispatcher.Post(UpdateRepositoriesSummary);

    private void UpdateRepositoriesSummary() => RepositoriesSummary = _watchlist.Account is null
        ? "Sign in to choose which repositories the widget watches."
        : _watchlist.Repositories.Count switch
        {
            0 => "The widget isn't watching any repositories yet.",
            1 => "The widget is watching 1 repository.",
            var n => $"The widget is watching {n} repositories.",
        };

    partial void OnAlwaysOnTopChanged(bool value) => Save(s => s with { Window = s.Window with { AlwaysOnTop = value } });

    partial void OnPositionLockedChanged(bool value) => Save(s => s with { Window = s.Window with { PositionLocked = value } });

    partial void OnMonitoringPausedChanged(bool value) => Save(s => s with { MonitoringPaused = value });

    // Refresh intervals: the defaults come from configuration (Polling:*); a value equal to its default is
    // stored as "use the default", so a later change of defaults still applies.
    public int DefaultRunningSeconds { get; }

    public int DefaultPullRequestsSeconds { get; }

    public int DefaultIssuesSeconds { get; }

    public int DefaultQuietSeconds { get; }

    public int MinRefreshSeconds => RefreshIntervals.MinSeconds;

    public int MaxRefreshSeconds => RefreshIntervals.MaxSeconds;

    [ObservableProperty] public partial decimal? RunningSeconds { get; set; }

    [ObservableProperty] public partial decimal? PullRequestsSeconds { get; set; }

    [ObservableProperty] public partial decimal? IssuesSeconds { get; set; }

    [ObservableProperty] public partial decimal? QuietSeconds { get; set; }

    /// <summary>True when every interval shows its default (Reset to defaults has nothing to do).</summary>
    [ObservableProperty] public partial bool RefreshIsDefault { get; private set; }

    /// <summary>A warning about a combination that is probably not intended, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRefreshHint))]
    public partial string? RefreshHint { get; private set; }

    public bool HasRefreshHint => RefreshHint is not null;

    /// <summary>
    /// The boxes update their value while the user types ("300" passes through "3" and "30"), so changes are
    /// saved this long after the last edit, or when the window closes. Zero saves at once (tests).
    /// </summary>
    public TimeSpan RefreshSaveDelay { get; set; } = TimeSpan.FromSeconds(1);

    private CancellationTokenSource? _pendingRefreshSave;

    partial void OnRunningSecondsChanged(decimal? value) => RefreshEdited();

    partial void OnPullRequestsSecondsChanged(decimal? value) => RefreshEdited();

    partial void OnIssuesSecondsChanged(decimal? value) => RefreshEdited();

    partial void OnQuietSecondsChanged(decimal? value) => RefreshEdited();

    [RelayCommand]
    private void ResetRefreshIntervals()
    {
        _pendingRefreshSave?.Cancel();
        Save(s => s with { Refresh = new RefreshIntervals() });
    }

    private void RefreshEdited()
    {
        if (_applying)
        {
            return;
        }

        UpdateRefreshState();
        _pendingRefreshSave?.Cancel();
        if (RefreshSaveDelay <= TimeSpan.Zero)
        {
            CommitRefresh();
            return;
        }

        var pending = _pendingRefreshSave = new CancellationTokenSource();
        _ = Task.Delay(RefreshSaveDelay, pending.Token).ContinueWith(
            t => _dispatcher.Post(() =>
            {
                if (!pending.IsCancellationRequested && ReferenceEquals(_pendingRefreshSave, pending))
                {
                    CommitRefresh();
                }
            }),
            CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
    }

    /// <summary>Saves what the boxes show. An empty box means "not decided yet" and keeps the saved value.</summary>
    private void CommitRefresh()
    {
        _pendingRefreshSave = null;
        Save(s => s with
        {
            Refresh = s.Refresh with
            {
                RunningWorkflowsSeconds = RunningSeconds is null ? s.Refresh.RunningWorkflowsSeconds : Choice(RunningSeconds, DefaultRunningSeconds),
                PullRequestsSeconds = PullRequestsSeconds is null ? s.Refresh.PullRequestsSeconds : Choice(PullRequestsSeconds, DefaultPullRequestsSeconds),
                IssuesSeconds = IssuesSeconds is null ? s.Refresh.IssuesSeconds : Choice(IssuesSeconds, DefaultIssuesSeconds),
                QuietSeconds = QuietSeconds is null ? s.Refresh.QuietSeconds : Choice(QuietSeconds, DefaultQuietSeconds),
            },
        });
    }

    private void UpdateRefreshState()
    {
        RefreshIsDefault = Shown(RunningSeconds, DefaultRunningSeconds) == DefaultRunningSeconds
            && Shown(PullRequestsSeconds, DefaultPullRequestsSeconds) == DefaultPullRequestsSeconds
            && Shown(IssuesSeconds, DefaultIssuesSeconds) == DefaultIssuesSeconds
            && Shown(QuietSeconds, DefaultQuietSeconds) == DefaultQuietSeconds;
        RefreshHint = Shown(RunningSeconds, DefaultRunningSeconds) > Shown(QuietSeconds, DefaultQuietSeconds)
            ? "Running workflows are now checked less often than quiet ones, so a run in progress updates more slowly than an idle repository."
            : null;
    }

    private static int Shown(decimal? value, int defaultSeconds) => value is { } v ? (int)Math.Round(v) : defaultSeconds;

    /// <summary>Null (default) when equal to the default; otherwise the value within the allowed range.</summary>
    private static int? Choice(decimal? value, int defaultSeconds)
    {
        if (value is null)
        {
            return null;
        }

        var seconds = RefreshIntervals.Clamp((int)Math.Round(value.Value))!.Value;
        return seconds == defaultSeconds ? null : seconds;
    }

    partial void OnThemeChanged(ThemePreference value) => Save(s => s with { Appearance = s.Appearance with { Theme = value } });

    partial void OnMaterialChanged(WindowMaterial value) => Save(s => s with { Appearance = s.Appearance with { Material = value } });

    partial void OnOpacityPercentChanged(double value) => Save(s => s with { Appearance = s.Appearance with { BackgroundOpacity = value / 100 } });

    partial void OnMotionChanged(MotionPreference value) => Save(s => s with { Appearance = s.Appearance with { Motion = value } });

    partial void OnAccentChanged(AccentPreset value) => Save(s => s with { Appearance = s.Appearance with { AccentColor = value?.Hex } });

    partial void OnDensityChanged(Density value) => Save(s => s with { Appearance = s.Appearance with { Density = value } });

    partial void OnNotificationsEnabledChanged(bool value) => Save(s => s with { Notifications = s.Notifications with { Enabled = value } });

    partial void OnNotifyCiFailureChanged(bool value) => Save(s => s with { Notifications = s.Notifications with { CiFailure = value } });

    partial void OnNotifyCiRecoveryChanged(bool value) => Save(s => s with { Notifications = s.Notifications with { CiRecovery = value } });

    partial void OnNotifyReviewRequestedChanged(bool value) => Save(s => s with { Notifications = s.Notifications with { ReviewRequested = value } });

    partial void OnNotifyMergedChanged(bool value) => Save(s => s with { Notifications = s.Notifications with { PullRequestMerged = value } });

    partial void OnHidePrivateDetailsChanged(bool value) => Save(s => s with { Notifications = s.Notifications with { HidePrivateDetails = value } });

    partial void OnQuietHoursEnabledChanged(bool value) => Save(s => s with { Notifications = s.Notifications with { QuietHours = s.Notifications.QuietHours with { Enabled = value } } });

    partial void OnQuietStartChanged(string value) => Save(s => s with { Notifications = s.Notifications with { QuietHours = s.Notifications.QuietHours with { Start = ParseHour(value) } } });

    partial void OnQuietEndChanged(string value) => Save(s => s with { Notifications = s.Notifications with { QuietHours = s.Notifications.QuietHours with { End = ParseHour(value) } } });

    partial void OnStartAtLoginChanged(bool value) => Save(s => s with { Startup = s.Startup with { StartAtLogin = value } });

    partial void OnStartMinimizedChanged(bool value) => Save(s => s with { Startup = s.Startup with { StartMinimized = value } });

    partial void OnShowHideShortcutChanged(bool value) => Save(s => s with { Window = s.Window with { ShowHideShortcut = value } });

    [RelayCommand]
    private void TestNotification()
    {
        var shown = _integration?.ShowTestNotification() == true;
        UpdateIntegrationStatus();
        if (!shown)
        {
            NotificationStatus += " The test notification wasn't shown.";
        }
    }

    [RelayCommand]
    private async Task ExportDiagnosticsAsync()
    {
        try
        {
            var file = _integration?.ExportDiagnostics();
            ActionMessage = file is null
                ? "Diagnostics aren't available in this build."
                : $"Saved {Path.GetFileName(file)} in the diagnostics folder of the data folder. It contains versions, states, counts and logs with secrets removed; no repository names or content.";
            if (file is not null)
            {
                await _shell.OpenDataFolderAsync();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ActionMessage = $"Couldn't write the diagnostics archive ({ex.GetType().Name}).";
        }
    }

    private static TimeOnly ParseHour(string value) =>
        int.TryParse(value.AsSpan(0, Math.Min(2, value.Length)), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var hour)
            ? new TimeOnly(Math.Clamp(hour, 0, 23), 0)
            : new TimeOnly(22, 0);

    private void OnIntegrationChanged(object? sender, EventArgs e) => _dispatcher.Post(UpdateIntegrationStatus);

    private void UpdateIntegrationStatus()
    {
        ShortcutStatus = _integration?.ShortcutStatus ?? "The Show/Hide shortcut isn't available here.";
        NotificationStatus = (_integration?.Notifications ?? Platform.Notifications.NotificationAvailability.Unsupported) switch
        {
            Platform.Notifications.NotificationAvailability.Available => "Notifications are shown by the system and follow its Do not disturb settings.",
            Platform.Notifications.NotificationAvailability.DisabledByUser => "Notifications for Repo Watch are turned off in the system settings (Settings → System → Notifications).",
            Platform.Notifications.NotificationAvailability.DisabledByPolicy => "Notifications are turned off by your organization's policy.",
            _ => "Notifications aren't supported in this build or on this system.",
        };
    }

    private void OnVisualsChanged(object? sender, EventArgs e) => _dispatcher.Post(() => VisualStatus = _visuals.Describe());

    [RelayCommand]
    private void EnterDemo() => _monitors.EnterDemo();

    [RelayCommand]
    private void ExitDemo() => _monitors.ExitDemo();

    [RelayCommand]
    private async Task OpenDataFolderAsync()
    {
        ActionMessage = await _shell.OpenDataFolderAsync()
            ? null
            : "Couldn't open the data folder in a file manager. Its path is shown above.";
    }

    /// <summary>True when updates are configured (Updates:Repository).</summary>
    public bool CanCheckForUpdates => _updates?.IsEnabled == true;

    /// <summary>Asks GitHub now. A newer release opens the update window; otherwise the result is shown here.</summary>
    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        if (_updates is null)
        {
            return;
        }

        ActionMessage = "Checking for updates…";
        var result = await _updates.CheckNowAsync();
        ActionMessage = result.Outcome switch
        {
            UpdateCheckOutcome.UpToDate => $"You have the latest version ({_updates.Current}). There is no newer release.",
            UpdateCheckOutcome.Available => $"Version {result.Latest!.Version} is available.",
            UpdateCheckOutcome.Busy => "An update check or install is already running.",
            UpdateCheckOutcome.Failed => $"Couldn't check for updates: {result.Error}",
            _ => "Updates are turned off in this build.",
        };
        if (result.Outcome == UpdateCheckOutcome.Available)
        {
            _shell.OpenUpdate();
        }
    }

    [RelayCommand]
    private void ShowWidget() => _shell.ShowWidget();

    [RelayCommand]
    private void Quit() => _shell.Quit();

    private void Save(Func<AppSettings, AppSettings> change)
    {
        if (!_applying)
        {
            _settings.UpdateApp(change);
        }
    }

    private void Load()
    {
        _applying = true;
        try
        {
            var app = _settings.App;
            AlwaysOnTop = app.Window.AlwaysOnTop;
            PositionLocked = app.Window.PositionLocked;
            MonitoringPaused = app.MonitoringPaused;
            RunningSeconds = app.Refresh.RunningWorkflowsSeconds ?? DefaultRunningSeconds;
            PullRequestsSeconds = app.Refresh.PullRequestsSeconds ?? DefaultPullRequestsSeconds;
            IssuesSeconds = app.Refresh.IssuesSeconds ?? DefaultIssuesSeconds;
            QuietSeconds = app.Refresh.QuietSeconds ?? DefaultQuietSeconds;
            UpdateRefreshState();
            Theme = app.Appearance.Theme;
            Material = app.Appearance.Material;
            OpacityPercent = app.Appearance.BackgroundOpacity * 100;
            Motion = app.Appearance.Motion;
            Accent = AccentPalette.Find(app.Appearance.AccentColor);
            Density = app.Appearance.Density;
            var notifications = app.Notifications;
            NotificationsEnabled = notifications.Enabled;
            NotifyCiFailure = notifications.CiFailure;
            NotifyCiRecovery = notifications.CiRecovery;
            NotifyReviewRequested = notifications.ReviewRequested;
            NotifyMerged = notifications.PullRequestMerged;
            HidePrivateDetails = notifications.HidePrivateDetails;
            QuietHoursEnabled = notifications.QuietHours.Enabled;
            QuietStart = $"{notifications.QuietHours.Start.Hour:00}:00";
            QuietEnd = $"{notifications.QuietHours.End.Hour:00}:00";
            StartAtLogin = app.Startup.StartAtLogin;
            StartMinimized = app.Startup.StartMinimized;
            ShowHideShortcut = app.Window.ShowHideShortcut;
            UpdateIntegrationStatus();
            VisualStatus = _visuals.Describe();
            IsDemo = _monitors.IsDemo;
            StorageProblem = _settings.Problem;
            HasStorageProblem = StorageProblem is not null;
        }
        finally
        {
            _applying = false;
        }
    }

    private void OnSettingsChanged(object? sender, AppSettingsChangedEventArgs e)
    {
        var (before, after) = (e.Previous, e.Current);
        if (before.Window.AlwaysOnTop != after.Window.AlwaysOnTop
            || before.Window.PositionLocked != after.Window.PositionLocked
            || before.MonitoringPaused != after.MonitoringPaused
            || (before.Refresh != after.Refresh && _pendingRefreshSave is null)
            || before.Notifications != after.Notifications
            || before.Startup != after.Startup
            || before.Window.ShowHideShortcut != after.Window.ShowHideShortcut
            || before.Appearance != after.Appearance)
        {
            _dispatcher.Post(Load);
        }
    }

    private void OnProblemChanged(object? sender, EventArgs e) => _dispatcher.Post(() =>
    {
        StorageProblem = _settings.Problem;
        HasStorageProblem = StorageProblem is not null;
    });

    private void OnMonitorChanged(object? sender, EventArgs e) => IsDemo = _monitors.IsDemo;
}
