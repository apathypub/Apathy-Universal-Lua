using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Apathy.Controls;

/// <summary>
/// Flat slider: click or drag anywhere on the bar, value drawn at the end of the fill,
/// "-" / "+" step buttons appear while hovered.
/// </summary>
[TemplatePart(Name = "PART_Area", Type = typeof(FrameworkElement))]
[TemplatePart(Name = "PART_Bar", Type = typeof(FrameworkElement))]
[TemplatePart(Name = "PART_Fill", Type = typeof(FrameworkElement))]
[TemplatePart(Name = "PART_Value", Type = typeof(FrameworkElement))]
[TemplatePart(Name = "PART_Decrease", Type = typeof(ButtonBase))]
[TemplatePart(Name = "PART_Increase", Type = typeof(ButtonBase))]
public class SkSlider : RangeBase
{
    public static readonly DependencyProperty HeaderProperty =
        DependencyProperty.Register(nameof(Header), typeof(string), typeof(SkSlider));

    public static readonly DependencyProperty SuffixProperty =
        DependencyProperty.Register(nameof(Suffix), typeof(string), typeof(SkSlider),
            new PropertyMetadata("", OnFormatChanged));

    public static readonly DependencyProperty DecimalsProperty =
        DependencyProperty.Register(nameof(Decimals), typeof(int), typeof(SkSlider),
            new PropertyMetadata(0, OnFormatChanged));

    /// <summary>Text shown instead of the number when the value is at Maximum (e.g. "∞").</summary>
    public static readonly DependencyProperty MaximumTextProperty =
        DependencyProperty.Register(nameof(MaximumText), typeof(string), typeof(SkSlider),
            new PropertyMetadata(null, OnFormatChanged));

    private static readonly DependencyPropertyKey ValueTextPropertyKey =
        DependencyProperty.RegisterReadOnly(nameof(ValueText), typeof(string), typeof(SkSlider),
            new PropertyMetadata(""));

    public static readonly DependencyProperty ValueTextProperty = ValueTextPropertyKey.DependencyProperty;

    private FrameworkElement? _area;
    private FrameworkElement? _bar;
    private FrameworkElement? _fill;
    private FrameworkElement? _value;
    private ButtonBase? _decrease;
    private ButtonBase? _increase;

    static SkSlider()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(SkSlider), new FrameworkPropertyMetadata(typeof(SkSlider)));
        MaximumProperty.OverrideMetadata(typeof(SkSlider), new FrameworkPropertyMetadata(100.0));
    }

    public string? Header
    {
        get => (string?)GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    /// <summary>Unit appended to the displayed value, e.g. "%", "ms", "°".</summary>
    public string Suffix
    {
        get => (string)GetValue(SuffixProperty);
        set => SetValue(SuffixProperty, value);
    }

    /// <summary>Number of decimal places the value is rounded to (also sets the +/- step).</summary>
    public int Decimals
    {
        get => (int)GetValue(DecimalsProperty);
        set => SetValue(DecimalsProperty, value);
    }

    public string? MaximumText
    {
        get => (string?)GetValue(MaximumTextProperty);
        set => SetValue(MaximumTextProperty, value);
    }

    public string ValueText => (string)GetValue(ValueTextProperty);

    protected override AutomationPeer OnCreateAutomationPeer() => new SkSliderAutomationPeer(this);

    public override void OnApplyTemplate()
    {
        if (_area != null)
        {
            _area.MouseLeftButtonDown -= Area_MouseLeftButtonDown;
            _area.MouseMove -= Area_MouseMove;
            _area.MouseLeftButtonUp -= Area_MouseLeftButtonUp;
        }
        if (_bar != null) _bar.SizeChanged -= Bar_SizeChanged;
        if (_decrease != null) _decrease.Click -= Decrease_Click;
        if (_increase != null) _increase.Click -= Increase_Click;

        base.OnApplyTemplate();

        _area = GetTemplateChild("PART_Area") as FrameworkElement;
        _bar = GetTemplateChild("PART_Bar") as FrameworkElement;
        _fill = GetTemplateChild("PART_Fill") as FrameworkElement;
        _value = GetTemplateChild("PART_Value") as FrameworkElement;
        _decrease = GetTemplateChild("PART_Decrease") as ButtonBase;
        _increase = GetTemplateChild("PART_Increase") as ButtonBase;

        if (_area != null)
        {
            _area.MouseLeftButtonDown += Area_MouseLeftButtonDown;
            _area.MouseMove += Area_MouseMove;
            _area.MouseLeftButtonUp += Area_MouseLeftButtonUp;
        }
        if (_bar != null) _bar.SizeChanged += Bar_SizeChanged;
        if (_decrease != null) _decrease.Click += Decrease_Click;
        if (_increase != null) _increase.Click += Increase_Click;

        UpdateVisuals();
    }

    protected override void OnValueChanged(double oldValue, double newValue)
    {
        base.OnValueChanged(oldValue, newValue);
        UpdateVisuals();
    }

    protected override void OnMinimumChanged(double oldMinimum, double newMinimum)
    {
        base.OnMinimumChanged(oldMinimum, newMinimum);
        UpdateVisuals();
    }

    protected override void OnMaximumChanged(double oldMaximum, double newMaximum)
    {
        base.OnMaximumChanged(oldMaximum, newMaximum);
        UpdateVisuals();
    }

    private static void OnFormatChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((SkSlider)d).UpdateVisuals();

    private void Bar_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateVisuals();

    private void Area_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_area == null || _bar == null) return;
        _area.CaptureMouse();
        SetValueFromPoint(e.GetPosition(_bar));
        e.Handled = true;
    }

    private void Area_MouseMove(object sender, MouseEventArgs e)
    {
        if (_area is { IsMouseCaptured: true } && _bar != null)
            SetValueFromPoint(e.GetPosition(_bar));
    }

    private void Area_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_area is not { IsMouseCaptured: true }) return;
        _area.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void Decrease_Click(object sender, RoutedEventArgs e) => Step(-1);

    private void Increase_Click(object sender, RoutedEventArgs e) => Step(1);

    private void Step(int direction)
    {
        double step = Math.Pow(10, -Decimals);
        SetCurrentValue(ValueProperty, Math.Round(Value + direction * step, Decimals));
    }

    private void SetValueFromPoint(Point point)
    {
        double inner = _bar!.ActualWidth - 2;
        if (inner <= 0) return;

        double ratio = Math.Clamp((point.X - 1) / inner, 0, 1);
        SetCurrentValue(ValueProperty, Math.Round(Minimum + ratio * (Maximum - Minimum), Decimals));
    }

    private void UpdateVisuals()
    {
        SetValue(ValueTextPropertyKey, MaximumText != null && Value >= Maximum
            ? MaximumText
            : Value.ToString("F" + Decimals, CultureInfo.InvariantCulture) + Suffix);

        if (_bar == null || _fill == null) return;

        double range = Maximum - Minimum;
        double ratio = range <= 0 ? 0 : (Value - Minimum) / range;
        double fill = Math.Round(Math.Max(0, _bar.ActualWidth - 2) * ratio);
        _fill.Width = fill;

        if (_value != null)
        {
            // Centered on the end of the fill, but kept within the bar.
            _value.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double width = _value.DesiredSize.Width;
            double left = Math.Clamp(1 + fill - width / 2, -1, Math.Max(-1, _bar.ActualWidth - width + 1));
            Canvas.SetLeft(_value, Math.Round(left));
        }
    }

    private sealed class SkSliderAutomationPeer(SkSlider owner) : RangeBaseAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Slider;

        protected override string GetClassNameCore() => nameof(SkSlider);

        protected override string GetNameCore()
        {
            string name = base.GetNameCore();
            return string.IsNullOrEmpty(name) ? ((SkSlider)Owner).Header ?? "" : name;
        }
    }
}
