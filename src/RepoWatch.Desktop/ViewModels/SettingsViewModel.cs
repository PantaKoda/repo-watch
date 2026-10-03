using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    private readonly WatchlistService _watchlist;
    private readonly VisualStateService _visuals;
    private bool _applying;

    public SettingsViewModel(SettingsService settings, MonitorHost monitors, IShell shell, IUiDispatcher dispatcher, AccountViewModel account, WatchlistService watchlist, AppPaths paths, VisualStateService visuals)
    {
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

        Version = AppInfo.Version;
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
