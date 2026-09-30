using Apathy.Models;

namespace Apathy.Services;

/// <summary>Holds the live settings object; loading a config swaps it out and notifies the window.</summary>
public static class SettingsStore
{
    public static MenuSettings Current { get; private set; } = new();

    public static event EventHandler? Replaced;

    public static void Replace(MenuSettings settings)
    {
        Current = settings;
        Replaced?.Invoke(null, EventArgs.Empty);
    }
}
