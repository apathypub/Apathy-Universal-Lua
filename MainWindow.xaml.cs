using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Apathy.Models;
using Apathy.Overlay;
using Apathy.Services;
using Apathy.Themes;

namespace Apathy;

public partial class MainWindow : Window
{
    private const double MinMenuWidth = 560;
    private const double MinMenuHeight = 420;

    private readonly InputPoller _input = new();
    private readonly OverlayController _overlay;
    private readonly IslandWindow _island;
    private MenuSettings? _settings;
    private TabItem? _dragTab;
    private Point _dragStart;

    public MainWindow()
    {
        InitializeComponent();

        // Drag from any spot that isn't a control (controls mark the click as handled).
        // Clicks inside popups (dropdowns, color picker) also bubble up here, so ignore those.
        MouseLeftButtonDown += (_, e) =>
        {
            if (FindParent<TabItem>(e.OriginalSource as DependencyObject) != null) return;
            if (e.ButtonState == MouseButtonState.Pressed
                && e.OriginalSource is Visual source
                && PresentationSource.FromVisual(source) == PresentationSource.FromVisual(this))
            {
                DragMove();
            }
        };
        SourceInitialized += (_, _) => Native.DisableRoundedCorners(new WindowInteropHelper(this).Handle);

        _island = new IslandWindow(ToggleMenu, OpenScripts, () => OpenEditor(), OpenChat, OpenGeneral);

        SettingsStore.Replaced += (_, _) => Attach(SettingsStore.Current);
        Attach(SettingsStore.Current);

        _input.MenuKeyPressed += (_, _) => ToggleMenu();
        _input.Start();

        // Watermark + Lua HUD; the overlay windows aren't owned by the menu, so they stay
        // up while the menu is hidden and are closed here with it.
        _overlay = new OverlayController();
        _overlay.Start();

        // Toast new chat messages unless the ChatLive tab is already in front of the user.
        ChatLiveClient.Instance.MessageReceived += message =>
        {
            if (IsActive && ChatTab.IsSelected) return;
            NotificationService.Show($"ChatLive: {message.Name}", message.Text);
        };

        Loaded += (_, _) =>
        {
            UpdateIsland();
            // Re-run any scripts that were marked loaded in the current config.
            ScriptService.StartActive();
            ChatLiveClient.Instance.Start(SettingsStore.Current.ChatServerUrl, Session.Username);
        };

        Closed += (_, _) =>
        {
            _input.Stop();
            _overlay.Dispose();
            _island.Close();
            ChatLiveClient.Instance.Stop();
        };
    }

    private void Attach(MenuSettings settings)
    {
        if (_settings != null) _settings.PropertyChanged -= Settings_PropertyChanged;
        _settings = settings;
        _settings.PropertyChanged += Settings_PropertyChanged;
        // The Run key is the source of truth (a config file may be out of date).
        settings.AutoStart = AutoStartService.IsEnabled;

        DataContext = settings;
        _island.DataContext = settings;
        ThemeManager.ApplyAccent(settings.MenuColor);
        ThemeManager.ApplyScale(settings.ScaleFactor);
        if (IsLoaded)
        {
            UpdateIsland();
            _island.RestorePosition();
            ChatLiveClient.Instance.Start(settings.ChatServerUrl, Session.Username);
        }
    }

    private void Settings_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_settings == null) return;

        if (e.PropertyName == nameof(MenuSettings.MenuColor))
            ThemeManager.ApplyAccent(_settings.MenuColor);
        else if (e.PropertyName == nameof(MenuSettings.DpiScale))
            ThemeManager.ApplyScale(_settings.ScaleFactor);
        else if (e.PropertyName == nameof(MenuSettings.ShowIsland))
            UpdateIsland();
    }

    private void UpdateIsland()
    {
        if (_settings?.ShowIsland == true) _island.Show();
        else _island.Hide();
    }

    public void OpenScripts()
    {
        Show();
        Activate();
        LuaTab.IsSelected = true;
    }

    private void OpenChat()
    {
        Show();
        Activate();
        ChatTab.IsSelected = true;
    }

    private void OpenGeneral()
    {
        Show();
        Activate();
        GeneralTab.IsSelected = true;
    }

    /// <summary>Opens the script editor (and a file in it). The menu is hidden so it doesn't sit on top.</summary>
    public void OpenEditor(string? path = null)
    {
        Hide();
        Editor.EditorWindow.Open(path);
    }

    /// <summary>Puts the island back at the top center of the screen.</summary>
    public void ResetIsland()
    {
        if (_settings == null) return;
        _settings.IslandLeft = null;
        _settings.IslandTop = null;
        _island.RestorePosition();
    }

    private void ToggleMenu()
    {
        if (IsVisible)
        {
            Hide();
        }
        else
        {
            Show();
            Activate();
        }
    }

    private void ResizeGrip_DragDelta(object sender, DragDeltaEventArgs e)
    {
        // Thumb deltas are in the unscaled (pre-LayoutTransform) space, same as Root's size.
        Root.Width = Math.Max(MinMenuWidth, Root.Width + e.HorizontalChange);
        Root.Height = Math.Max(MinMenuHeight, Root.Height + e.VerticalChange);
    }

    private void MainTabs_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindParent<TabPanel>(e.OriginalSource as DependencyObject) == null) { _dragTab = null; return; }
        _dragTab = FindParent<TabItem>(e.OriginalSource as DependencyObject);
        if (_dragTab != null) _dragStart = e.GetPosition(null);
    }

    private void MainTabs_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragTab == null || e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _dragStart.X) > SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(pos.Y - _dragStart.Y) > SystemParameters.MinimumVerticalDragDistance)
        {
            DragDrop.DoDragDrop(_dragTab, new DataObject(typeof(TabItem), _dragTab), DragDropEffects.Move);
            _dragTab = null;
        }
    }

    private void MainTabs_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(TabItem)) is not TabItem source) return;
        var target = FindParent<TabItem>(e.OriginalSource as DependencyObject);
        if (target == null || target == source) return;
        int srcIdx = MainTabs.Items.IndexOf(source);
        int tgtIdx = MainTabs.Items.IndexOf(target);
        if (srcIdx < 0 || tgtIdx < 0) return;
        MainTabs.Items.RemoveAt(srcIdx);
        MainTabs.Items.Insert(tgtIdx, source);
        source.IsSelected = true;
    }

    private static T? FindParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child != null)
        {
            if (child is T found) return found;
            // Text elements (Run, Hyperlink...) aren't Visuals: VisualTreeHelper throws on them, so
            // step to their logical parent (the TextBlock) and continue up the visual tree from there.
            child = child is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(child)
                : LogicalTreeHelper.GetParent(child);
        }
        return null;
    }
}
