using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using RepoWatch.Desktop.Infrastructure;
using RepoWatch.Desktop.ViewModels;
using RepoWatch.Desktop.Views;

namespace RepoWatch.Desktop;

public partial class App : Application
{
    private readonly IServiceProvider? _services;

    // Used by the designer/previewer, which has no composition root.
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
            desktop.MainWindow = configuration.IsValid
                ? new MainWindow { DataContext = _services.GetRequiredService<MainWindowViewModel>() }
                : new ConfigurationErrorWindow { DataContext = _services.GetRequiredService<ConfigurationErrorViewModel>() };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
