using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media;

namespace Apathy.Models;

/// <summary>A group of controls a single Lua script has added (shown as its own box in the General tab).</summary>
public sealed class LuaScriptPanel : ObservableObject
{
    public LuaScriptPanel(string name) => Name = name;

    public string Name { get; }

    public ObservableCollection<LuaControl> Controls { get; } = [];
}

/// <summary>Base for controls a script creates via the <c>ui</c> API.</summary>
public abstract class LuaControl : ObservableObject
{
    protected LuaControl(string label) => Label = label;

    public string Label { get; set => SetField(ref field, value); }
}

public sealed class LuaCheckbox(string label, bool value) : LuaControl(label)
{
    public bool Value { get; set => SetField(ref field, value); } = value;
}

public sealed class LuaSliderControl(string label, double value, double min, double max, string suffix, int decimals)
    : LuaControl(label)
{
    public double Value { get; set => SetField(ref field, value); } = value;
    public double Min { get; } = min;
    public double Max { get; } = max;
    public string Suffix { get; } = suffix;
    public int Decimals { get; } = decimals;
}

/// <summary>A drop-down list a script adds via ui.combo. <see cref="Index"/> is 0-based (Lua sees 1-based).</summary>
public sealed class LuaComboControl(string label, IReadOnlyList<string> items, int index) : LuaControl(label)
{
    public IReadOnlyList<string> Items { get; } = items;
    public int Index { get; set => SetField(ref field, value); } = index;
}

/// <summary>A color swatch a script adds via ui.color.</summary>
public sealed class LuaColorControl(string label, Color color) : LuaControl(label)
{
    public Color Color { get; set => SetField(ref field, value); } = color;
}

public sealed class LuaTextControl(string text) : LuaControl(text)
{
    public string Text { get => Label; set => Label = value; }
}

public sealed class LuaButtonControl : LuaControl
{
    public LuaButtonControl(string label, Action onClick) : base(label) =>
        Command = new RelayCommand(onClick);

    public ICommand Command { get; }
}

/// <summary>A rebindable hotkey a script adds via ui.keybind. State fields are updated by LuaHost.</summary>
public sealed class LuaKeybindControl(string label, Keybind keybind) : LuaControl(label)
{
    public Keybind Keybind { get; } = keybind;

    /// <summary>Physical key state this tick.</summary>
    public bool IsDown { get; set; }

    /// <summary>True on the tick the key went down.</summary>
    public bool JustPressed { get; set; }

    /// <summary>Previous physical state (null = reset, e.g. while rebinding).</summary>
    public bool? WasDown { get; set; }

    /// <summary>KeyPresses count at the previous tick (null = reset).</summary>
    public int? PressCount { get; set; }
}

/// <summary>Minimal ICommand that always can execute.</summary>
public sealed class RelayCommand(Action execute) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => execute();
}
