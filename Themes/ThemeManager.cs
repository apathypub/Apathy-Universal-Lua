using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Apathy.Themes;

/// <summary>Replaces the runtime resources declared at the bottom of Palette.xaml.</summary>
public static class ThemeManager
{
    /// <summary>The page background: flat, like the forum.</summary>
    public static void InstallPattern() => Set("Sk.Pattern", new SolidColorBrush(Color.FromRgb(0x0F, 0x0E, 0x0E)));

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
