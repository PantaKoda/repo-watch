using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RepoWatch.Core.Access;
using RepoWatch.Core.Identity;
using RepoWatch.Core.Platform;
using RepoWatch.Desktop.Presentation;
using RepoWatch.Desktop.Services;

namespace RepoWatch.Desktop.ViewModels;

/// <summary>One installation of the GitHub App: whose account, what it grants, and any problem.</summary>
public sealed partial class InstallationItemViewModel(InstallationAccess access, IExternalBrowser browser)
{
    public string Owner => access.Installation.AccountLogin;

    public string Kind => access.Installation.AccountKind == RepositoryOwnerKind.Organization ? "Organization" : "Personal account";

    public string Scope => access.Installation.Selection == RepositorySelection.All ? "All repositories" : "Selected repositories";

    public StatusTone Tone => access.Health switch
    {
        InstallationHealth.Ok when access.Installation.MissingPermissions.Count > 0 || access.SsoPartial => StatusTone.Warning,
        InstallationHealth.Ok => StatusTone.Success,
        InstallationHealth.Unavailable => StatusTone.Warning,
        _ => StatusTone.Failure,
    };

    public string Status => access.Health switch
    {
        InstallationHealth.Suspended => "Suspended: an owner must unsuspend the installation on GitHub before its repositories can be monitored.",
        InstallationHealth.SsoRequired => "This organization requires single sign-on. Authorize your SAML session on GitHub, then refresh.",
        InstallationHealth.Unavailable => $"Couldn't list repositories: {access.Error?.Message}",
        _ when access.Installation.MissingPermissions.Count > 0 =>
            $"Missing read permissions: {string.Join(", ", access.Installation.MissingPermissions)}. An owner may need to accept updated permissions on GitHub.",
        _ when access.SsoPartial => "Some repositories are hidden until you authorize single sign-on for this organization on GitHub. They're not removed; refresh after authorizing.",
        _ when !access.RepositoriesComplete => "Only part of the repository list could be loaded.",
        _ => "Access granted.",
    };

    public bool CanAuthorizeSso => access.Error?.ActionUrl is not null;

    public bool CanManage => access.Installation.ManageUrl is not null;

    [RelayCommand]
    private async Task ManageAsync()
    {
        if (access.Installation.ManageUrl is { } url)
        {
            await browser.OpenAsync(url);
        }
    }

    [RelayCommand]
    private async Task AuthorizeSsoAsync()
    {
        if (access.Error?.ActionUrl is { } url)
        {
            await browser.OpenAsync(url);
        }
    }
}

/// <summary>Where Repo Watch has been granted access on GitHub, and the links to change it.</summary>
public sealed partial class AccessViewModel : ObservableObject, IDisposable
{
    private readonly AccessCatalogService _catalog;
    private readonly IExternalBrowser _browser;

    public AccessViewModel(AccessCatalogService catalog, IExternalBrowser browser)
    {
        _catalog = catalog;
        Links = new LinkNotice(browser);
        _browser = Links;
        _catalog.Changed += OnChanged;
        Refresh();
    }

    public ObservableCollection<InstallationItemViewModel> Installations { get; } = [];

    /// <summary>Feedback when Grant access, Manage access or an SSO link could not be opened.</summary>
    public LinkNotice Links { get; }

    /// <summary>GitHub left out organizations that require single sign-on (X-GitHub-SSO: partial-results).</summary>
    [ObservableProperty] public partial string? SsoHiddenText { get; private set; }

    [ObservableProperty] public partial bool IsLoading { get; private set; }

    [ObservableProperty] public partial bool HasInstallations { get; private set; }

    [ObservableProperty] public partial bool ShowNoInstallations { get; private set; }

    [ObservableProperty] public partial string? StatusText { get; private set; }

    public bool CanGrantAccess => _catalog.InstallationUrl is not null;

    public void Dispose() => _catalog.Changed -= OnChanged;

    [RelayCommand]
    private Task RefreshAsync() => _catalog.RefreshAsync();

    [RelayCommand]
    private async Task GrantAccessAsync()
    {
        if (_catalog.InstallationUrl is { } url)
        {
            await _browser.OpenAsync(url);
        }
    }

    private void OnChanged(object? sender, EventArgs e) => Refresh();

    private void Refresh()
    {
        IsLoading = _catalog.Status == CatalogStatus.Loading;
        Installations.Clear();
        foreach (var installation in _catalog.Catalog?.Installations ?? [])
        {
            Installations.Add(new InstallationItemViewModel(installation, _browser));
        }

        HasInstallations = Installations.Count > 0;
        SsoHiddenText = _catalog.Catalog is { SsoHidesInstallations: true }
            ? "GitHub hid installations from organizations that require single sign-on. Authorize single sign-on for those organizations on GitHub, then refresh. Their watched repositories show \"access couldn't be confirmed\"; they aren't removed."
            : null;
        ShowNoInstallations = _catalog.Status == CatalogStatus.Loaded && !HasInstallations;
        StatusText = _catalog.Status switch
        {
            CatalogStatus.Loading => "Checking what GitHub has granted Repo Watch…",
            CatalogStatus.Failed => $"Couldn't check access: {_catalog.Error?.Message}",
            _ => null,
        };
    }
}
