using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace RepoWatch.Desktop.Platform;

/// <summary>Draws the app/tray icon at runtime. A designed .ico asset replaces this for packaging (Stage 11).</summary>
public static class AppIconFactory
{
    public static WindowIcon Create()
    {
        var accent = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB));
        using var bitmap = new RenderTargetBitmap(new PixelSize(64, 64), new Vector(96, 96));
        using (var context = bitmap.CreateDrawingContext())
        {
            context.DrawRectangle(accent, null, new RoundedRect(new Rect(4, 4, 56, 56), 14));
            context.DrawEllipse(Brushes.White, null, new Point(32, 32), 15, 15);
            context.DrawEllipse(accent, null, new Point(32, 32), 7, 7);
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        stream.Position = 0;
        return new WindowIcon(stream);
    }
}
