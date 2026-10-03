using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using RepoWatch.Desktop.Presentation;

namespace RepoWatch.Desktop.Controls;

/// <summary>
/// A small status indicator colored from the theme's Status*Brush resources. Unknown is drawn
/// hollow so it is distinguishable without color; None draws nothing. Always pair with a text label.
/// </summary>
public sealed class StatusDot : Control
{
    public static readonly StyledProperty<StatusTone> ToneProperty =
        AvaloniaProperty.Register<StatusDot, StatusTone>(nameof(Tone));

    static StatusDot()
    {
        AffectsRender<StatusDot>(ToneProperty);
    }

    public StatusDot()
    {
        Width = 10;
        Height = 10;
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    public StatusTone Tone
    {
        get => GetValue(ToneProperty);
        set => SetValue(ToneProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (Tone == StatusTone.None)
        {
            return;
        }

        var brush = this.TryFindResource($"Status{Tone}Brush", ActualThemeVariant, out var resource) && resource is IBrush found
            ? found
            : Brushes.Gray;

        var radius = Math.Min(Bounds.Width, Bounds.Height) / 2;
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        if (Tone == StatusTone.Unknown)
        {
            context.DrawEllipse(null, new Pen(brush, 1.5), center, radius - 0.75, radius - 0.75);
        }
        else
        {
            context.DrawEllipse(brush, null, center, radius, radius);
        }
    }
}
