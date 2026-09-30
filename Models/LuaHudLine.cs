namespace Apathy.Models;

/// <summary>One text line a Lua script shows under the watermark via the <c>hud</c> API.</summary>
public sealed class LuaHudLine : ObservableObject
{
    public string Text { get; set => SetField(ref field, value); } = "";

    public bool Visible { get; set => SetField(ref field, value); } = true;
}
