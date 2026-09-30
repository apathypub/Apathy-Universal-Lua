using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace Apathy.Controls;

/// <summary>
/// Bold text with a 1px black outline (used for slider values), drawn like the pixel-font
/// overlays in game menus: the text is stamped in black around its position, then filled.
/// </summary>
public sealed class OutlinedText : FrameworkElement
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(OutlinedText),
            new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty =
        DependencyProperty.Register(nameof(Fill), typeof(Brush), typeof(OutlinedText),
            new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontSizeProperty =
        TextElement.FontSizeProperty.AddOwner(typeof(OutlinedText),
            new FrameworkPropertyMetadata(10.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly (double X, double Y)[] OutlineOffsets =
    [
        (-1, -1), (0, -1), (1, -1),
        (-1, 0), (1, 0),
        (-1, 1), (0, 1), (1, 1),
    ];

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public Brush Fill
    {
        get => (Brush)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public double FontSize
    {
        get => (double)GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var text = Format(Fill);
        return new Size(Math.Ceiling(text.WidthIncludingTrailingWhitespace) + 2, Math.Ceiling(text.Height) + 2);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var outline = Format(Brushes.Black);
        foreach (var (x, y) in OutlineOffsets)
            dc.DrawText(outline, new Point(1 + x, 1 + y));

        dc.DrawText(Format(Fill), new Point(1, 1));
    }

    private FormattedText Format(Brush brush)
    {
        var typeface = new Typeface(TextElement.GetFontFamily(this), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        return new FormattedText(Text ?? "", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface,
            FontSize, brush, null, TextFormattingMode.Display, VisualTreeHelper.GetDpi(this).PixelsPerDip);
    }
}
