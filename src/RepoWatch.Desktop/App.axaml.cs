using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using RepoWatch.Desktop.Infrastructure;
using RepoWatch.Desktop.Shell;
using RepoWatch.Desktop.ViewModels;
using RepoWatch.Desktop.Views;

namespace RepoWatch.Desktop;

public partial class App : Application
{
    private readonly IServiceProvider? _services;

    // Used by the designer/previewer and headless tests, which have no composition root.
    public App()
    {
    }

    internal App(IServiceProvider services)
    {
        _services = services;
    }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (_services is not null && ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var configuration = _services.GetRequiredService<ConfigurationLoadResult>();
            if (configuration.IsValid)
            {
                _services.GetRequiredService<AppShell>().Start(desktop, this);
            }
            else
            {
                desktop.MainWindow = new ConfigurationErrorWindow { DataContext = _services.GetRequiredService<ConfigurationErrorViewModel>() };
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
