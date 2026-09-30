namespace Apathy.Models;

/// <summary>
/// Saved values of the controls a Lua script created (keyed by control label). Stored per script in
/// <see cref="MenuSettings.ScriptOptions"/> so a config remembers each script's settings.
/// </summary>
public sealed class ScriptOptionState
{
    public Dictionary<string, bool> Checkboxes { get; set; } = new();
    public Dictionary<string, double> Sliders { get; set; } = new();
    public Dictionary<string, Keybind> Keybinds { get; set; } = new();
    public Dictionary<string, int> Combos { get; set; } = new();
    public Dictionary<string, string> Colors { get; set; } = new();
}
