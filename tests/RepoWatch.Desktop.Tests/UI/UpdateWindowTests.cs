using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RepoWatch.Desktop.Tests.Updates;
using RepoWatch.Desktop.ViewModels;
using RepoWatch.Desktop.Views;

namespace RepoWatch.Desktop.Tests.UI;

public sealed class UpdateWindowTests
{
    [AvaloniaFact]
    public async Task The_update_window_shows_the_changes_and_an_enabled_install_button()
    {
        using var kit = new UpdateKit("0.2.0");
        kit.Source.Publish("0.3.0", notes: "## Added\n- Updates inside the app");
        await kit.Service.CheckNowAsync();
        using var viewModel = new UpdateViewModel(kit.Service, new RecordingBrowser(), new ImmediateDispatcher());
        var window = new UpdateWindow { DataContext = viewModel, Width = 520, Height = 560 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var texts = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
        Assert.Contains("Repo Watch 0.3.0 is available", texts);
        Assert.Contains(texts, t => t?.Contains("• Updates inside the app", StringComparison.Ordinal) == true);
        var install = window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Install update"));
        Assert.True(install.IsEffectivelyEnabled);
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_copy_that_cannot_update_itself_says_why_instead_of_offering_install()
    {
        using var kit = new UpdateKit("0.2.0", portable: false);
        kit.Source.Publish("0.3.0");
        await kit.Service.CheckNowAsync();
        using var viewModel = new UpdateViewModel(kit.Service, new RecordingBrowser(), new ImmediateDispatcher());
        var window = new UpdateWindow { DataContext = viewModel, Width = 520, Height = 560 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var install = window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Install update"));
        Assert.False(install.IsEffectivelyEnabled);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text?.Contains("built from source", StringComparison.Ordinal) == true);
        window.Close();
    }
}
