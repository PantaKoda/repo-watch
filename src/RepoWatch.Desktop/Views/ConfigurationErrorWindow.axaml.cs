using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using RepoWatch.Desktop.ViewModels;

namespace RepoWatch.Desktop.Views;

public partial class ConfigurationErrorWindow : Window
{
    public ConfigurationErrorWindow()
    {
        InitializeComponent();
    }

    private async void OnOpenFolderClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ConfigurationErrorViewModel vm)
        {
            var directory = Directory.CreateDirectory(vm.UserConfigDirectory);
            await Launcher.LaunchDirectoryInfoAsync(directory);
        }
    }

    private void OnQuitClick(object? sender, RoutedEventArgs e) => Close();
}
