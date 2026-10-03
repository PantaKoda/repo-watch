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

    /// <summary>The colors the widget actually paints behind text: both stops of the theme's surface gradient.</summary>
    private static IReadOnlyList<Color> SurfaceStops(ThemeVariant variant)
    {
        Assert.True(Application.Current!.TryGetResource("SpaceSurfaceBrush", variant, out var brush));
        var stops = ((LinearGradientBrush)brush!).GradientStops.Select(s => s.Color).ToList();
        Assert.NotEmpty(stops);
        return stops;
    }

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

    [AvaloniaFact]
    public void Every_accent_has_readable_contrast_on_both_themes()
    {
        foreach (var preset in AccentPalette.Presets)
        {
            var (dark, light) = AccentPalette.Colors(preset.Hex);

            // The accent colors the brand text and focus: at least 4.5:1, the WCAG level for normal text,
            // everywhere on the gradient the widget paints.
            foreach (var stop in SurfaceStops(ThemeVariant.Dark))
            {
                Assert.True(Contrast(dark, stop) >= 4.5, $"{preset.Name} on dark {stop}: {Contrast(dark, stop):0.00}");
            }

            foreach (var stop in SurfaceStops(ThemeVariant.Light))
            {
                Assert.True(Contrast(light, stop) >= 4.5, $"{preset.Name} on light {stop}: {Contrast(light, stop):0.00}");
            }
        }
    }

    [AvaloniaFact]
    public void Secondary_text_is_readable_across_the_painted_surface_gradient()
    {
        var application = Application.Current!;
        foreach (var variant in new[] { ThemeVariant.Dark, ThemeVariant.Light })
        {
            Assert.True(application.TryGetResource("HudDimBrush", variant, out var dim));
            foreach (var stop in SurfaceStops(variant))
            {
                var ratio = Contrast(((ISolidColorBrush)dim!).Color, stop);
                Assert.True(ratio >= 4.5, $"secondary text on {variant} {stop}: {ratio:0.00}");
            }
        }
    }

    [Fact]
    public void Unknown_or_missing_accents_fall_back_to_the_default()
    {
        Assert.Equal(AccentPalette.Presets[0], AccentPalette.Find(null));
        Assert.Equal(AccentPalette.Presets[0], AccentPalette.Find("#123456"));
        Assert.Equal(AccentPalette.Colors(null), AccentPalette.Colors("not a color"));
        // Only presets are honored: a hand-edited value would have no contrast guarantee and no entry in Settings.
        Assert.Equal(AccentPalette.Colors(null), AccentPalette.Colors("#3366FF"));
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
    public void The_default_accent_reproduces_App_axaml()
    {
        var application = Application.Current!;
        AccentPalette.Apply(application, null);

        var xaml = System.Xml.Linq.XDocument.Load(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "RepoWatch.Desktop", "App.axaml"));
        System.Xml.Linq.XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        foreach (var variant in new[] { ThemeVariant.Dark, ThemeVariant.Light })
        {
            var dictionary = xaml.Descendants().Single(e => e.Name.LocalName == "ResourceDictionary" && (string?)e.Attribute(x + "Key") == variant.Key.ToString());
            foreach (var key in new[] { "HudAccentBrush", "HudFrameBrush", "RowHoverBrush", "PanelBrush", "WidgetBorderBrush" })
            {
                var declared = Color.Parse((string)dictionary.Elements().Single(e => (string?)e.Attribute(x + "Key") == key).Attribute("Color")!);
                Assert.True(application.TryGetResource(key, variant, out var applied));
                Assert.Equal(declared, ((ISolidColorBrush)applied!).Color);
            }

            var glow = BoxShadows.Parse(dictionary.Elements().Single(e => (string?)e.Attribute(x + "Key") == "HudGlowShadow").Value);
            Assert.True(application.TryGetResource("HudGlowShadow", variant, out var appliedGlow));
            Assert.Equal(glow, (BoxShadows)appliedGlow!);
        }
    }

    [AvaloniaFact]
    public void Changing_the_accent_updates_open_windows()
    {
        var application = Application.Current!;
        application.RequestedThemeVariant = ThemeVariant.Dark;
        using var monitors = new MonitorHost(TimeProvider.System);
        monitors.EnterDemo();
        var widget = new WidgetWindow
        {
            DataContext = new WidgetViewModel(monitors, TestServices.Settings(), new FakeShell(), new RecordingBrowser(), TimeProvider.System,
                new ImmediateDispatcher(), new Core.Configuration.RepoWatchOptions()),
            Width = 400, Height = 520,
        };
        widget.Show();
        Dispatcher.UIThread.RunJobs();
        var brand = widget.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("brand"));
        var before = ((ISolidColorBrush)brand.Foreground!).Color;
        application.TryGetResource("SystemAccentColor", ThemeVariant.Dark, out var fluentBefore);

        try
        {
            AccentPalette.Apply(application, "#E15BFF");
            Dispatcher.UIThread.RunJobs();

            Assert.NotEqual(before, ((ISolidColorBrush)brand.Foreground!).Color);
            Assert.Equal(Color.Parse("#E15BFF"), ((ISolidColorBrush)brand.Foreground!).Color);
            Assert.True(application.TryGetResource("SystemAccentColor", ThemeVariant.Dark, out var fluentAfter));
            Assert.NotEqual(fluentBefore, fluentAfter); // Fluent controls (sliders, check boxes) follow too
        }
        finally
        {
            AccentPalette.Apply(application, null);
            widget.Close();
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
            // Names end inside the window (trimmed with an ellipsis if needed), measured in window coordinates.
            var titles = widget.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && t.Classes.Contains("title")).ToList();
            Assert.NotEmpty(titles);
            Assert.All(titles, t =>
            {
                var end = t.TranslatePoint(new Point(t.Bounds.Width, 0), widget);
                Assert.True(end is { } p && p.X <= widget.ClientSize.Width, $"{t.Text} ends at {end?.X} of {widget.ClientSize.Width}");
            });

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
