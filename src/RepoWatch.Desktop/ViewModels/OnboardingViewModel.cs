using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RepoWatch.Core.Accounts;
using RepoWatch.Core.Settings;
using RepoWatch.Desktop.Services;

namespace RepoWatch.Desktop.ViewModels;

public enum OnboardingStep
{
    SignIn = 0,
    GrantAccess = 1,
    ChooseRepositories = 2,
    Appearance = 3,
}

/// <summary>
/// First-run flow: Sign in → Grant repository access → Choose repositories → Appearance → Open widget.
/// Users who already installed the app see that and can continue straight away.
/// </summary>
public sealed partial class OnboardingViewModel : ObservableObject, IDisposable
{
    private static readonly string[] Titles = ["Sign in to GitHub", "Grant repository access", "Choose repositories", "Appearance"];

    private readonly AccountService _accounts;
    private readonly AccessCatalogService _catalog;
    private readonly WatchlistService _watchlist;
    private readonly SettingsService _settings;
    private bool _applyingAppearance;

    public OnboardingViewModel(AccountViewModel account, AccessViewModel access, RepositoryPickerViewModel picker,
        AccountService accounts, AccessCatalogService catalog, WatchlistService watchlist, SettingsService settings)
    {
        Account = account;
        Access = access;
        Picker = picker;
        _accounts = accounts;
        _catalog = catalog;
        _watchlist = watchlist;
        _settings = settings;
        _accounts.Changed += OnStateChanged;
        _catalog.Changed += OnStateChanged;
        _watchlist.Changed += OnStateChanged;

        _applyingAppearance = true;
        Theme = settings.App.Appearance.Theme;
        AlwaysOnTop = settings.App.Window.AlwaysOnTop;
        _applyingAppearance = false;

        Step = accounts.State is AccountState.SignedIn or AccountState.Offline ? OnboardingStep.GrantAccess : OnboardingStep.SignIn;
        Update();
    }

    public AccountViewModel Account { get; }

    public AccessViewModel Access { get; }

    public RepositoryPickerViewModel Picker { get; }

    public IReadOnlyList<ThemePreference> Themes { get; } = Enum.GetValues<ThemePreference>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSignInStep), nameof(IsGrantStep), nameof(IsChooseStep), nameof(IsAppearanceStep), nameof(StepTitle), nameof(StepNumber))]
    public partial OnboardingStep Step { get; private set; }

    public bool IsSignInStep => Step == OnboardingStep.SignIn;

    public bool IsGrantStep => Step == OnboardingStep.GrantAccess;

    public bool IsChooseStep => Step == OnboardingStep.ChooseRepositories;

    public bool IsAppearanceStep => Step == OnboardingStep.Appearance;

    public string StepTitle => Titles[(int)Step];

    public string StepNumber => $"Step {(int)Step + 1} of {Titles.Length}";

    [ObservableProperty] public partial bool CanGoNext { get; private set; }

    [ObservableProperty] public partial bool CanGoBack { get; private set; }

    [ObservableProperty] public partial string NextLabel { get; private set; } = "Next";

    /// <summary>Grant step: what GitHub already grants, so existing users know they can continue.</summary>
    [ObservableProperty] public partial string? GrantSummary { get; private set; }

    [ObservableProperty] public partial ThemePreference Theme { get; set; }

    [ObservableProperty] public partial bool AlwaysOnTop { get; set; }

    /// <summary>Raised when the user finishes; the shell closes the window and shows the widget.</summary>
    public event EventHandler? Completed;

    public void Dispose()
    {
        _accounts.Changed -= OnStateChanged;
        _catalog.Changed -= OnStateChanged;
        _watchlist.Changed -= OnStateChanged;
        Account.Dispose();
        Access.Dispose();
        Picker.Dispose();
    }

    [RelayCommand]
    private void Next()
    {
        if (!CanGoNext)
        {
            return;
        }

        if (Step == OnboardingStep.Appearance)
        {
            _settings.UpdateApp(s => s with { OnboardingCompleted = true });
            _settings.Flush();
            Completed?.Invoke(this, EventArgs.Empty);
            return;
        }

        Step++;
        if (Step is OnboardingStep.GrantAccess or OnboardingStep.ChooseRepositories)
        {
            _ = _catalog.EnsureLoadedAsync();
        }

        Update();
    }

    [RelayCommand]
    private void Back()
    {
        if (Step > OnboardingStep.SignIn)
        {
            Step--;
            Update();
        }
    }

    partial void OnThemeChanged(ThemePreference value)
    {
        if (!_applyingAppearance)
        {
            _settings.UpdateApp(s => s with { Appearance = s.Appearance with { Theme = value } });
        }
    }

    partial void OnAlwaysOnTopChanged(bool value)
    {
        if (!_applyingAppearance)
        {
            _settings.UpdateApp(s => s with { Window = s.Window with { AlwaysOnTop = value } });
        }
    }

    private void OnStateChanged(object? sender, EventArgs e) => Update();

    private void Update()
    {
        var signedIn = _accounts.State is AccountState.SignedIn or AccountState.Offline;
        if (!signedIn && Step != OnboardingStep.SignIn)
        {
            Step = OnboardingStep.SignIn; // signed out mid-way
        }

        var installations = _catalog.Catalog?.Installations ?? [];
        GrantSummary = _catalog.Status switch
        {
            CatalogStatus.Loading => "Checking what you've already granted…",
            CatalogStatus.Loaded when installations.Count > 0 =>
                $"Repo Watch already has access through: {string.Join(", ", installations.Select(i => i.Installation.AccountLogin))}. Continue, or grant access to more accounts or repositories.",
            CatalogStatus.Loaded => "Repo Watch hasn't been installed on any of your accounts yet. Choose Grant access, select repositories on GitHub, then come back and check again.",
            CatalogStatus.Failed => $"Couldn't check access: {_catalog.Error?.Message}",
            _ => null,
        };

        CanGoBack = Step > OnboardingStep.SignIn;
        CanGoNext = Step switch
        {
            OnboardingStep.SignIn => signedIn,
            OnboardingStep.GrantAccess => _catalog.Status != CatalogStatus.Loading,
            _ => true,
        };
        NextLabel = Step switch
        {
            OnboardingStep.Appearance => "Open widget",
            OnboardingStep.ChooseRepositories when _watchlist.Repositories.Count == 0 => "Skip for now",
            OnboardingStep.GrantAccess when installations.Count == 0 && _catalog.Status == CatalogStatus.Loaded => "Skip for now",
            _ => "Next",
        };
    }
}
