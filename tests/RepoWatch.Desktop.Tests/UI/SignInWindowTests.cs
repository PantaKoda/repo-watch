using System.Net;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging.Abstractions;
using RepoWatch.Core.Accounts;
using RepoWatch.Core.Monitoring;
using RepoWatch.Desktop.Infrastructure;
using RepoWatch.Desktop.Platform;
using RepoWatch.Desktop.Services;
using RepoWatch.Desktop.ViewModels;
using RepoWatch.Desktop.Views;
using RepoWatch.GitHub;

namespace RepoWatch.Desktop.Tests.UI;

public sealed class SignInWindowTests
{
    private static readonly string ScreenshotDirectory = Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "artifacts", "screenshots", "stage04");

    private static (SettingsWindow Window, AccountViewModel Account, FakeShell Shell, RecordingBrowser Browser) OpenSettings(AccountKit kit)
    {
        var shell = new FakeShell();
        var browser = new RecordingBrowser();
        var endpoints = new GitHubEndpoints(kit.Options.GitHub);
        var account = new AccountViewModel(kit.Accounts, shell, browser,
            new AvatarLoader(GitHubHttp.CreateClient(new QueueHandler()), NullLogger<AvatarLoader>.Instance), endpoints, new ImmediateDispatcher(), kit.Time);
        var settings = new SettingsViewModel(kit.Settings, kit.Monitors, shell, new ImmediateDispatcher(), account, kit.Watchlist,
            new AppPaths(Path.GetTempPath(), "d.json", "u.json", "logs"));
        var window = new SettingsWindow { DataContext = settings, Width = 480, Height = 720 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, account, shell, browser);
    }

    private static void Capture(Window window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        Directory.CreateDirectory(ScreenshotDirectory);
        using var frame = window.CaptureRenderedFrame();
        frame!.Save(Path.Combine(ScreenshotDirectory, name + ".png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }

    private static List<string> VisibleText(Visual root) =>
        root.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text)).Select(t => t.Text!).ToList();

    [AvaloniaFact]
    public async Task Sign_in_shows_the_user_code_with_copy_and_open_actions_but_never_the_device_code()
    {
        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        var kit = new AccountKit().Start();
        kit.Http.Json(AccountKit.DeviceCodeJson).Json("""{"error":"authorization_pending"}""");
        var (window, account, shell, browser) = OpenSettings(kit);

        var signIn = account.SignInCommand.ExecuteAsync(null);
        for (var i = 0; i < 50 && account.UserCode is null; i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.True(account.IsSigningIn);
        Assert.Equal("WDJB-MJHT", account.UserCode);
        Assert.Equal(new Uri("https://github.com/login/device"), Assert.Single(browser.Opened)); // browser launched for the user

        var texts = VisibleText(window);
        Assert.Contains("WDJB-MJHT", texts);
        Assert.Contains(texts, t => t.Contains("expires in 15 minutes", StringComparison.Ordinal));
        Assert.DoesNotContain(texts, t => t.Contains("3584d83530557fdd1f46af8289938c8ef79f9dc5", StringComparison.Ordinal));
        Assert.DoesNotContain(texts, t => t.Contains("password", StringComparison.OrdinalIgnoreCase) && !t.Contains("never asks", StringComparison.Ordinal));
        Capture(window, "settings-signing-in");

        await account.CopyCodeCommand.ExecuteAsync(null);
        Assert.Equal(["WDJB-MJHT"], shell.Copied);

        account.CancelSignInCommand.Execute(null);
        await signIn.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Dispatcher.UIThread.RunJobs();
        Assert.False(account.IsSigningIn);
        Assert.Equal("Sign-in cancelled.", account.Message);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Signed_in_account_shows_identity_storage_and_sign_out()
    {
        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        var kit = new AccountKit().Start();
        await kit.SignInAsync(AccountKit.TokenJson(), AccountKit.UserJson());
        var (window, account, _, browser) = OpenSettings(kit);

        var texts = VisibleText(window);
        Assert.Contains("octo-test", texts);
        Assert.Contains(texts, t => t.Contains("test credential store", StringComparison.Ordinal));
        Capture(window, "settings-signed-in");

        await account.ManageAuthorizationCommand.ExecuteAsync(null);
        Assert.Equal("https://github.com/settings/apps/authorizations", browser.Opened.Last().ToString());

        await account.SignOutCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(AccountState.SignedOut, kit.Accounts.State);
        Assert.True(account.ShowSignIn);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Widget_shows_reconnect_instead_of_retrying_when_access_is_revoked()
    {
        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        var kit = new AccountKit().Start();
        await kit.SignInAsync(AccountKit.TokenJson(), AccountKit.UserJson());
        kit.Start();
        kit.Http.Status(HttpStatusCode.Unauthorized).Json("""{"error":"bad_refresh_token"}""");
        await kit.Accounts.RestoreAsync(TestContext.Current.CancellationToken);

        var shell = new FakeShell();
        var widget = new WidgetViewModel(kit.Monitors, kit.Settings, shell, new RecordingBrowser(), kit.Time, new ImmediateDispatcher(), kit.Options);
        var window = new WidgetWindow { DataContext = widget, Width = 320, Height = 420 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(ConnectionState.ReconnectRequired, kit.Monitors.Current.State);
        Assert.True(widget.ShowReconnectState);
        Assert.Contains("Reconnect required", VisibleText(window));
        Capture(window, "widget-reconnect-320");

        widget.SignInCommand.Execute(null);
        Assert.Equal(1, shell.SignInRequests);
        window.Close();
    }
}
