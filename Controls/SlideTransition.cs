using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Apathy.Controls;

/// <summary>
/// Attached behavior for a TabControl: when the selected tab changes, the new page slides in
/// from the side of the clicked tab (right when moving right, left when moving left) and fades in.
/// Usage: <c>c:SlideTransition.IsEnabled="True"</c>. The template's PART_SelectedContentHost is
/// animated, so its container should clip (Sk.CategoryTabs does).
/// </summary>
public static class SlideTransition
{
    private const double Distance = 40;
    private static readonly Duration Duration = TimeSpan.FromMilliseconds(220);

    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached("IsEnabled", typeof(bool), typeof(SlideTransition),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TabControl tabs) return;

        tabs.SelectionChanged -= Tabs_SelectionChanged;
        if ((bool)e.NewValue) tabs.SelectionChanged += Tabs_SelectionChanged;
    }

    private static void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var tabs = (TabControl)sender;

        // Selection changes of dropdowns/lists inside the pages bubble up here too; only react to our own.
        if (!ReferenceEquals(e.OriginalSource, tabs) || !tabs.IsLoaded || !SystemParameters.ClientAreaAnimation) return;
        if (tabs.Template?.FindName("PART_SelectedContentHost", tabs) is not FrameworkElement host) return;

        int oldIndex = e.RemovedItems.Count > 0 ? tabs.Items.IndexOf(e.RemovedItems[0]) : -1;
        double direction = oldIndex >= 0 && tabs.SelectedIndex < oldIndex ? -1 : 1;

        var slide = new TranslateTransform(direction * Distance, 0);
        host.RenderTransform = slide;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, Duration) { EasingFunction = ease });
        host.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Duration) { EasingFunction = ease });
    }
}
