using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RepoWatch.Core.Settings;
using RepoWatch.Desktop.Controls;
using RepoWatch.Desktop.Platform.Windowing;
using RepoWatch.Desktop.Services;
using RepoWatch.Desktop.ViewModels;
using RepoWatch.Desktop.Views;

namespace RepoWatch.Desktop.Tests.UI;

/// <summary>Transparency fallbacks, activity motion and the appearance controls.</summary>
public sealed class SpaceStationVisualsTests
{
    private static readonly string ScreenshotDirectory = Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "artifacts", "screenshots", "ui-space-station");

    [Theory]
    [InlineData(WindowMaterial.Solid, 0.4, 1.0)]
    [InlineData(WindowMaterial.Transparent, 0.4, 0.4)]
    [InlineData(WindowMaterial.Frosted, 0.1, 0.2)] // clamped to the readable floor
    [InlineData(WindowMaterial.Auto, 0.3, 0.7)] // Auto never goes below a readable frosted surface
    public void Surface_opacity_follows_the_material(WindowMaterial material, double requested, double expected)
    {
        var applied = WindowMaterialService.Resolve(Appearance(material, requested), WindowTransparencyLevel.AcrylicBlur, false, false);

        Assert.Equal(expected, applied.SurfaceOpacity, 3);
    }

    [Fact]
    public void The_light_theme_keeps_a_readable_surface()
    {
        var light = WindowMaterialService.Resolve(Appearance(WindowMaterial.Transparent, 0.4), WindowTransparencyLevel.Transparent, false, false, lightTheme: true);
        var dark = WindowMaterialService.Resolve(Appearance(WindowMaterial.Transparent, 0.4), WindowTransparencyLevel.Transparent, false, false, lightTheme: false);
        var lightHigh = WindowMaterialService.Resolve(Appearance(WindowMaterial.Frosted, 0.9), WindowTransparencyLevel.AcrylicBlur, false, false, lightTheme: true);

        Assert.Equal(WindowMaterialService.LightThemeMinimumOpacity, light.SurfaceOpacity);
        Assert.Contains("light theme", light.Fallback, StringComparison.Ordinal);
        Assert.Equal(0.4, dark.SurfaceOpacity, 3);
        Assert.Equal(0.9, lightHigh.SurfaceOpacity, 3);
        Assert.Null(lightHigh.Fallback);
    }

    [Fact]
    public void Unavailable_transparency_falls_back_to_a_solid_surface_and_says_so()
    {
        var applied = WindowMaterialService.Resolve(Appearance(WindowMaterial.Transparent, 0.4), WindowTransparencyLevel.None, false, false);

        Assert.Equal(1, applied.SurfaceOpacity);
        Assert.False(applied.IsSeeThrough);
        Assert.NotNull(applied.Fallback);
    }

    [Fact]
    public void High_contrast_and_remote_sessions_force_solid()
    {
        var highContrast = WindowMaterialService.Resolve(Appearance(WindowMaterial.Transparent, 0.4), WindowTransparencyLevel.Transparent, true, false);
        var remote = WindowMaterialService.Resolve(Appearance(WindowMaterial.Frosted, 0.4), WindowTransparencyLevel.AcrylicBlur, false, true);

        Assert.Equal(1, highContrast.SurfaceOpacity);
        Assert.Contains("High contrast", highContrast.Fallback, StringComparison.Ordinal);
        Assert.Equal(1, remote.SurfaceOpacity);
        Assert.Contains("remote", remote.Fallback, StringComparison.Ordinal);
    }

    [Fact]
    public void A_frosted_request_that_only_gets_plain_transparency_is_reported()
    {
        var applied = WindowMaterialService.Resolve(Appearance(WindowMaterial.Frosted, 0.5), WindowTransparencyLevel.Transparent, false, false);

        Assert.Equal(0.5, applied.SurfaceOpacity, 3);
        Assert.Contains("plain transparency", applied.Fallback, StringComparison.Ordinal);
    }

    [AvaloniaTheory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void Running_work_is_marked_active_and_text_stays_opaque_when_the_surface_fades(string theme)
    {
        Application.Current!.RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        var (window, viewModel) = Open();
        viewModel.SurfaceOpacity = 0.55;
        Dispatcher.UIThread.RunJobs();

        var running = viewModel.Repositories.Where(r => r.IsActive).Select(r => r.Name).ToList();
        Assert.NotEmpty(running);
        var dots = window.GetVisualDescendants().OfType<StatusDot>().Where(d => d.IsEffectivelyVisible).ToList();
        Assert.Contains(dots, d => d.Classes.Contains(":active"));
        Assert.Contains(dots, d => !d.Classes.Contains(":active")); // only running work radiates

        // Opacity applies to the background layer only: no text or its ancestors are faded.
        foreach (var text in window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible))
        {
            Assert.All(text.GetSelfAndVisualAncestors().OfType<Visual>(), v => Assert.Equal(1, v.Opacity));
        }

        Capture(window, $"compact-{theme.ToLowerInvariant()}");

        viewModel.ShowRepositoryCommand.Execute(viewModel.Repositories.First(r => r.IsActive));
        Dispatcher.UIThread.RunJobs();
        Capture(window, $"details-running-{theme.ToLowerInvariant()}");
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Running_work_pulses_unless_motion_is_reduced(bool reduceMotion)
    {
        Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        var (window, _) = Open();
        window.Classes.Set("reduce-motion", reduceMotion);
        Dispatcher.UIThread.RunJobs();

        var dots = window.GetVisualDescendants().OfType<StatusDot>().ToList();
        var active = dots.Where(d => d.Classes.Contains(":active")).ToList();
        Assert.NotEmpty(active);
        Assert.All(active, d => Assert.True(d.IsEffectivelyVisible)); // nothing hidden keeps an animation running
        var observed = new List<double>();
        for (var i = 0; i < 40; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            observed.Add(active[0].Pulse);
        }

        if (reduceMotion)
        {
            Assert.All(observed, p => Assert.Equal(0, p));
        }
        else
        {
            Assert.Contains(observed, p => p > 0);
        }

        window.Close();
    }

    [AvaloniaFact]
    public void Appearance_controls_change_material_opacity_and_motion()
    {
        Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        var settings = TestServices.Settings();
        using var monitors = new MonitorHost(TimeProvider.System);
        var viewModel = SettingsViewModels.Create(settings, monitors, new FakeShell { CanHideToTray = true });
        var window = new SettingsWindow { DataContext = viewModel, Width = 480, Height = 760 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var combos = window.GetVisualDescendants().OfType<ComboBox>().ToDictionary(c => AutomationProperties.GetName(c)!);
        combos["Window material"].SelectedItem = WindowMaterial.Frosted;
        combos["Motion"].SelectedItem = MotionPreference.Off;
        var slider = window.GetVisualDescendants().OfType<Slider>().Single(s => AutomationProperties.GetName(s) == "Background opacity");
        Assert.Equal(20, slider.Minimum);
        Assert.Equal(100, slider.Maximum);
        slider.Value = 55;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(WindowMaterial.Frosted, settings.App.Appearance.Material);
        Assert.Equal(MotionPreference.Off, settings.App.Appearance.Motion);
        Assert.Equal(0.55, settings.App.Appearance.BackgroundOpacity, 3);
        Assert.Equal("55%", viewModel.OpacityLabel);

        // A solid surface has nothing to fade, so the slider is disabled rather than silently ignored.
        combos["Window material"].SelectedItem = WindowMaterial.Solid;
        Dispatcher.UIThread.RunJobs();
        Assert.False(slider.IsEffectivelyEnabled);
        combos["Window material"].SelectedItem = WindowMaterial.Frosted;

        Capture(window, "settings-appearance");
        window.Close();
    }

    [AvaloniaFact]
    public void Opacity_changes_do_not_reapply_the_window_backdrop()
    {
        var (window, _) = Open();
        WindowMaterialService.Apply(window, Appearance(WindowMaterial.Frosted, 0.5));
        var hint = window.TransparencyLevelHint;

        WindowMaterialService.Apply(window, Appearance(WindowMaterial.Frosted, 0.6));
        Assert.Same(hint, window.TransparencyLevelHint);

        WindowMaterialService.Apply(window, Appearance(WindowMaterial.Solid, 0.6));
        Assert.NotSame(hint, window.TransparencyLevelHint);
        window.Close();
    }

    [Fact]
    public void Without_transparency_the_whole_window_is_painted_solid()
    {
        var surface = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Colors.Navy);
        var none = WindowMaterialService.Resolve(Appearance(WindowMaterial.Transparent, 0.4), WindowTransparencyLevel.None, false, false);
        var granted = WindowMaterialService.Resolve(Appearance(WindowMaterial.Transparent, 0.4), WindowTransparencyLevel.Transparent, false, false);

        // Otherwise the margin and rounded corners render black around a "solid" widget.
        Assert.Same(surface, WindowMaterialService.WindowBackground(none, surface));
        Assert.Same(Avalonia.Media.Brushes.Transparent, WindowMaterialService.WindowBackground(granted, surface));
    }
    [AvaloniaFact]
    public void The_low_opacity_halo_applies_to_text_not_the_animated_content()
    {
        Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        var (window, viewModel) = Open();
        viewModel.SurfaceOpacity = 0.4;
        Dispatcher.UIThread.RunJobs();

        var content = window.GetVisualDescendants().OfType<DockPanel>().Single(d => d.Classes.Contains("legible"));
        Assert.Null(content.Effect);
        // Button labels (AccessText) sit on opaque button backgrounds and need no halo.
        Assert.All(content.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && t is not Avalonia.Controls.Primitives.AccessText), t => Assert.NotNull(t.Effect));
        Assert.All(content.GetVisualDescendants().OfType<StatusDot>(), d => Assert.Null(d.Effect));

        viewModel.SurfaceOpacity = 0.9;
        Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<DockPanel>(), d => d.Classes.Contains("legible"));
        window.Close();
    }

    private static AppearanceSettings Appearance(WindowMaterial material, double opacity) =>
        new() { Material = material, BackgroundOpacity = opacity };

    private static (WidgetWindow Window, WidgetViewModel ViewModel) Open()
    {
        var monitors = new MonitorHost(TimeProvider.System);
        monitors.EnterDemo();
        var viewModel = new WidgetViewModel(monitors, TestServices.Settings(), new FakeShell(), new RecordingBrowser(), TimeProvider.System,
            new ImmediateDispatcher(), new Core.Configuration.RepoWatchOptions());
        var window = new WidgetWindow { DataContext = viewModel, Width = 400, Height = 600 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, viewModel);
    }

    private static void Capture(Window window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        Directory.CreateDirectory(ScreenshotDirectory);
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(ScreenshotDirectory, name + ".png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }
}
