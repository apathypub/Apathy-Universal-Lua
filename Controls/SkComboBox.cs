using System.Windows;
using System.Windows.Controls;

namespace Apathy.Controls;

/// <summary>Standard ComboBox with a header label above the box.</summary>
public class SkComboBox : ComboBox
{
    public static readonly DependencyProperty HeaderProperty =
        DependencyProperty.Register(nameof(Header), typeof(string), typeof(SkComboBox));

    static SkComboBox()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(SkComboBox), new FrameworkPropertyMetadata(typeof(SkComboBox)));
    }

    public string? Header
    {
        get => (string?)GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }
}
