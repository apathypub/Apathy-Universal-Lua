namespace Apathy.Services;

/// <summary>
/// Win32 virtual-key codes by name, exposed to Lua as the <c>keys</c> table (keys.F1, keys.SPACE, keys.A...)
/// so scripts don't need to look up hex codes for key_down / ui.keybind. Also used by the editor's hints.
/// </summary>
public static class ScriptKeys
{
    public static IReadOnlyList<(string Name, int Code)> All { get; } = Build();

    private static List<(string, int)> Build()
    {
        List<(string, int)> keys =
        [
            ("MOUSE1", 0x01), ("MOUSE2", 0x02), ("MOUSE3", 0x04), ("MOUSE4", 0x05), ("MOUSE5", 0x06),
            ("BACKSPACE", 0x08), ("TAB", 0x09), ("ENTER", 0x0D), ("SHIFT", 0x10), ("CTRL", 0x11), ("ALT", 0x12),
            ("PAUSE", 0x13), ("CAPSLOCK", 0x14), ("ESCAPE", 0x1B), ("SPACE", 0x20), ("PAGEUP", 0x21),
            ("PAGEDOWN", 0x22), ("END", 0x23), ("HOME", 0x24), ("LEFT", 0x25), ("UP", 0x26), ("RIGHT", 0x27),
            ("DOWN", 0x28), ("INSERT", 0x2D), ("DELETE", 0x2E),
            ("LSHIFT", 0xA0), ("RSHIFT", 0xA1), ("LCTRL", 0xA2), ("RCTRL", 0xA3), ("LALT", 0xA4), ("RALT", 0xA5),
        ];
        for (int i = 0; i <= 9; i++) keys.Add(($"D{i}", 0x30 + i));          // top-row digits
        for (char c = 'A'; c <= 'Z'; c++) keys.Add((c.ToString(), c));        // letters = their ASCII code
        for (int i = 0; i <= 9; i++) keys.Add(($"NUMPAD{i}", 0x60 + i));
        for (int i = 1; i <= 12; i++) keys.Add(($"F{i}", 0x6F + i));
        return keys;
    }

    /// <summary>The name of a key code ("F1"), or null if it has none.</summary>
    public static string? NameOf(int code)
    {
        foreach (var (name, value) in All)
        {
            if (value == code) return name;
        }
        return null;
    }
}
