using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls.Primitives;

namespace Apathy.Controls;

/// <summary>
/// Makes a <see cref="Popup"/> follow its window. WPF popups are placed once when opened and keep
/// their screen position afterwards, so dragging or resizing the menu leaves an open dropdown / color
/// picker behind. While open, this re-runs the popup's placement on the window's
/// LocationChanged / SizeChanged. Enable with <c>c:PopupTracker.TrackWindow="True"</c>.
/// </summary>
public static class PopupTracker
{
    public static readonly DependencyProperty TrackWindowProperty =
        DependencyProperty.RegisterAttached("TrackWindow", typeof(bool), typeof(PopupTracker),
            new PropertyMetadata(false, OnTrackWindowChanged));

    // Remembers the window each open popup is hooked to, so the handlers can be removed on close.
    private static readonly ConditionalWeakTable<Popup, Window> Hooked = new();

    public static bool GetTrackWindow(DependencyObject element) => (bool)element.GetValue(TrackWindowProperty);

    public static void SetTrackWindow(DependencyObject element, bool value) => element.SetValue(TrackWindowProperty, value);

    private static void OnTrackWindowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Popup popup) return;

        popup.Opened -= OnPopupOpened;
        popup.Closed -= OnPopupClosed;
        if ((bool)e.NewValue)
        {
            popup.Opened += OnPopupOpened;
            popup.Closed += OnPopupClosed;
        }
    }

    private static void OnPopupOpened(object? sender, EventArgs e)
    {
        if (sender is not Popup popup) return;

        var anchor = popup.PlacementTarget ?? popup.Parent as UIElement;
        if (anchor == null || Window.GetWindow(anchor) is not { } window) return;

        Hooked.AddOrUpdate(popup, window);
        window.LocationChanged += popup.FollowWindow;
        window.SizeChanged += popup.FollowWindow;
    }

    private static void OnPopupClosed(object? sender, EventArgs e)
    {
        if (sender is not Popup popup || !Hooked.TryGetValue(popup, out var window)) return;

        window.LocationChanged -= popup.FollowWindow;
        window.SizeChanged -= popup.FollowWindow;
        Hooked.Remove(popup);
    }

    // Toggling an offset forces WPF to recompute the popup's position relative to its target.
    private static void FollowWindow(this Popup popup, object? sender, EventArgs e)
    {
        if (!popup.IsOpen) return;
        double offset = popup.HorizontalOffset;
        popup.HorizontalOffset = offset + 0.5;
        popup.HorizontalOffset = offset;
    }
}
