using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RepoWatch.Core.Configuration;
using RepoWatch.Core.Settings;
using RepoWatch.Desktop.Infrastructure;
using RepoWatch.Desktop.Presentation;
using RepoWatch.Desktop.Services;

namespace RepoWatch.Desktop.ViewModels;

/// <summary>Settings window. Shows only settings that work in this build; later stages add more.</summary>
public sealed partial class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly SettingsService _settings;
    private readonly MonitorHost _monitors;
    private readonly IShell _shell;
    private readonly IUiDispatcher _dispatcher;
    private bool _applying;

    public SettingsViewModel(SettingsService settings, MonitorHost monitors, IShell shell, IUiDispatcher dispatcher, RepoWatchOptions options, AppPaths paths)
    {
        _settings = settings;
        _monitors = monitors;
        _shell = shell;
        _dispatcher = dispatcher;

        Version = AppInfo.Version;
        DataDirectory = paths.DataDirectory;
        SignInAvailability = options.GitHub.IsSignInConfigured
            ? "Signing in with GitHub is not available in this build yet."
            : "Signing in with GitHub is not available in this build yet, and no GitHub App client ID is configured.";

        _settings.AppChanged += OnSettingsChanged;
        _monitors.CurrentChanged += OnMonitorChanged;
        Load();
    }

    public IReadOnlyList<ThemePreference> Themes { get; } = Enum.GetValues<ThemePreference>();

    public string Version { get; }

    public string DataDirectory { get; }

    public string SignInAvailability { get; }

    public string TrayDescription => _shell.CanHideToTray
        ? "Closing or hiding the widget keeps Repo Watch running in the notification area. Use the tray icon's Quit to exit."
        : "No notification-area icon is available on this system, so hiding minimizes the widget and closing it exits.";

    [ObservableProperty]
    public partial bool AlwaysOnTop { get; set; }

    [ObservableProperty]
    public partial bool PositionLocked { get; set; }

    [ObservableProperty]
    public partial ThemePreference Theme { get; set; }

    [ObservableProperty]
    public partial bool IsDemo { get; private set; }

    [ObservableProperty]
    public partial string? StorageProblem { get; private set; }

    [ObservableProperty]
    public partial bool HasStorageProblem { get; private set; }

    public void Dispose()
    {
        _settings.AppChanged -= OnSettingsChanged;
        _monitors.CurrentChanged -= OnMonitorChanged;
    }

    partial void OnAlwaysOnTopChanged(bool value) => Save(s => s with { Window = s.Window with { AlwaysOnTop = value } });

    partial void OnPositionLockedChanged(bool value) => Save(s => s with { Window = s.Window with { PositionLocked = value } });

    partial void OnThemeChanged(ThemePreference value) => Save(s => s with { Appearance = s.Appearance with { Theme = value } });

    [RelayCommand]
    private void EnterDemo() => _monitors.EnterDemo();

    [RelayCommand]
    private void ExitDemo() => _monitors.ExitDemo();

    [RelayCommand]
    private void OpenDataFolder() => _shell.OpenDataFolder();

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
            Theme = app.Appearance.Theme;
            IsDemo = _monitors.IsDemo;
            StorageProblem = _settings.Problem;
            HasStorageProblem = StorageProblem is not null;
        }
        finally
        {
            _applying = false;
        }
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => _dispatcher.Post(Load);

    private void OnMonitorChanged(object? sender, EventArgs e) => IsDemo = _monitors.IsDemo;
}
