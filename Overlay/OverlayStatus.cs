using Apathy.Models;

namespace Apathy.Overlay;

/// <summary>Overlay state shown inside the menu (bind with Source={x:Static o:OverlayStatus.Instance}).</summary>
public sealed class OverlayStatus : ObservableObject
{
    public static OverlayStatus Instance { get; } = new();

    /// <summary>Bomb timer connection hint; empty while the Bomb indicator isn't selected.</summary>
    public string BombTimer { get; set => SetField(ref field, value); } = "";
}
