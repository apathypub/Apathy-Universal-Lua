using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace Apathy.Controls;

/// <summary>
/// Draws a filled icon geometry designed on a 32-unit grid (see <see cref="TabIcons"/>), scaled so
/// 32 units = <see cref="Size"/> pixels. The element is sized to the geometry's bounds, so wide
/// icons (weapons) work too. The color is inherited from TextElement.Foreground, like the font glyph
/// icons, so tab hover/selected colors apply to it.
/// </summary>
public sealed class GeometryIcon : FrameworkElement
{
    public static readonly DependencyProperty ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner(typeof(GeometryIcon),
            new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DataProperty =
        DependencyProperty.Register(nameof(Data), typeof(Geometry), typeof(GeometryIcon),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SizeProperty =
        DependencyProperty.Register(nameof(Size), typeof(double), typeof(GeometryIcon),
            new FrameworkPropertyMetadata(32.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public Geometry? Data
    {
        get => (Geometry?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    private double Scale => Size / TabIcons.GridSize;

    protected override System.Windows.Size MeasureOverride(System.Windows.Size availableSize)
    {
        if (Data == null) return default;
        var (_, size) = PixelBounds();
        return new System.Windows.Size(size.X, size.Y);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (Data == null) return;

        // Shift by whole pixels only, so straight edges that sit on the grid stay crisp.
        var (origin, _) = PixelBounds();
        dc.PushTransform(new TranslateTransform(-origin.X, -origin.Y));
        dc.PushTransform(new ScaleTransform(Scale, Scale));
        dc.DrawGeometry(Foreground, null, Data);
        dc.Pop();
        dc.Pop();
    }

    // Geometry bounds in pixels, snapped outward to whole pixels.
    private (Point Origin, Vector Size) PixelBounds()
    {
        var bounds = Data!.Bounds;
        double left = Math.Floor(bounds.Left * Scale), top = Math.Floor(bounds.Top * Scale);
        double right = Math.Ceiling(bounds.Right * Scale), bottom = Math.Ceiling(bounds.Bottom * Scale);
        return (new Point(left, top), new Vector(right - left, bottom - top));
    }
}
