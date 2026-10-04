using Avalonia.Data.Converters;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RepoWatch.Desktop.Uninstall;

namespace RepoWatch.Desktop.ViewModels;

/// <summary>
/// The uninstall confirmation: what will be removed (from the real paths), whether to keep the settings,
/// whether to open GitHub to revoke access, and one Uninstall button.
/// </summary>
public sealed partial class UninstallViewModel : ObservableObject
{
    private readonly UninstallService _uninstall;

    public UninstallViewModel(UninstallService uninstall)
    {
        _uninstall = uninstall;
        Items = uninstall.Describe(keepSettings: false);
    }

    [ObservableProperty]
    public partial IReadOnlyList<UninstallItem> Items { get; private set; }

    /// <summary>Remove everything: nothing of Repo Watch stays on this PC (the default).</summary>
    [ObservableProperty]
    public partial bool RemoveEverything { get; set; } = true;

    /// <summary>Keep only the settings and repository list for a later reinstall.</summary>
    [ObservableProperty]
    public partial bool KeepSettings { get; set; }

    [ObservableProperty]
    public partial bool OpenGitHubAccess { get; set; } = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UninstallCommand))]
    public partial bool IsRunning { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    public partial string? Message { get; private set; }

    public bool HasMessage => Message is not null;

    partial void OnKeepSettingsChanged(bool value)
    {
        if (value)
        {
            RemoveEverything = false;
        }

        Items = _uninstall.Describe(value);
    }

    partial void OnRemoveEverythingChanged(bool value)
    {
        if (value)
        {
            KeepSettings = false;
        }
    }

    private bool CanUninstall => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanUninstall))]
    private async Task UninstallAsync()
    {
        IsRunning = true;
        Message = "Removing Repo Watch…";
        Message = await _uninstall.UninstallAsync(new UninstallOptions(KeepSettings, OpenGitHubAccess)) ?? "Repo Watch is closing and removing its files.";
        IsRunning = false;
    }
}

/// <summary>"✕" for what is removed, "✓" for what is kept.</summary>
public static class UninstallMarks
{
    public static FuncValueConverter<bool, string> Mark { get; } = new(removed => removed ? "✕" : "✓");
}
