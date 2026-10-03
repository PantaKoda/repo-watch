using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RepoWatch.Core.Configuration;
using RepoWatch.Core.Platform;
using RepoWatch.Core.Settings;
using RepoWatch.Desktop.Infrastructure;
using RepoWatch.Desktop.Presentation;
using RepoWatch.Desktop.Services;

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
    private readonly IExternalBrowser? _browser;
    private readonly Uri? _releasesUrl;
    private bool _applying;

    public SettingsViewModel(SettingsService settings, MonitorHost monitors, IShell shell, IUiDispatcher dispatcher, AccountViewModel account, WatchlistService watchlist, AppPaths paths, VisualStateService visuals, DesktopIntegration? integration = null,
        IExternalBrowser? browser = null, RepoWatchOptions? options = null)
    {
        _browser = browser;
        _releasesUrl = options is not null && Uri.TryCreate(options.GitHub.WebBaseUrl, UriKind.Absolute, out var web) ? options.Updates.ResolveFor(web) : null;
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

    /// <summary>True when a releases page is configured. Updates are never downloaded or installed by the app.</summary>
    public bool CanCheckForUpdates => _browser is not null && _releasesUrl is not null;

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        if (_browser is null || _releasesUrl is null)
        {
            return;
        }

        ActionMessage = await _browser.OpenAsync(_releasesUrl) switch
        {
            LinkOpenResult.Opened => $"Opened the releases page. You have Repo Watch {Version}; download a newer release there if one is listed.",
            _ => $"Couldn't open a browser. Releases are listed at {_releasesUrl}",
        };
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
