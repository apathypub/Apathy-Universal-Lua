using System.Collections.ObjectModel;

namespace Apathy.Models;

/// <summary>A button on the island, added by a Lua script (island.button).</summary>
public sealed class IslandShortcut(string label, Action invoke)
{
    public string Label { get; } = label;
    public void Invoke() => invoke();
}

public static class IslandShortcuts
{
    public static ObservableCollection<IslandShortcut> Items { get; } = [];
}
