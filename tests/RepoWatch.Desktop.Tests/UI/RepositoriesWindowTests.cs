using System.Net;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RepoWatch.Desktop.Tests.Repositories;
using RepoWatch.Desktop.ViewModels;
using RepoWatch.Desktop.Views;
using static RepoWatch.Desktop.Tests.Repositories.CatalogFixtures;

namespace RepoWatch.Desktop.Tests.UI;

public sealed class RepositoriesWindowTests
{
    private static readonly string ScreenshotDirectory = Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "artifacts", "screenshots", "stage05");

    private static void Capture(Window window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Directory.CreateDirectory(ScreenshotDirectory);
        using var frame = window.CaptureRenderedFrame();
        frame!.Save(Path.Combine(ScreenshotDirectory, name + ".png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }

    private static List<string> VisibleText(Visual root) =>
        root.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text)).Select(t => t.Text!).ToList();

    [AvaloniaFact]
    public async Task Repository_manager_tabs_render()
    {
        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        var kit = await SignedInWithCatalogAsync();
        kit.Watchlist.Add(kit.Catalog.Catalog!.Repositories.Where(r => r.Id is 11 or 21).ToList());
        var viewModel = new RepositoriesViewModel(kit.Catalog, kit.Watchlist, new RecordingBrowser(), kit.Time);
        var window = new RepositoriesWindow { DataContext = viewModel, Width = 560, Height = 680 };
        window.Show();

        viewModel.SelectedTab = (int)RepositoriesTab.Add;
        viewModel.Picker.SearchText = "o";
        Capture(window, "manager-add");
        Assert.Contains("2 of 5 repositories selected for the widget", VisibleText(window));

        viewModel.SelectedTab = (int)RepositoriesTab.Watched;
        Dispatcher.UIThread.RunJobs();
        window.GetVisualDescendants().OfType<Expander>().First().IsExpanded = true;
        Capture(window, "manager-watched");
        Assert.Contains("Access granted.", VisibleText(window));

        viewModel.SelectedTab = (int)RepositoriesTab.Access;
        Capture(window, "manager-access");
        window.Close();
        viewModel.Dispose();
    }

    [AvaloniaFact]
    public async Task Access_tab_explains_suspended_and_sso_installations()
    {
        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        var kit = new AccountKit().Start();
        await kit.SignInAsync(AccountKit.TokenJson(), AccountKit.UserJson());
        kit.Http.Json(InstallationsJson(Installation(1, "octo-test"), Installation(2, "paused-org", "Organization", suspendedAt: "2026-09-01T00:00:00Z"), Installation(3, "sso-org", "Organization")))
            .Json(RepositoriesJson(Repository(11, "octo-test", "dotfiles")));
        // The SSO response needs a header, which QueueHandler.Json can't add, so it fails as Forbidden in this render.
        kit.Http.Status(HttpStatusCode.Forbidden);
        await kit.Catalog.RefreshAsync();

        var viewModel = new RepositoriesViewModel(kit.Catalog, kit.Watchlist, new RecordingBrowser(), kit.Time) { SelectedTab = (int)RepositoriesTab.Access };
        var window = new RepositoriesWindow { DataContext = viewModel, Width = 560, Height = 680 };
        window.Show();
        Capture(window, "manager-access-problems");

        var texts = VisibleText(window);
        Assert.Contains(texts, t => t.StartsWith("Suspended", StringComparison.Ordinal));
        Assert.Contains(texts, t => t.StartsWith("Couldn't list repositories", StringComparison.Ordinal));
        window.Close();
        viewModel.Dispose();
    }

    [AvaloniaFact]
    public async Task Onboarding_steps_render()
    {
        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        var kit = new AccountKit().Start();
        var onboarding = WatchlistAcceptanceTests.Onboarding(kit);
        var window = new OnboardingWindow { DataContext = onboarding, Width = 560, Height = 640 };
        window.Show();
        Capture(window, "onboarding-1-sign-in");

        await kit.SignInAsync(AccountKit.TokenJson(), AccountKit.UserJson());
        QueueTypicalCatalog(kit.Http);
        onboarding.NextCommand.Execute(null);
        await kit.Catalog.EnsureLoadedAsync();
        Capture(window, "onboarding-2-access");

        onboarding.NextCommand.Execute(null);
        onboarding.Picker.Shown[0].IsSelected = true;
        Capture(window, "onboarding-3-choose");

        onboarding.NextCommand.Execute(null);
        Capture(window, "onboarding-4-appearance");
        Assert.Contains("Open widget", window.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).Select(b => b.Content as string));
        window.Close();
        onboarding.Dispose();
    }
}
