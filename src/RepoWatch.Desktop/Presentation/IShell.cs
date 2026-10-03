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

    /// <summary>Opens the data folder in the file manager. Never throws; returns false if it could not be opened.</summary>
    Task<bool> OpenDataFolderAsync();

    void Quit();
}

/// <summary>Marshals work to the UI thread.</summary>
public interface IUiDispatcher
{
    void Post(Action action);
}
