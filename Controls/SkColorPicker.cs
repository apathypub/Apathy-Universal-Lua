using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Apathy.Controls;

/// <summary>
/// Small color swatch that opens an HSV picker (saturation/value square, hue strip, alpha strip).
/// Right-click the swatch to copy/paste the color as #AARRGGBB.
/// </summary>
[TemplatePart(Name = "PART_Toggle", Type = typeof(ToggleButton))]
[TemplatePart(Name = "PART_Sv", Type = typeof(FrameworkElement))]
[TemplatePart(Name = "PART_Hue", Type = typeof(FrameworkElement))]
[TemplatePart(Name = "PART_Alpha", Type = typeof(FrameworkElement))]
public class SkColorPicker : Control
{
    public static readonly DependencyProperty ColorProperty =
        DependencyProperty.Register(nameof(Color), typeof(Color), typeof(SkColorPicker),
            new FrameworkPropertyMetadata(Colors.White, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnColorChanged));

    public static readonly DependencyProperty ShowAlphaProperty =
        DependencyProperty.Register(nameof(ShowAlpha), typeof(bool), typeof(SkColorPicker), new PropertyMetadata(true));

    private static readonly DependencyPropertyKey SwatchBrushPropertyKey =
        DependencyProperty.RegisterReadOnly(nameof(SwatchBrush), typeof(Brush), typeof(SkColorPicker), new PropertyMetadata(Brushes.White));
    public static readonly DependencyProperty SwatchBrushProperty = SwatchBrushPropertyKey.DependencyProperty;

    private static readonly DependencyPropertyKey HueBrushPropertyKey =
        DependencyProperty.RegisterReadOnly(nameof(HueBrush), typeof(Brush), typeof(SkColorPicker), new PropertyMetadata(Brushes.Red));
    public static readonly DependencyProperty HueBrushProperty = HueBrushPropertyKey.DependencyProperty;

    private static readonly DependencyPropertyKey AlphaBrushPropertyKey =
        DependencyProperty.RegisterReadOnly(nameof(AlphaBrush), typeof(Brush), typeof(SkColorPicker), new PropertyMetadata(Brushes.White));
    public static readonly DependencyProperty AlphaBrushProperty = AlphaBrushPropertyKey.DependencyProperty;

    // Kept separately from Color so hue/saturation survive black, white and gray colors.
    private double _hue, _saturation, _brightness = 1;
    private bool _settingColor;

    private FrameworkElement? _sv, _hueStrip, _alphaStrip;
    private FrameworkElement? _svMarker, _hueMarker, _alphaMarker;

    static SkColorPicker()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(SkColorPicker), new FrameworkPropertyMetadata(typeof(SkColorPicker)));
    }

    public SkColorPicker()
    {
        SyncFromColor();
        UpdateBrushes();
    }

    public Color Color
    {
        get => (Color)GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    public bool ShowAlpha
    {
        get => (bool)GetValue(ShowAlphaProperty);
        set => SetValue(ShowAlphaProperty, value);
    }

    public Brush SwatchBrush => (Brush)GetValue(SwatchBrushProperty);
    public Brush HueBrush => (Brush)GetValue(HueBrushProperty);
    public Brush AlphaBrush => (Brush)GetValue(AlphaBrushProperty);

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (GetTemplateChild("PART_Toggle") is ToggleButton toggle)
            toggle.ContextMenu = BuildClipboardMenu();

        _sv = GetTemplateChild("PART_Sv") as FrameworkElement;
        _hueStrip = GetTemplateChild("PART_Hue") as FrameworkElement;
        _alphaStrip = GetTemplateChild("PART_Alpha") as FrameworkElement;
        _svMarker = GetTemplateChild("PART_SvMarker") as FrameworkElement;
        _hueMarker = GetTemplateChild("PART_HueMarker") as FrameworkElement;
        _alphaMarker = GetTemplateChild("PART_AlphaMarker") as FrameworkElement;

        HookDrag(_sv, (x, y) =>
        {
            _saturation = x;
            _brightness = 1 - y;
            ApplyHsv();
        });
        HookDrag(_hueStrip, (_, y) =>
        {
            _hue = y * 360;
            ApplyHsv();
        });
        HookDrag(_alphaStrip, (x, _) =>
        {
            var c = Color;
            SetColor(Color.FromArgb((byte)Math.Round(x * 255), c.R, c.G, c.B));
        });

        UpdateMarkers();
    }

    private static void OnColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var picker = (SkColorPicker)d;
        if (!picker._settingColor) picker.SyncFromColor();
        picker.UpdateBrushes();
        picker.UpdateMarkers();
    }

    private void HookDrag(FrameworkElement? element, Action<double, double> apply)
    {
        if (element == null) return;

        void Apply(MouseEventArgs e)
        {
            var p = e.GetPosition(element);
            apply(Math.Clamp(p.X / element.ActualWidth, 0, 1), Math.Clamp(p.Y / element.ActualHeight, 0, 1));
        }

        element.MouseLeftButtonDown += (_, e) =>
        {
            element.CaptureMouse();
            Apply(e);
            e.Handled = true;
        };
        element.MouseMove += (_, e) =>
        {
            if (element.IsMouseCaptured) Apply(e);
        };
        element.MouseLeftButtonUp += (_, _) => element.ReleaseMouseCapture();
        element.SizeChanged += (_, _) => UpdateMarkers();
    }

    private void ApplyHsv() => SetColor(FromHsv(_hue, _saturation, _brightness, Color.A));

    private void SetColor(Color color)
    {
        _settingColor = true;
        try
        {
            SetCurrentValue(ColorProperty, color);
        }
        finally
        {
            _settingColor = false;
        }
        UpdateMarkers();
    }

    private void SyncFromColor()
    {
        var (h, s, v) = ToHsv(Color);
        if (v > 0 && s > 0) _hue = h;
        if (v > 0) _saturation = s;
        _brightness = v;
    }

    private void UpdateBrushes()
    {
        var c = Color;
        SetValue(SwatchBrushPropertyKey, Frozen(new SolidColorBrush(c)));
        SetValue(HueBrushPropertyKey, Frozen(new SolidColorBrush(FromHsv(_hue, 1, 1, 255))));
        SetValue(AlphaBrushPropertyKey, Frozen(new LinearGradientBrush(
            Color.FromArgb(0, c.R, c.G, c.B), Color.FromArgb(255, c.R, c.G, c.B), 0)));
    }

    private void UpdateMarkers()
    {
        if (_sv != null && _svMarker != null)
        {
            Canvas.SetLeft(_svMarker, Math.Round(_saturation * _sv.ActualWidth) - 2);
            Canvas.SetTop(_svMarker, Math.Round((1 - _brightness) * _sv.ActualHeight) - 2);
        }
        if (_hueStrip != null && _hueMarker != null)
            Canvas.SetTop(_hueMarker, Math.Round(_hue / 360 * _hueStrip.ActualHeight) - 1);
        if (_alphaStrip != null && _alphaMarker != null)
            Canvas.SetLeft(_alphaMarker, Math.Round(Color.A / 255.0 * _alphaStrip.ActualWidth) - 1);
    }

    private ContextMenu BuildClipboardMenu()
    {
        var copy = new MenuItem { Header = "Copy" };
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(Color.ToString()); }
            catch (System.Runtime.InteropServices.ExternalException) { /* clipboard busy */ }
        };

        var paste = new MenuItem { Header = "Paste" };
        paste.Click += (_, _) =>
        {
            try
            {
                if (ColorConverter.ConvertFromString(Clipboard.GetText().Trim()) is Color pasted)
                    SetCurrentValue(ColorProperty, ShowAlpha ? pasted : Color.FromArgb(255, pasted.R, pasted.G, pasted.B));
            }
            catch (FormatException) { /* not a color */ }
            catch (System.Runtime.InteropServices.ExternalException) { /* clipboard busy */ }
        };

        var menu = new ContextMenu();
        menu.Items.Add(copy);
        menu.Items.Add(paste);
        return menu;
    }

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    internal static Color FromHsv(double h, double s, double v, byte a)
    {
        h = ((h % 360) + 360) % 360;
        double c = v * s;
        double x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        double m = v - c;
        var (r, g, b) = (int)(h / 60) switch
        {
            0 => (c, x, 0d),
            1 => (x, c, 0d),
            2 => (0d, c, x),
            3 => (0d, x, c),
            4 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        return Color.FromArgb(a, ToByte(r + m), ToByte(g + m), ToByte(b + m));
    }

    internal static (double H, double S, double V) ToHsv(Color color)
    {
        double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double d = max - min;

        double h = d == 0 ? 0
            : max == r ? 60 * ((g - b) / d % 6)
            : max == g ? 60 * ((b - r) / d + 2)
            : 60 * ((r - g) / d + 4);
        if (h < 0) h += 360;

        return (h, max == 0 ? 0 : d / max, max);
    }

    private static byte ToByte(double value) => (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);
}
