using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Apathy.Services;

namespace Apathy.Overlay;

/// <summary>
/// Stack of toasts anchored to the bottom-right of the screen. Clickable (Accept/Decline, click to
/// dismiss) but never takes focus and stays out of Alt+Tab. Shown only while there are toasts.
/// </summary>
public partial class ToastWindow : Window
{
    public ToastWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => Native.MakeToolWindow(new WindowInteropHelper(this).Handle);
        SizeChanged += (_, _) => Reposition();
        NotificationService.Items.CollectionChanged += (_, _) => UpdateVisibility();
    }

    private void UpdateVisibility()
    {
        bool any = NotificationService.Items.Count > 0;
        if (any && !IsVisible) Show();
        else if (!any && IsVisible) Hide();
    }

    private void Reposition()
    {
        var work = SystemParameters.WorkArea;
        Left = work.Right - ActualWidth;
        Top = work.Bottom - ActualHeight;
    }

    private static Toast? ToastOf(object sender) => (sender as FrameworkElement)?.DataContext as Toast;

    private void Toast_Click(object sender, MouseButtonEventArgs e)
    {
        if (ToastOf(sender) is { IsQuestion: false } toast) NotificationService.Dismiss(toast);
    }

    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        if (ToastOf(sender) is { } toast) NotificationService.Respond(toast, true);
    }

    private void Decline_Click(object sender, RoutedEventArgs e)
    {
        if (ToastOf(sender) is { } toast) NotificationService.Respond(toast, false);
    }
}
