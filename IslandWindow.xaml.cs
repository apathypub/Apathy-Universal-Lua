using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Apathy.Models;
using Apathy.Services;

namespace Apathy;

/// <summary>
/// Small always-on-top bar with the connection dot and shortcuts. Draggable (unless locked),
/// never takes focus and stays out of Alt+Tab.
/// </summary>
public partial class IslandWindow : Window
{
    private const double TopGap = 6;

    private readonly Action _toggleMenu;
    private readonly Action _openScripts;
    private readonly Action _openEditor;
    private readonly Action _openChat;
    private readonly Action _openGeneral;
    private readonly ObservableCollection<string> _activeScripts = [];

    public IslandWindow(Action toggleMenu, Action openScripts, Action openEditor, Action openChat, Action openGeneral)
    {
        _toggleMenu = toggleMenu;
        _openScripts = openScripts;
        _openEditor = openEditor;
        _openChat = openChat;
        _openGeneral = openGeneral;
        InitializeComponent();
        ActiveScripts.ItemsSource = _activeScripts;

        SourceInitialized += (_, _) => Native.MakeToolWindow(new WindowInteropHelper(this).Handle);
        Loaded += (_, _) => RestorePosition();
        ScriptService.Changed += (_, _) => RefreshActiveScripts();
        RefreshActiveScripts();
        // Folding the shortcuts changes the width; keep the default spot centered.
        SizeChanged += (_, _) => RestorePosition();
        // New settings (startup, config load): show their folded state without animating.
        DataContextChanged += (_, _) => ApplyCollapsed(animate: false);

        // Buttons handle their own clicks, so this only fires on the bar itself.
        MouseLeftButtonDown += (_, e) =>
        {
            var settings = SettingsStore.Current;
            if (settings.LockIsland || e.ButtonState != MouseButtonState.Pressed) return;
            DragMove();
            settings.IslandLeft = Left;
            settings.IslandTop = Top;
        };
    }

    /// <summary>Moves the island to its saved spot, or top center when there is none (or it's off-screen).</summary>
    public void RestorePosition()
    {
        var settings = SettingsStore.Current;
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                              SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);

        if (settings.IslandLeft is { } left && settings.IslandTop is { } top && screen.Contains(new Point(left, top)))
        {
            Left = left;
            Top = top;
            return;
        }

        var work = SystemParameters.WorkArea;
        Left = work.Left + (work.Width - ActualWidth) / 2;
        Top = work.Top + TopGap;
    }

    private void ChatLive_Click(object sender, RoutedEventArgs e) => _openChat();

    private void Collapse_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MenuSettings settings) return;
        settings.IslandCollapsed = !settings.IslandCollapsed;
        ApplyCollapsed(animate: true);
    }

    /// <summary>Slides the shortcuts closed/open behind the arrow (clip width + fade) and turns the arrow around.</summary>
    private void ApplyCollapsed(bool animate)
    {
        bool collapsed = (DataContext as MenuSettings)?.IslandCollapsed == true;
        var duration = animate ? TimeSpan.FromMilliseconds(240) : TimeSpan.Zero;
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };

        // Full width of the shortcuts, measured without the clip's current (animated) width.
        Shortcuts.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double full = Shortcuts.DesiredSize.Width;
        double from = double.IsNaN(ShortcutsClip.Width) ? ShortcutsClip.ActualWidth : ShortcutsClip.Width;

        var width = new DoubleAnimation(from, collapsed ? 0 : full, duration) { EasingFunction = ease };
        // Once open, go back to automatic width so turning shortcuts on/off in Misc still resizes the island.
        if (!collapsed)
        {
            width.Completed += (_, _) =>
            {
                if ((DataContext as MenuSettings)?.IslandCollapsed == true) return;
                ShortcutsClip.BeginAnimation(WidthProperty, null);
                ShortcutsClip.Width = double.NaN;
            };
        }
        ShortcutsClip.BeginAnimation(WidthProperty, width);
        Shortcuts.BeginAnimation(OpacityProperty, new DoubleAnimation(collapsed ? 0 : 1, duration) { EasingFunction = ease });
        ((RotateTransform)Chevron.RenderTransform).BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(collapsed ? 180 : 0, duration) { EasingFunction = ease });
        Shortcuts.IsHitTestVisible = !collapsed;
    }

    private void Scripts_Click(object sender, RoutedEventArgs e) => _openScripts();

    private void Editor_Click(object sender, RoutedEventArgs e) => _openEditor();

    private void Menu_Click(object sender, RoutedEventArgs e) => _toggleMenu();

    private void Shortcut_Click(object sender, RoutedEventArgs e) =>
        ((sender as Button)?.DataContext as IslandShortcut)?.Invoke();

    private void ActiveScript_Click(object sender, RoutedEventArgs e) => _openGeneral();

    private void RefreshActiveScripts()
    {
        _activeScripts.Clear();
        foreach (string name in ScriptService.Loaded()) _activeScripts.Add(name);
        // The list changes the island width; keep the default spot centered.
        RestorePosition();
    }
}
