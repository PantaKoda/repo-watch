namespace RepoWatch.Desktop.Presentation;

/// <summary>Window-level actions view models can request without referencing windows.</summary>
public interface IShell
{
    /// <summary>True when a working tray icon can bring a hidden widget back.</summary>
    bool CanHideToTray { get; }

    void ShowWidget();

    /// <summary>Hides to the tray when available, otherwise minimizes so the taskbar can restore it.</summary>
    void HideWidget();

    void OpenSettings();

    /// <summary>Opens the repository manager on a tab (or the onboarding flow if it was never finished).</summary>
    void OpenRepositories(ViewModels.RepositoriesTab tab);

    /// <summary>Opens settings and starts GitHub sign-in.</summary>
    void BeginSignIn();

    /// <summary>Copies non-secret text (e.g. the device-flow user code). Never use for tokens.</summary>
    Task CopyTextAsync(string text);

    /// <summary>Opens the data folder in the file manager. Never throws; returns false if it could not be opened.</summary>
    Task<bool> OpenDataFolderAsync();

    /// <summary>Opens the update window: what changed since this version, and Install update.</summary>
    void OpenUpdate();

    /// <summary>Opens the uninstall window (what will be removed, keep settings or not, Uninstall).</summary>
    void OpenUninstall();

    void Quit();
}

/// <summary>Marshals work to the UI thread.</summary>
public interface IUiDispatcher
{
    void Post(Action action);
}
