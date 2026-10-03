using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RepoWatch.Core.Settings;
using RepoWatch.Desktop.Infrastructure;
using RepoWatch.Desktop.Platform;
using RepoWatch.Desktop.Platform.Tray;
using RepoWatch.Desktop.Platform.Windowing;
using RepoWatch.Desktop.Presentation;
using RepoWatch.Desktop.Services;
using RepoWatch.Desktop.ViewModels;
using RepoWatch.Desktop.Views;

namespace RepoWatch.Desktop.Shell;

/// <summary>
/// Owns the widget and settings windows, the tray icon and app lifetime. A hidden widget can
/// always be recovered: from the tray where available, otherwise from the taskbar.
/// </summary>
public sealed class AppShell(
    IServiceProvider services,
    SettingsService settings,
    MonitorHost monitors,
    WindowPlacementService placement,
    TrayService tray,
    AppPaths paths,
    ILogger<AppShell> logger) : IShell, IDisposable
{
    public const string DemoArgument = "--demo";

    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private Application? _application;
    private WidgetWindow? _widget;
    private WidgetViewModel? _widgetViewModel;
    private SettingsWindow? _settingsWindow;
    private DispatcherTimer? _clock;
    private bool _quitting;

    public bool CanHideToTray => tray.IsAvailable;

    public void Start(IClassicDesktopStyleApplicationLifetime desktop, Application application)
    {
        ArgumentNullException.ThrowIfNull(desktop);
        _desktop = desktop;
        _application = application;

        settings.Load();
        ApplyAppearance();
        settings.AppChanged += (_, _) => Dispatcher.UIThread.Post(ApplyAppearance);

        if (desktop.Args?.Contains(DemoArgument, StringComparer.OrdinalIgnoreCase) == true)
        {
            monitors.EnterDemo();
        }

        var icon = AppIconFactory.Create();
        tray.Initialize(application, icon, ToggleWidget, ShowWidget, OpenSettings, Quit);
        desktop.ShutdownMode = CanHideToTray ? ShutdownMode.OnExplicitShutdown : ShutdownMode.OnMainWindowClose;

        _widgetViewModel = ActivatorUtilities.CreateInstance<WidgetViewModel>(services, this);
        _widget = new WidgetWindow
        {
            DataContext = _widgetViewModel,
            Icon = icon,
            ShowInTaskbar = !CanHideToTray,
        };
        placement.Attach(_widget);
        _widget.Closing += OnWidgetClosing;
        desktop.MainWindow = _widget;
        desktop.ShutdownRequested += (_, _) => settings.Flush();

        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _clock.Tick += (_, _) => _widgetViewModel.Tick();
        _clock.Start();

        logger.LogInformation("Shell started; tray {Tray}, demo {Demo}", CanHideToTray ? "available" : "unavailable", monitors.IsDemo);
    }

    public void ShowWidget()
    {
        if (_widget is null)
        {
            return;
        }

        _widget.Show();
        if (_widget.WindowState == WindowState.Minimized)
        {
            _widget.WindowState = WindowState.Normal;
        }

        placement.EnsureReachable(_widget);
        _widget.Activate();
    }

    public void HideWidget()
    {
        if (_widget is null)
        {
            return;
        }

        if (CanHideToTray)
        {
            _widget.Hide();
        }
        else
        {
            _widget.WindowState = WindowState.Minimized;
        }
    }

    public void OpenSettings()
    {
        if (_settingsWindow is null)
        {
            var viewModel = ActivatorUtilities.CreateInstance<SettingsViewModel>(services, this);
            _settingsWindow = new SettingsWindow { DataContext = viewModel, Icon = _widget?.Icon };
            _settingsWindow.Closed += (_, _) =>
            {
                viewModel.Dispose();
                _settingsWindow = null;
            };
        }

        _settingsWindow.Show();
        if (_settingsWindow.WindowState == WindowState.Minimized)
        {
            _settingsWindow.WindowState = WindowState.Normal;
        }

        _settingsWindow.Activate();
    }

    public async void OpenDataFolder()
    {
        try
        {
            var directory = Directory.CreateDirectory(paths.DataDirectory);
            var launcher = (_settingsWindow as TopLevel ?? _widget)?.Launcher;
            if (launcher is not null)
            {
                await launcher.LaunchDirectoryInfoAsync(directory);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not open the data folder");
        }
    }

    public void Quit()
    {
        _quitting = true;
        settings.Flush();
        _settingsWindow?.Close();
        _desktop?.Shutdown();
    }

    public void Dispose()
    {
        _clock?.Stop();
        _widgetViewModel?.Dispose();
    }

    private void ToggleWidget()
    {
        if (_widget is { IsVisible: true } && _widget.WindowState != WindowState.Minimized)
        {
            HideWidget();
        }
        else
        {
            ShowWidget();
        }
    }

    private void OnWidgetClosing(object? sender, WindowClosingEventArgs e)
    {
        // With a tray icon, closing hides the widget; Quit is in the tray menu and settings.
        if (CanHideToTray && !_quitting && !e.IsProgrammatic)
        {
            e.Cancel = true;
            HideWidget();
        }
    }

    private void ApplyAppearance()
    {
        if (_application is null)
        {
            return;
        }

        _application.RequestedThemeVariant = settings.App.Appearance.Theme switch
        {
            ThemePreference.Light => ThemeVariant.Light,
            ThemePreference.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }
}
