using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace Apathy.Models;

/// <summary>
/// Everything the menu edits. Pages bind straight to these properties and a config file is
/// this object serialized to JSON, so adding a setting = adding a property here + a control on a page.
/// </summary>
public sealed class MenuSettings : ObservableObject
{
    // ---- Misc ----
    public bool AutoStart { get; set => SetField(ref field, value); }
    public bool Notifications { get; set => SetField(ref field, value); }
    public double Volume { get; set => SetField(ref field, value); } = 80;

    // ---- Lua ----
    /// <summary>Names (without .lua) of the scripts marked as loaded.</summary>
    public ObservableCollection<string> ActiveScripts { get; set; } = [];
    /// <summary>Saved control values per script (script name → its controls' state).</summary>
    public Dictionary<string, ScriptOptionState> ScriptOptions { get; set; } = new();
    /// <summary>Permissions the user granted per script (script name → e.g. "game", "chat").</summary>
    public Dictionary<string, List<string>> ScriptPermissions { get; set; } = new();

    // ---- CS2 ----
    /// <summary>Port of CS2's console (launch option -netconport); used only to send chat messages.</summary>
    public int NetconPort { get; set => SetField(ref field, value); } = 2121;

    // ---- ChatLive ----
    /// <summary>WebSocket address of the ChatLive server (the ChatServer project).</summary>
    public string ChatServerUrl { get; set => SetField(ref field, value); } = "ws://localhost:5080/chat";

    // ---- Island ----
    public bool ShowIsland { get; set => SetField(ref field, value); } = true;
    /// <summary>When on, the island can't be dragged.</summary>
    public bool LockIsland { get; set => SetField(ref field, value); }
    // Which shortcut buttons the island shows.
    public bool IslandChatLive { get; set => SetField(ref field, value); } = true;
    public bool IslandScripts { get; set => SetField(ref field, value); } = true;
    public bool IslandEditor { get; set => SetField(ref field, value); } = true;
    /// <summary>Show the currently loaded scripts as chips on the island.</summary>
    public bool IslandActiveScripts { get; set => SetField(ref field, value); }
    /// <summary>Shortcuts folded away with the island's arrow button.</summary>
    public bool IslandCollapsed { get; set => SetField(ref field, value); }
    /// <summary>Last dragged position; null = default (top center of the screen).</summary>
    public double? IslandLeft { get; set; }
    public double? IslandTop { get; set; }

    // ---- Menu settings ----
    public Keybind MenuKey { get; set; } = new() { Key = 0x2D }; // VK_INSERT
    public Color MenuColor { get; set => SetField(ref field, value); } = Color.FromRgb(0x9A, 0xC5, 0x27);
    /// <summary>"apathy | user | time" box in the top-right corner of the screen.</summary>
    public bool Watermark { get; set => SetField(ref field, value); }
    /// <summary>Index into 100% / 125% / 150% / 175% / 200%.</summary>
    public int DpiScale { get; set => SetField(ref field, value); }

    [JsonIgnore]
    public double ScaleFactor => 1 + 0.25 * Math.Clamp(DpiScale, 0, 4);
}
