using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Apathy.Themes;

/// <summary>Replaces the runtime resources declared at the bottom of Palette.xaml.</summary>
public static class ThemeManager
{
    // 4x4 tile for the page background: dark base with a faint diagonal dot pattern.
    private static readonly byte[,] PatternTile =
    {
        { 0x19, 0x13, 0x13, 0x13 },
        { 0x13, 0x13, 0x16, 0x13 },
        { 0x13, 0x13, 0x13, 0x13 },
        { 0x13, 0x16, 0x13, 0x13 },
    };

    public static void InstallPattern()
    {
        const int size = 4;
        var pixels = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            int i = (y * size + x) * 4;
            pixels[i] = pixels[i + 1] = pixels[i + 2] = PatternTile[y, x];
            pixels[i + 3] = 0xFF;
        }

        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4);
        var brush = new ImageBrush(bitmap)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, size, size),
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None,
        };
        RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.NearestNeighbor);
        Set("Sk.Pattern", brush);
    }

    public static void ApplyAccent(Color accent)
    {
        accent.A = 255;
        var dark = Color.FromRgb((byte)(accent.R * 0.6), (byte)(accent.G * 0.6), (byte)(accent.B * 0.6));
        Set("Sk.Accent", new SolidColorBrush(accent));
        Set("Sk.Accent.Gradient", new LinearGradientBrush(accent, dark, 90));
    }

    public static void ApplyScale(double scale) => Set("Sk.Scale", new ScaleTransform(scale, scale));

    private static void Set(string key, Freezable value)
    {
        value.Freeze();
        Application.Current.Resources[key] = value;
    }
}
