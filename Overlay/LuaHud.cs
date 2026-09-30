using System.Collections.ObjectModel;
using Apathy.Models;

namespace Apathy.Overlay;

/// <summary>Shared list of HUD lines that scripts add under the watermark.</summary>
public static class LuaHud
{
    public static ObservableCollection<LuaHudLine> Lines { get; } = [];

    public static bool HasVisibleLines
    {
        get
        {
            foreach (var line in Lines)
            {
                if (line.Visible && !string.IsNullOrEmpty(line.Text)) return true;
            }
            return false;
        }
    }
}
