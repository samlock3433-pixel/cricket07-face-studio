using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Cricket07.FaceStudio.Views;

/// <summary>Small helpers shared by the windows.</summary>
internal static class Ui
{
    /// <summary>Keeps a large dialog inside the screen's work area (high DPI scaling, small laptop screens).</summary>
    public static void FitToScreen(Window window)
    {
        var area = SystemParameters.WorkArea;
        window.MinWidth = Math.Min(window.MinWidth, area.Width * 0.9); window.MinHeight = Math.Min(window.MinHeight, area.Height * 0.9);
        window.Width = Math.Min(window.Width, area.Width * 0.96); window.Height = Math.Min(window.Height, area.Height * 0.96);
    }

    public static (byte[] Pixels, int Width, int Height) Pixels(BitmapSource image)
    {
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[checked(converted.PixelWidth * converted.PixelHeight * 4)];
        converted.CopyPixels(pixels, converted.PixelWidth * 4, 0);
        return (pixels, converted.PixelWidth, converted.PixelHeight);
    }

    /// <summary>96 DPI so one image pixel = one canvas unit, whatever DPI the source file declared.</summary>
    public static BitmapSource Bitmap(byte[] pixels, int width, int height)
    { var b = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4); b.Freeze(); return b; }

    /// <summary>Paint colours offered as swatches: skin tones from pale to deep, then hair and stubble.</summary>
    public static readonly Color[] PaintColours =
    {
        Color.FromRgb(236, 200, 172), Color.FromRgb(214, 168, 134), Color.FromRgb(188, 138, 104), Color.FromRgb(156, 106, 76),
        Color.FromRgb(120, 80, 56), Color.FromRgb(86, 56, 40), Color.FromRgb(16, 13, 12), Color.FromRgb(46, 31, 22),
        Color.FromRgb(84, 58, 38), Color.FromRgb(62, 56, 52), Color.FromRgb(130, 124, 120), Color.FromRgb(200, 196, 190),
    };
    /// <summary>Fills a panel with clickable colour swatches.</summary>
    public static void FillSwatches(Panel panel, Action<Color> chosen)
    {
        foreach (var colour in PaintColours)
        {
            var swatch = new Border { Width = 20, Height = 20, Margin = new Thickness(0, 0, 3, 3), Background = new SolidColorBrush(colour), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Cursor = Cursors.Hand };
            swatch.MouseLeftButtonDown += (_, _) => chosen(colour);
            panel.Children.Add(swatch);
        }
    }
}
