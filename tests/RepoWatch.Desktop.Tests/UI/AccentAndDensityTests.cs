using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RepoWatch.Core.Settings;
using RepoWatch.Desktop.Presentation;
using RepoWatch.Desktop.Services;
using RepoWatch.Desktop.ViewModels;
using RepoWatch.Desktop.Views;

namespace RepoWatch.Desktop.Tests.UI;

/// <summary>Accent presets keep text readable; density is a real layout change; status colors never follow the accent.</summary>
public sealed class AccentAndDensityTests
{
    private static readonly string ScreenshotDirectory = Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "artifacts", "screenshots", "stage08");

    private static readonly Color DarkSurface = Color.Parse("#0B1322");
    private static readonly Color LightSurface = Color.Parse("#F5F9FD");

    /// <summary>WCAG 2 contrast ratio.</summary>
    private static double Contrast(Color a, Color b)
    {
        static double Luminance(Color c)
        {
            static double Channel(byte value)
            {
                var v = value / 255.0;
                return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
            }

            return (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));
        }

        var (l1, l2) = (Luminance(a), Luminance(b));
        return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
    }

    [Fact]
    public void Every_accent_has_readable_contrast_on_both_themes()
    {
        foreach (var preset in AccentPalette.Presets)
        {
            var (dark, light) = AccentPalette.Colors(preset.Hex);

            // The accent colors the brand text and focus: at least 4.5:1, the WCAG level for normal text.
            Assert.True(Contrast(dark, DarkSurface) >= 4.5, $"{preset.Name} on dark: {Contrast(dark, DarkSurface):0.0}");
            Assert.True(Contrast(light, LightSurface) >= 4.5, $"{preset.Name} on light: {Contrast(light, LightSurface):0.0}");
        }
    }

    [AvaloniaFact]
    public void Secondary_text_is_readable_on_the_solid_surfaces()
    {
        var application = Application.Current!;
        foreach (var (variant, surface) in new[] { (ThemeVariant.Dark, DarkSurface), (ThemeVariant.Light, LightSurface) })
        {
            Assert.True(application.TryGetResource("HudDimBrush", variant, out var dim));
            var ratio = Contrast(((ISolidColorBrush)dim!).Color, surface);
            Assert.True(ratio >= 4.5, $"secondary text on {variant}: {ratio:0.0}");
        }
    }

    [Fact]
    public void Unknown_or_missing_accents_fall_back_to_the_default()
    {
        Assert.Equal(AccentPalette.Presets[0], AccentPalette.Find(null));
        Assert.Equal(AccentPalette.Presets[0], AccentPalette.Find("#123456"));
        Assert.Equal(AccentPalette.Colors(null), AccentPalette.Colors("not a color"));
        Assert.Equal("Nebula violet", AccentPalette.Find("#9d7cff").Name);
    }

    [AvaloniaFact]
    public void Applying_an_accent_changes_the_HUD_but_never_the_status_colors()
    {
        var application = Application.Current!;
        application.TryGetResource("StatusRunningBrush", ThemeVariant.Dark, out var runningBefore);
        try
        {
            AccentPalette.Apply(application, "#9D7CFF");

            Assert.True(application.TryGetResource("HudAccentBrush", ThemeVariant.Dark, out var accent));
            Assert.Equal(Color.Parse("#9D7CFF"), ((ISolidColorBrush)accent!).Color);
            Assert.True(application.TryGetResource("HudAccentBrush", ThemeVariant.Light, out var lightAccent));
            Assert.Equal(AccentPalette.Colors("#9D7CFF").Light, ((ISolidColorBrush)lightAccent!).Color);
            Assert.True(application.TryGetResource("StatusRunningBrush", ThemeVariant.Dark, out var runningAfter));
            Assert.Equal(((ISolidColorBrush)runningBefore!).Color, ((ISolidColorBrush)runningAfter!).Color);
        }
        finally
        {
            AccentPalette.Apply(application, null);
        }
    }

    [AvaloniaFact]
    public void Settings_change_accent_and_density_and_the_compact_widget_stays_readable_at_320()
    {
        var application = Application.Current!;
        application.RequestedThemeVariant = ThemeVariant.Dark;
        var settings = TestServices.Settings();
        using var monitors = new MonitorHost(TimeProvider.System);
        var viewModel = SettingsViewModels.Create(settings, monitors, new FakeShell { CanHideToTray = true });
        var window = new SettingsWindow { DataContext = viewModel, Width = 480, Height = 820 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var combos = window.GetVisualDescendants().OfType<ComboBox>().ToDictionary(c => AutomationProperties.GetName(c)!);
        combos["Accent"].SelectedItem = AccentPalette.Presets[1];
        combos["Density"].SelectedItem = Density.Compact;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("#9D7CFF", settings.App.Appearance.AccentColor);
        Assert.Equal(Density.Compact, settings.App.Appearance.Density);
        window.Close();

        try
        {
            AccentPalette.Apply(application, settings.App.Appearance.AccentColor);
            monitors.EnterDemo();
            var widgetModel = new WidgetViewModel(monitors, TestServices.Settings(), new FakeShell(), new RecordingBrowser(), TimeProvider.System,
                new ImmediateDispatcher(), new Core.Configuration.RepoWatchOptions());
            var widget = new WidgetWindow { DataContext = widgetModel, Width = 320, Height = 520 };
            widget.Show();
            Dispatcher.UIThread.RunJobs();
            var comfortableRow = widget.GetVisualDescendants().OfType<ListBoxItem>().First().Bounds.Height;

            widget.Classes.Add("compact");
            Dispatcher.UIThread.RunJobs();
            widget.UpdateLayout();
            var compactRow = widget.GetVisualDescendants().OfType<ListBoxItem>().First().Bounds.Height;

            Assert.True(compactRow < comfortableRow, $"compact {compactRow} vs comfortable {comfortableRow}");
            // Names are still shown in full or trimmed with an ellipsis, never clipped mid-glyph off screen.
            Assert.All(widget.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && t.Classes.Contains("title")),
                t => Assert.True(t.Bounds.Right <= widget.Bounds.Width));

            Directory.CreateDirectory(ScreenshotDirectory);
            using var frame = widget.CaptureRenderedFrame();
            frame!.Save(Path.Combine(ScreenshotDirectory, "compact-violet-320.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            widget.Close();
        }
        finally
        {
            AccentPalette.Apply(application, null);
        }
    }
}
