using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Apathy.Models;

namespace Apathy.Controls;

/// <summary>
/// "[KEY]" label bound to a <see cref="Models.Keybind"/>.
/// Left-click → press a key or mouse button (M2-M5) to bind, Esc clears, left-click again cancels.
/// Right-click → choose the mode (Always on / On hotkey / Toggle / Off hotkey).
/// </summary>
public class SkKeybind : Control
{
    public static readonly DependencyProperty KeybindProperty =
        DependencyProperty.Register(nameof(Keybind), typeof(Keybind), typeof(SkKeybind),
            new PropertyMetadata(null, OnKeybindChanged));

    /// <summary>Show the mode menu on right-click.</summary>
    public static readonly DependencyProperty AllowModesProperty =
        DependencyProperty.Register(nameof(AllowModes), typeof(bool), typeof(SkKeybind), new PropertyMetadata(true));

    /// <summary>Whether Esc may clear the bind (disable for binds that must always have a key, like the menu key).</summary>
    public static readonly DependencyProperty AllowEmptyProperty =
        DependencyProperty.Register(nameof(AllowEmpty), typeof(bool), typeof(SkKeybind), new PropertyMetadata(true));

    private static readonly DependencyPropertyKey DisplayTextPropertyKey =
        DependencyProperty.RegisterReadOnly(nameof(DisplayText), typeof(string), typeof(SkKeybind), new PropertyMetadata("[-]"));
    public static readonly DependencyProperty DisplayTextProperty = DisplayTextPropertyKey.DependencyProperty;

    private static readonly DependencyPropertyKey IsCapturingPropertyKey =
        DependencyProperty.RegisterReadOnly(nameof(IsCapturing), typeof(bool), typeof(SkKeybind), new PropertyMetadata(false));
    public static readonly DependencyProperty IsCapturingProperty = IsCapturingPropertyKey.DependencyProperty;

    private static readonly (KeybindMode Mode, string Text)[] Modes =
    [
        (KeybindMode.Always, "Always on"),
        (KeybindMode.Hold, "On hotkey"),
        (KeybindMode.Toggle, "Toggle"),
        (KeybindMode.OffHotkey, "Off hotkey"),
    ];

    private ContextMenu? _modeMenu;
    private bool _swallowRightUp;

    static SkKeybind()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(SkKeybind), new FrameworkPropertyMetadata(typeof(SkKeybind)));
        FocusableProperty.OverrideMetadata(typeof(SkKeybind), new FrameworkPropertyMetadata(true));
    }

    /// <summary>True while any keybind is waiting for a key, so global input polling can pause.</summary>
    public static bool IsAnyCapturing { get; private set; }

    public Keybind? Keybind
    {
        get => (Keybind?)GetValue(KeybindProperty);
        set => SetValue(KeybindProperty, value);
    }

    public bool AllowModes
    {
        get => (bool)GetValue(AllowModesProperty);
        set => SetValue(AllowModesProperty, value);
    }

    public bool AllowEmpty
    {
        get => (bool)GetValue(AllowEmptyProperty);
        set => SetValue(AllowEmptyProperty, value);
    }

    public string DisplayText => (string)GetValue(DisplayTextProperty);

    public bool IsCapturing
    {
        get => (bool)GetValue(IsCapturingProperty);
        private set
        {
            SetValue(IsCapturingPropertyKey, value);
            IsAnyCapturing = value;
            UpdateDisplayText();
        }
    }

    private static void OnKeybindChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (SkKeybind)d;
        if (e.OldValue is Keybind oldBind) oldBind.PropertyChanged -= control.Keybind_PropertyChanged;
        if (e.NewValue is Keybind newBind) newBind.PropertyChanged += control.Keybind_PropertyChanged;
        control.UpdateDisplayText();
    }

    private void Keybind_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Models.Keybind.Key)) UpdateDisplayText();
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);

        if (IsCapturing)
        {
            // Bind the clicked mouse button, including M1. (To cancel: press Esc or click away.)
            int vk = e.ChangedButton switch
            {
                MouseButton.Left => 0x01,
                MouseButton.Right => 0x02,
                MouseButton.Middle => 0x04,
                MouseButton.XButton1 => 0x05,
                MouseButton.XButton2 => 0x06,
                _ => 0,
            };
            if (vk != 0) Assign(vk);

            _swallowRightUp = e.ChangedButton == MouseButton.Right;
            e.Handled = true;
            return;
        }

        if (e.ChangedButton == MouseButton.Left)
        {
            Focus();
            IsCapturing = true;
            e.Handled = true;
        }
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.ChangedButton != MouseButton.Right) return;

        if (_swallowRightUp)
        {
            _swallowRightUp = false;
            e.Handled = true;
            return;
        }

        if (AllowModes && Keybind != null)
        {
            _modeMenu ??= BuildModeMenu();
            _modeMenu.PlacementTarget = this;
            _modeMenu.Placement = PlacementMode.Bottom;
            _modeMenu.IsOpen = true;
            e.Handled = true;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (!IsCapturing)
        {
            base.OnKeyDown(e);
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            if (AllowEmpty) Assign(0);
            else IsCapturing = false;
        }
        else
        {
            int vk = KeyInterop.VirtualKeyFromKey(key);
            if (vk != 0) Assign(vk);
        }
        e.Handled = true;
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        if (IsCapturing) IsCapturing = false;
    }

    private void Assign(int virtualKey)
    {
        if (Keybind != null) Keybind.Key = virtualKey;
        IsCapturing = false;
    }

    private void UpdateDisplayText()
    {
        string text = IsCapturing ? "..." : KeyName(Keybind?.Key ?? 0);
        SetValue(DisplayTextPropertyKey, $"[{text}]");
    }

    private ContextMenu BuildModeMenu()
    {
        var menu = new ContextMenu();
        foreach (var (mode, text) in Modes)
        {
            var item = new MenuItem { Header = text, Tag = mode };
            item.Click += (_, _) =>
            {
                if (Keybind != null) Keybind.Mode = mode;
            };
            menu.Items.Add(item);
        }
        menu.Opened += (_, _) =>
        {
            foreach (MenuItem item in menu.Items)
                item.IsChecked = Keybind?.Mode == (KeybindMode)item.Tag;
        };
        return menu;
    }

    /// <summary>Short display name for a Win32 virtual-key code.</summary>
    public static string KeyName(int virtualKey)
    {
        switch (virtualKey)
        {
            case 0: return "-";
            case 0x01: return "M1";
            case 0x02: return "M2";
            case 0x04: return "M3";
            case 0x05: return "M4";
            case 0x06: return "M5";
        }

        var key = KeyInterop.KeyFromVirtualKey(virtualKey);
        return key switch
        {
            >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
            >= Key.NumPad0 and <= Key.NumPad9 => "NUM" + (key - Key.NumPad0),
            Key.LeftShift => "LSHIFT",
            Key.RightShift => "RSHIFT",
            Key.LeftCtrl => "LCTRL",
            Key.RightCtrl => "RCTRL",
            Key.LeftAlt => "LALT",
            Key.RightAlt => "RALT",
            Key.Return => "ENTER",
            Key.Back => "BACK",
            Key.Capital => "CAPS",
            Key.Prior => "PGUP",
            Key.Next => "PGDN",
            Key.Insert => "INS",
            Key.Delete => "DEL",
            Key.None => "0x" + virtualKey.ToString("X2"),
            _ => key.ToString().ToUpperInvariant(),
        };
    }
}
