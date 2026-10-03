using CommunityToolkit.Mvvm.ComponentModel;
using RepoWatch.Core.Configuration;
using RepoWatch.Desktop.Infrastructure;

namespace RepoWatch.Desktop.ViewModels;

/// <summary>Stage 01 baseline window: reports build and configuration state. Replaced by the widget in Stage 03.</summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    public MainWindowViewModel(RepoWatchOptions options, AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(paths);

        Version = AppInfo.Version;
        DataDirectory = paths.DataDirectory;
        SignInStatus = options.GitHub.IsSignInConfigured
            ? "GitHub App client ID configured."
            : "Sign-in not configured: set GitHub:ClientId (see README 'Configuration').";
        RelayStatus = options.Relay.IsConfigured
            ? $"Relay: {options.Relay.BaseUrl}"
            : "Relay not configured; polling only.";
    }

    public string Version { get; }

    public string DataDirectory { get; }

    public string SignInStatus { get; }

    public string RelayStatus { get; }
}
