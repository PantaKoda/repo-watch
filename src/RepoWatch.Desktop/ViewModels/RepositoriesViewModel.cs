using CommunityToolkit.Mvvm.ComponentModel;
using RepoWatch.Core.Platform;
using RepoWatch.Desktop.Services;

namespace RepoWatch.Desktop.ViewModels;

public enum RepositoriesTab
{
    Watched = 0,
    Add = 1,
    Access = 2,
}

/// <summary>The repository manager: watched repositories, adding more, and GitHub access.</summary>
public sealed partial class RepositoriesViewModel : ObservableObject, IDisposable
{
    public RepositoriesViewModel(AccessCatalogService catalog, WatchlistService watchlist, IExternalBrowser browser, TimeProvider time)
    {
        Watchlist = new WatchlistViewModel(watchlist, catalog, browser);
        Picker = new RepositoryPickerViewModel(catalog, watchlist, browser, time);
        Access = new AccessViewModel(catalog, browser);
        Watchlist.AddRequested += (_, _) => SelectedTab = (int)RepositoriesTab.Add;
        _ = catalog.EnsureLoadedAsync();
    }

    public WatchlistViewModel Watchlist { get; }

    public RepositoryPickerViewModel Picker { get; }

    public AccessViewModel Access { get; }

    [ObservableProperty]
    public partial int SelectedTab { get; set; }

    public void Dispose()
    {
        Watchlist.Dispose();
        Picker.Dispose();
        Access.Dispose();
    }
}
