using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RepoWatch.Desktop.Services;
using RepoWatch.Desktop.ViewModels;
using RepoWatch.Desktop.Views;

[assembly: AvaloniaTestApplication(typeof(RepoWatch.Desktop.Tests.UI.HeadlessApp))]

namespace RepoWatch.Desktop.Tests.UI;

public static class HeadlessApp
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>
/// Renders the real widget with labeled demo data. Screenshots are written to
/// artifacts/screenshots for visual inspection; assertions cover structure and keyboard use.
/// </summary>
public sealed class WidgetWindowTests
{
    private static readonly string ScreenshotDirectory = Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "artifacts", "screenshots", "stage03");

    private static (WidgetWindow Window, WidgetViewModel ViewModel, RecordingBrowser Browser) Open(double width, double height, bool demo = true)
    {
        var monitors = new MonitorHost(TimeProvider.System);
        if (demo)
        {
            monitors.EnterDemo();
        }

        var browser = new RecordingBrowser();
        var viewModel = new WidgetViewModel(monitors, TestServices.Settings(), new FakeShell(), browser, TimeProvider.System, new ImmediateDispatcher());
        var window = new WidgetWindow { DataContext = viewModel, Width = width, Height = height };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, viewModel, browser);
    }

    private static void Capture(Window window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        Directory.CreateDirectory(ScreenshotDirectory);
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(ScreenshotDirectory, name + ".png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }

    private static IEnumerable<TextBlock> VisibleText(Visual root) =>
        root.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text));

    [AvaloniaTheory]
    [InlineData(320, "Light")]
    [InlineData(400, "Light")]
    [InlineData(320, "Dark")]
    public void Compact_list_renders_demo_data_with_a_visible_label(double width, string theme)
    {
        Application.Current!.RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        var (window, viewModel, _) = Open(width, 560);

        var texts = VisibleText(window).Select(t => t.Text).ToList();
        Assert.Contains(texts, t => t!.Contains("not from GitHub", StringComparison.Ordinal));
        Assert.Contains("demo-org/web-app", texts);
        Assert.Equal(5, viewModel.Repositories.Count);
        Assert.Equal("demo-org/web-app", viewModel.Repositories[0].Name); // failing default branch sorts first

        Capture(window, $"compact-{width}-{theme.ToLowerInvariant()}");
        window.Close();
    }

    [AvaloniaFact]
    public void Text_in_a_320_pixel_widget_stays_inside_the_window()
    {
        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        var (window, viewModel, _) = Open(320, 560);
        viewModel.ShowRepositoryCommand.Execute(viewModel.Repositories[0]);
        Dispatcher.UIThread.RunJobs();

        foreach (var text in VisibleText(window))
        {
            var topLeft = text.TranslatePoint(new Point(0, 0), window);
            Assert.NotNull(topLeft);
            Assert.True(topLeft.Value.X >= 0 && topLeft.Value.X + text.Bounds.Width <= window.Bounds.Width + 0.5,
                $"'{text.Text}' overflows horizontally");
        }

        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(0, "actions")]
    [InlineData(1, "pulls")]
    [InlineData(2, "issues")]
    public void Expanded_details_show_each_tab(int tab, string name)
    {
        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        var (window, viewModel, _) = Open(400, 600);
        viewModel.ShowRepositoryCommand.Execute(viewModel.Repositories.First(r => r.Name == "demo-org/web-app"));
        Dispatcher.UIThread.RunJobs();

        var tabs = window.GetLogicalDescendants().OfType<TabControl>().Single();
        tabs.SelectedIndex = tab;
        Capture(window, $"details-{name}");

        Assert.True(viewModel.ShowDetails);
        window.Close();
    }

    [AvaloniaFact]
    public void Stale_and_unavailable_states_are_visible()
    {
        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        var (window, viewModel, _) = Open(400, 600);

        var legacy = viewModel.Repositories.First(r => r.Name == "demo-org/legacy-tool");
        Assert.Equal("Some data is stale", legacy.FreshnessWarning);
        Assert.StartsWith("Stale", legacy.PullRequests.StatusText, StringComparison.Ordinal);

        var retired = viewModel.Repositories.First(r => r.Name == "demo-org/retired-service");
        Assert.Contains("no longer has access", retired.Actions.Message, StringComparison.Ordinal);

        var dotfiles = viewModel.Repositories.First(r => r.Name == "demo-user/dotfiles");
        Assert.Equal("Issues are turned off for this repository.", dotfiles.Issues.Message);
        Assert.Equal("main: No checks", dotfiles.BranchStatus);

        viewModel.ShowRepositoryCommand.Execute(legacy);
        window.GetLogicalDescendants().OfType<TabControl>().Single().SelectedIndex = 1;
        Capture(window, "details-stale-pulls");
        window.Close();
    }

    [AvaloniaFact]
    public void Keyboard_can_navigate_open_details_and_return()
    {
        var (window, viewModel, browser) = Open(400, 600);
        var list = window.FindControl<ListBox>("RepositoryList")!;

        // Tab from the window until focus reaches the repository list, as a keyboard user would.
        for (var i = 0; i < 12 && window.FocusManager?.GetFocusedElement() is not ListBoxItem; i++)
        {
            window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.IsType<ListBoxItem>(window.FocusManager?.GetFocusedElement());
        list.SelectedIndex = 0;
        (list.ContainerFromIndex(0) as ListBoxItem)?.Focus(NavigationMethod.Tab);
        Dispatcher.UIThread.RunJobs();

        // Arrow down to a repository that has workflow runs.
        var target = viewModel.Repositories.ToList().FindIndex(r => r.Name == "demo-org/api-service");
        Assert.True(target > 0);
        for (var i = 0; i < target; i++)
        {
            window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.Equal(target, list.SelectedIndex);

        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(viewModel.ShowDetails);
        Assert.Same(list.SelectedItem, viewModel.SelectedRepository);

        // Tab to the first focusable row in details and activate it with the keyboard.
        window.UpdateLayout();
        var firstRow = window.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("row") && b.IsEffectivelyVisible);
        firstRow.Focus(NavigationMethod.Tab);
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Dispatcher.UIThread.RunJobs();
        Assert.Single(browser.Opened);

        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(viewModel.ShowDetails);
        Assert.True(viewModel.IsExpanded);

        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(viewModel.IsExpanded);
        window.Close();
    }

    [AvaloniaFact]
    public void Signed_out_state_offers_demo_and_settings()
    {
        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        var (window, viewModel, _) = Open(320, 420, demo: false);

        var visibleButtons = window.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).ToList();
        var labels = visibleButtons.Select(b => b.Content as string).ToList();
        Assert.Contains("Explore demo data", labels);
        Assert.Contains("Settings", labels);

        // Nothing to refresh or expand: those controls are not shown.
        var names = visibleButtons.Select(AutomationProperties.GetName).ToList();
        Assert.DoesNotContain("Refresh", names);
        Assert.DoesNotContain("Expand or collapse", names);
        Assert.Contains("Settings", names);
        Capture(window, "signed-out-320");

        Assert.True(viewModel.ShowSignedOutState);
        window.Close();
    }

    [AvaloniaFact]
    public void Settings_window_controls_change_settings()
    {
        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        var settings = TestServices.Settings();
        var monitors = new MonitorHost(TimeProvider.System);
        var paths = new RepoWatch.Desktop.Infrastructure.AppPaths(Path.GetTempPath(), "defaults.json", "user.json", "logs");
        var viewModel = new SettingsViewModel(settings, monitors, new FakeShell { CanHideToTray = true }, new ImmediateDispatcher(), new Core.Configuration.RepoWatchOptions(), paths);
        var window = new SettingsWindow { DataContext = viewModel, Width = 480, Height = 640 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var checkBoxes = window.GetVisualDescendants().OfType<CheckBox>().ToList();
        checkBoxes.Single(c => (c.Content as string)!.Contains("above other windows", StringComparison.Ordinal)).IsChecked = true;
        checkBoxes.Single(c => (c.Content as string)!.Contains("Lock", StringComparison.Ordinal)).IsChecked = true;
        window.GetVisualDescendants().OfType<ComboBox>().Single().SelectedItem = Core.Settings.ThemePreference.Dark;
        Dispatcher.UIThread.RunJobs();

        Assert.True(settings.App.Window.AlwaysOnTop);
        Assert.True(settings.App.Window.PositionLocked);
        Assert.Equal(Core.Settings.ThemePreference.Dark, settings.App.Appearance.Theme);

        window.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Explore demo data").Command!.Execute(null);
        Assert.True(monitors.IsDemo);

        Capture(window, "settings");
        window.Close();
        monitors.Dispose();
    }
}
