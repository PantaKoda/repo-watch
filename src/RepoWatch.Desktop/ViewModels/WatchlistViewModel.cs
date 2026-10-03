using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RepoWatch.Core.Access;
using RepoWatch.Core.Platform;
using RepoWatch.Core.Settings;
using RepoWatch.Desktop.Presentation;
using RepoWatch.Desktop.Services;

namespace RepoWatch.Desktop.ViewModels;

/// <summary>A selectable workflow for a repository's workflow filter.</summary>
public sealed partial class WorkflowChoiceViewModel(long id, string name, string path, bool isSelected, Action<long, bool> toggle) : ObservableObject
{
    public long Id { get; } = id;

    public string Name { get; } = name;

    public string Path { get; } = path;

    [ObservableProperty]
    public partial bool IsSelected { get; set; } = isSelected;

    partial void OnIsSelectedChanged(bool value) => toggle(Id, value);
}

/// <summary>One watched repository: order controls, removal, access state and per-repository options.</summary>
public sealed partial class WatchedItemViewModel : ObservableObject
{
    private readonly WatchlistService _watchlist;
    private readonly AccessCatalogService _catalog;
    private readonly IExternalBrowser _browser;
    private bool _loading;
    private bool _branchesEdited;

    public WatchedItemViewModel(WatchedRepository entry, WatchlistService watchlist, AccessCatalogService catalog, IExternalBrowser browser)
    {
        Id = entry.RepositoryId;
        _watchlist = watchlist;
        _catalog = catalog;
        _browser = browser;
        Update(entry);
    }

    public long Id { get; }

    public IReadOnlyList<PullRequestScope> PullRequestScopes { get; } = Enum.GetValues<PullRequestScope>();

    public ObservableCollection<WorkflowChoiceViewModel> Workflows { get; } = [];

    [ObservableProperty] public partial string Name { get; private set; } = "";

    [ObservableProperty] public partial string AccessText { get; private set; } = "";

    [ObservableProperty] public partial StatusTone AccessTone { get; private set; }

    [ObservableProperty] public partial bool CanManageAccess { get; private set; }

    [ObservableProperty] public partial PullRequestScope PullRequests { get; set; }

    [ObservableProperty] public partial bool ShowIssues { get; set; }

    [ObservableProperty] public partial bool NotificationsEnabled { get; set; }

    /// <summary>Extra branches whose workflow health is shown, comma-separated. Empty means the default branch.</summary>
    [ObservableProperty] public partial string Branches { get; set; } = "";

    [ObservableProperty] public partial string WorkflowSummary { get; private set; } = "";

    [ObservableProperty] public partial string? WorkflowStatus { get; private set; }

    [ObservableProperty] public partial bool IsFirst { get; set; }

    [ObservableProperty] public partial bool IsLast { get; set; }

    private Uri? AccessUrl { get; set; }

    public void Update(WatchedRepository entry)
    {
        _loading = true;
        Name = entry.Owner.Length > 0 ? $"{entry.Owner}/{entry.Name}" : $"Repository {entry.RepositoryId}";
        PullRequests = entry.PullRequests;
        ShowIssues = entry.ShowIssues;
        NotificationsEnabled = entry.NotificationsEnabled;
        if (!_branchesEdited)
        {
            Branches = string.Join(", ", entry.Branches); // never overwrite text the user hasn't saved yet
        }

        WorkflowSummary = entry.WorkflowIds.Count == 0 ? "All workflows" : $"{entry.WorkflowIds.Count} selected workflow(s)";
        foreach (var workflow in Workflows)
        {
            workflow.IsSelected = entry.WorkflowIds.Contains(workflow.Id);
        }

        var catalog = _catalog.Catalog;
        if (catalog is null)
        {
            (AccessText, AccessTone, AccessUrl) = ("Access not checked yet. Open the Add repositories tab or refresh.", StatusTone.Unknown, null);
        }
        else
        {
            var (access, installation) = AccessClassifier.Classify(entry.RepositoryId, entry.Owner, catalog);
            (AccessText, AccessTone, AccessUrl) = access switch
            {
                WatchedAccess.Granted => ("Access granted.", StatusTone.Success, installation?.Installation.ManageUrl),
                WatchedAccess.NotGranted => ("GitHub hasn't granted Repo Watch access to this repository (or the access was removed). Grant it on GitHub, or remove it here.", StatusTone.Failure, _catalog.InstallationUrl),
                WatchedAccess.Suspended => ("The owner's Repo Watch installation is suspended.", StatusTone.Failure, installation?.Installation.ManageUrl),
                WatchedAccess.SsoRequired => ("The organization requires single sign-on before Repo Watch can see this repository.", StatusTone.Warning, installation?.Error?.ActionUrl),
                _ when catalog.SsoHidesInstallations => ("Access couldn't be confirmed: GitHub hid organizations that require single sign-on. Authorize SSO on GitHub, then refresh.", StatusTone.Unknown, null),
                _ => ("Access couldn't be confirmed because the repository list is incomplete. Refresh to try again.", StatusTone.Unknown, null),
            };
        }

        CanManageAccess = AccessUrl is not null;
        _loading = false;
    }

    [RelayCommand]
    private void MoveUp() => _watchlist.Move(Id, -1);

    [RelayCommand]
    private void MoveDown() => _watchlist.Move(Id, 1);

    /// <summary>Stops watching in the widget only; GitHub access is unchanged.</summary>
    [RelayCommand]
    private void Remove() => _watchlist.Remove([Id]);

    [RelayCommand]
    private async Task ManageAccessAsync()
    {
        if (AccessUrl is { } url)
        {
            await _browser.OpenAsync(url);
        }
    }

    [RelayCommand]
    private async Task LoadWorkflowsAsync()
    {
        var entry = _watchlist.Repositories.FirstOrDefault(w => w.RepositoryId == Id);
        var account = _watchlist.Account;
        if (entry is null || account is null)
        {
            return;
        }

        // Use the repository's current name from the catalog (matched by ID) so a rename or
        // transfer doesn't send the request to the old name.
        var current = _catalog.Catalog?.Repositories.FirstOrDefault(r => r.Id == Id);
        var (owner, name) = current is not null ? (current.Owner, current.Name) : (entry.Owner, entry.Name);
        if (owner.Length == 0)
        {
            WorkflowStatus = "Refresh the repository list first.";
            return;
        }

        WorkflowStatus = "Loading workflows…";
        var result = await _catalog.ListWorkflowsAsync(owner, name, CancellationToken.None);
        if (_watchlist.Account != account || !_watchlist.IsWatched(Id))
        {
            WorkflowStatus = null; // signed out, switched account or removed meanwhile: discard
            return;
        }

        if (!result.IsSuccess)
        {
            WorkflowStatus = $"Couldn't load workflows: {result.Error!.Message}";
            return;
        }

        Workflows.Clear();
        foreach (var workflow in result.Value!.Items)
        {
            Workflows.Add(new WorkflowChoiceViewModel(workflow.Id, workflow.Name, workflow.Path, entry.WorkflowIds.Contains(workflow.Id), ToggleWorkflow));
        }

        WorkflowStatus = Workflows.Count == 0 ? "This repository has no workflows." : "Select workflows to show; none selected shows all.";
    }

    partial void OnPullRequestsChanged(PullRequestScope value) => Save(w => w with { PullRequests = value });

    partial void OnShowIssuesChanged(bool value) => Save(w => w with { ShowIssues = value });

    partial void OnNotificationsEnabledChanged(bool value) => Save(w => w with { NotificationsEnabled = value });

    partial void OnBranchesChanged(string value)
    {
        if (!_loading)
        {
            _branchesEdited = true;
        }
    }

    [RelayCommand]
    private void SaveBranches()
    {
        _branchesEdited = false;
        Save(w => w with
        {
            Branches = Branches.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToList(),
        });
    }

    private void ToggleWorkflow(long workflowId, bool selected)
    {
        if (_loading)
        {
            return;
        }

        Save(w => w with
        {
            WorkflowIds = selected ? w.WorkflowIds.Append(workflowId).Distinct().ToList() : w.WorkflowIds.Where(id => id != workflowId).ToList(),
        });
    }

    private void Save(Func<WatchedRepository, WatchedRepository> change)
    {
        if (!_loading)
        {
            _watchlist.UpdateRepository(Id, change);
        }
    }
}

/// <summary>The watched repositories in manual order, plus the ordering preference.</summary>
public sealed partial class WatchlistViewModel : ObservableObject, IDisposable
{
    private readonly WatchlistService _watchlist;
    private readonly AccessCatalogService _catalog;
    private readonly IExternalBrowser _browser;
    private bool _syncing;

    public WatchlistViewModel(WatchlistService watchlist, AccessCatalogService catalog, IExternalBrowser browser)
    {
        _watchlist = watchlist;
        _catalog = catalog;
        Links = new LinkNotice(browser);
        _browser = Links;
        _watchlist.Changed += OnChanged;
        _catalog.Changed += OnChanged;
        Sync();
    }

    public ObservableCollection<WatchedItemViewModel> Items { get; } = [];

    /// <summary>Feedback when a Manage access link could not be opened.</summary>
    public LinkNotice Links { get; }

    [ObservableProperty] public partial bool IsEmpty { get; private set; }

    [ObservableProperty] public partial bool AttentionFirst { get; set; }

    [ObservableProperty] public partial bool ManualOrder { get; set; }

    [ObservableProperty] public partial bool RecentActivityFirst { get; set; }

    /// <summary>Raised when the user asks to add repositories from the empty state.</summary>
    public event EventHandler? AddRequested;

    public void Dispose()
    {
        _watchlist.Changed -= OnChanged;
        _catalog.Changed -= OnChanged;
    }

    [RelayCommand]
    private void AddRepositories() => AddRequested?.Invoke(this, EventArgs.Empty);

    partial void OnAttentionFirstChanged(bool value)
    {
        if (value && !_syncing)
        {
            _watchlist.SetOrdering(RepositoryOrdering.AttentionFirst);
        }
    }

    partial void OnManualOrderChanged(bool value)
    {
        if (value && !_syncing)
        {
            _watchlist.SetOrdering(RepositoryOrdering.Manual);
        }
    }

    partial void OnRecentActivityFirstChanged(bool value)
    {
        if (value && !_syncing)
        {
            _watchlist.SetOrdering(RepositoryOrdering.RecentActivity);
        }
    }

    private void OnChanged(object? sender, EventArgs e) => Sync();

    private void Sync()
    {
        _syncing = true;
        var entries = _watchlist.Repositories;
        CollectionReconciler.Reconcile(Items, entries, w => w.RepositoryId, i => i.Id,
            w => new WatchedItemViewModel(w, _watchlist, _catalog, _browser), (i, w) => i.Update(w), allowReorder: true);
        for (var i = 0; i < Items.Count; i++)
        {
            Items[i].IsFirst = i == 0;
            Items[i].IsLast = i == Items.Count - 1;
        }

        IsEmpty = Items.Count == 0;
        AttentionFirst = _watchlist.Current.Ordering == RepositoryOrdering.AttentionFirst;
        ManualOrder = _watchlist.Current.Ordering == RepositoryOrdering.Manual;
        RecentActivityFirst = _watchlist.Current.Ordering == RepositoryOrdering.RecentActivity;
        _syncing = false;
    }
}
