using System.Text.Json.Serialization;

namespace Apathy.Models;

public enum KeybindMode
{
    /// <summary>Active regardless of the key.</summary>
    Always,
    /// <summary>Active while the key is held.</summary>
    Hold,
    /// <summary>Each press flips the state.</summary>
    Toggle,
    /// <summary>Active unless the key is held.</summary>
    OffHotkey,
}

/// <summary>
/// A hotkey attached to a feature. <see cref="Key"/> is a Win32 virtual-key code (0 = unbound),
/// <see cref="IsActive"/> is kept up to date by <see cref="Services.InputPoller"/> even while the
/// menu is hidden, so feature code only has to check it.
/// </summary>
public sealed class Keybind : ObservableObject
{
    public int Key { get; set => SetField(ref field, value); }

    public KeybindMode Mode { get; set => SetField(ref field, value); } = KeybindMode.Always;

    [JsonIgnore]
    public bool IsActive { get; private set => SetField(ref field, value); }

    internal void Update(bool isDown, bool wasPressed)
    {
        IsActive = Mode switch
        {
            KeybindMode.Always => true,
            KeybindMode.Hold => Key != 0 && isDown,
            KeybindMode.Toggle => wasPressed ? !IsActive : IsActive,
            KeybindMode.OffHotkey => Key == 0 || !isDown,
            _ => false,
        };
    }
}
