using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RepoWatch.Core.Access;
using RepoWatch.Core.Identity;
using RepoWatch.Core.Platform;
using RepoWatch.Desktop.Presentation;
using RepoWatch.Desktop.Services;

namespace RepoWatch.Desktop.ViewModels;

/// <summary>One repository GitHub has granted Repo Watch, with its checkbox for the widget's watchlist.</summary>
public sealed partial class RepositoryChoiceViewModel : ObservableObject
{
    private readonly Action<RepositoryChoiceViewModel, bool> _toggle;
    private bool _syncing;

    public RepositoryChoiceViewModel(AccessibleRepository repository, bool isSelected, Action<RepositoryChoiceViewModel, bool> toggle)
    {
        Repository = repository;
        _toggle = toggle;
        _syncing = true;
        IsSelected = isSelected;
        _syncing = false;

        var badges = new List<string>();
        if (repository.IsPrivate)
        {
            badges.Add("private");
        }

        badges.Add(repository.OwnerKind == RepositoryOwnerKind.Organization ? "organization" : "personal");
        if (repository.IsArchived)
        {
            badges.Add("archived");
        }

        Badges = string.Join(" · ", badges);
    }

    public AccessibleRepository Repository { get; }

    public long Id => Repository.Id;

    public string FullName => Repository.FullName;

    public string Badges { get; }

    /// <summary>Untrusted repository text; displayed as plain text only.</summary>
    public string? Description => Repository.Description;

    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    internal void Sync(bool isSelected)
    {
        _syncing = true;
        IsSelected = isSelected;
        _syncing = false;
    }

    partial void OnIsSelectedChanged(bool value)
    {
        if (!_syncing)
        {
            _toggle(this, value);
        }
    }
}

/// <summary>
/// Chooses which granted repositories the widget watches. Selection is separate from access:
/// checking a box never changes GitHub permissions, and access never selects anything by itself.
/// </summary>
public sealed partial class RepositoryPickerViewModel : ObservableObject, IDisposable
{
    private const string AllOwners = "All owners";

    private readonly AccessCatalogService _catalog;
    private readonly WatchlistService _watchlist;
    private readonly IExternalBrowser _browser;
    private readonly TimeProvider _time;
    private readonly List<RepositoryChoiceViewModel> _all = [];

    public RepositoryPickerViewModel(AccessCatalogService catalog, WatchlistService watchlist, IExternalBrowser browser, TimeProvider time)
    {
        _time = time;
        _catalog = catalog;
        _watchlist = watchlist;
        _browser = browser;
        _catalog.Changed += OnCatalogChanged;
        _watchlist.Changed += OnWatchlistChanged;
        Rebuild();
    }

    public ObservableCollection<RepositoryChoiceViewModel> Shown { get; } = [];

    public ObservableCollection<string> Owners { get; } = [AllOwners];

    [ObservableProperty]
    public partial string? SearchText { get; set; }

    [ObservableProperty]
    public partial string? OwnerFilter { get; set; } = AllOwners;

    [ObservableProperty]
    public partial bool ShowSelectedOnly { get; set; }

    [ObservableProperty] public partial bool IsLoading { get; private set; }

    [ObservableProperty] public partial string? StatusText { get; private set; }

    [ObservableProperty] public partial bool HasError { get; private set; }

    [ObservableProperty] public partial string SelectedCountText { get; private set; } = "";

    [ObservableProperty] public partial string ShownText { get; private set; } = "";

    /// <summary>Says exactly which repositories a bulk action affects.</summary>
    [ObservableProperty] public partial string BulkScopeText { get; private set; } = "";

    [ObservableProperty] public partial string AddShownLabel { get; private set; } = "";

    [ObservableProperty] public partial string RemoveShownLabel { get; private set; } = "";

    [ObservableProperty] public partial bool CanAddShown { get; private set; }

    [ObservableProperty] public partial bool CanRemoveShown { get; private set; }

    /// <summary>No installation at all: the user still has to grant access on GitHub.</summary>
    [ObservableProperty] public partial bool ShowGrantAccess { get; private set; }

    [ObservableProperty] public partial bool ShowEmpty { get; private set; }

    [ObservableProperty] public partial string? EmptyText { get; private set; }

    public void Dispose()
    {
        _catalog.Changed -= OnCatalogChanged;
        _watchlist.Changed -= OnWatchlistChanged;
    }

    [RelayCommand]
    private Task RefreshAsync() => _catalog.RefreshAsync();

    /// <summary>Adds every repository currently shown (all of them when no filter is active).</summary>
    [RelayCommand]
    private void AddShown() => _watchlist.Add(Shown.Where(c => !c.IsSelected).Select(c => c.Repository).ToList());

    /// <summary>Removes every repository currently shown from the widget (does not change GitHub access).</summary>
    [RelayCommand]
    private void RemoveShown() => _watchlist.Remove(Shown.Where(c => c.IsSelected).Select(c => c.Id).ToList());

    [RelayCommand]
    private void ClearFilters()
    {
        SearchText = null;
        OwnerFilter = AllOwners;
        ShowSelectedOnly = false;
    }

    [RelayCommand]
    private async Task GrantAccessAsync()
    {
        if (_catalog.InstallationUrl is { } url)
        {
            await _browser.OpenAsync(url);
        }
    }

    partial void OnSearchTextChanged(string? value) => ApplyFilter();

    partial void OnOwnerFilterChanged(string? value) => ApplyFilter();

    partial void OnShowSelectedOnlyChanged(bool value) => ApplyFilter();

    private void OnCatalogChanged(object? sender, EventArgs e) => Rebuild();

    private void OnWatchlistChanged(object? sender, WatchlistChangedEventArgs e)
    {
        foreach (var choice in _all)
        {
            choice.Sync(_watchlist.IsWatched(choice.Id));
        }

        ApplyFilter();
    }

    private void Toggle(RepositoryChoiceViewModel choice, bool selected)
    {
        if (selected)
        {
            _watchlist.Add([choice.Repository]);
        }
        else
        {
            _watchlist.Remove([choice.Id]);
        }
    }

    private void Rebuild()
    {
        var catalog = _catalog.Catalog;
        IsLoading = _catalog.Status == CatalogStatus.Loading;
        HasError = _catalog.Status == CatalogStatus.Failed;
        StatusText = _catalog.Status switch
        {
            CatalogStatus.Loading => "Loading the repositories GitHub has granted Repo Watch…",
            CatalogStatus.Failed => $"Couldn't load the list: {_catalog.Error?.Message}",
            CatalogStatus.Loaded when catalog is { IsComplete: false } => "Some repositories couldn't be listed. See the Access tab for details.",
            CatalogStatus.Loaded => $"Updated {TimeText.Ago(catalog!.LoadedAt, _time.GetUtcNow())}.",
            _ => null,
        };

        _all.Clear();
        if (catalog is not null)
        {
            _all.AddRange(catalog.Repositories.Select(r => new RepositoryChoiceViewModel(r, _watchlist.IsWatched(r.Id), Toggle)));
        }

        var owners = catalog?.Owners ?? [];
        var keepOwner = OwnerFilter;
        Owners.Clear();
        Owners.Add(AllOwners);
        foreach (var owner in owners)
        {
            Owners.Add(owner);
        }

        OwnerFilter = keepOwner is not null && Owners.Contains(keepOwner) ? keepOwner : AllOwners;

        ShowGrantAccess = _catalog.Status == CatalogStatus.Loaded && catalog!.Installations.Count == 0;
        ApplyFilter();
    }

    private static string Plural(int count) => count == 1 ? "1 repository" : string.Create(CultureInfo.InvariantCulture, $"{count} repositories");

    private void ApplyFilter()
    {
        var filter = new PickerFilter(SearchText, OwnerFilter == AllOwners ? null : OwnerFilter, ShowSelectedOnly);
        var shown = _all.Where(c => filter.Matches(c.Repository, c.IsSelected)).ToList();
        CollectionReconciler.Reconcile(Shown, shown, c => c.Id, c => c.Id, c => c, (_, _) => { }, allowReorder: true);

        var total = _all.Count;
        var selected = _all.Count(c => c.IsSelected);
        var notShownSelected = _catalog.Catalog is null ? 0 : _watchlist.Repositories.Count - selected;
        SelectedCountText = string.Create(CultureInfo.InvariantCulture, $"{selected} of {total} repositories selected for the widget")
            + (notShownSelected > 0 ? $" (plus {notShownSelected} watched repositories GitHub no longer lists)" : "");
        ShownText = filter.IsFiltered ? $"Showing {shown.Count} of {total}" : $"Showing all {total}";

        var toAdd = shown.Count(c => !c.IsSelected);
        var toRemove = shown.Count(c => c.IsSelected);
        BulkScopeText = filter.IsFiltered
            ? $"Bulk actions apply only to the {Plural(shown.Count)} shown by the current search and filters."
            : $"Bulk actions apply to all {Plural(total)} in the list.";
        AddShownLabel = filter.IsFiltered ? $"Add {toAdd} shown" : $"Add all ({toAdd})";
        RemoveShownLabel = filter.IsFiltered ? $"Remove {toRemove} shown" : $"Remove all ({toRemove})";
        CanAddShown = toAdd > 0;
        CanRemoveShown = toRemove > 0;

        ShowEmpty = !IsLoading && !ShowGrantAccess && shown.Count == 0 && _catalog.Status == CatalogStatus.Loaded;
        EmptyText = total == 0
            ? "Repo Watch has access, but no repositories are included. Use Manage access to choose repositories on GitHub."
            : "No repositories match. Clear the search or filters.";
    }
}
