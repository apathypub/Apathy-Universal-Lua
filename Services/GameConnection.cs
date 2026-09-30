using System.Windows.Threading;
using Apathy.Models;

namespace Apathy.Services;

public enum GameLinkState { Disconnected, Connecting, Connected }

/// <summary>
/// The island's status light: whether apathy is linked to the game through Game State Integration.
/// Red = not linked (no game script running, or CS2 not found), yellow = set up and waiting / has
/// trouble, green = connected and receiving events, so you can build scripts for it. Read-only —
/// this never reads the game's memory; it only reflects the official GSI feed.
/// </summary>
public sealed class GameConnection : ObservableObject
{
    public static GameConnection Instance { get; } = new();

    private GameConnection()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => Refresh();
        timer.Start();
        Refresh();
    }

    public GameLinkState State { get; private set => SetField(ref field, value); }
    public string Detail { get; private set => SetField(ref field, value); } = "No game linked";

    private void Refresh()
    {
        (State, Detail) = GameEvents.CurrentStatus switch
        {
            "connected" => (GameLinkState.Connected, "Linked to CS2 — receiving game events"),
            "waiting" => (GameLinkState.Connecting, "Waiting for CS2 (restart it once if it's running)"),
            "not installed" => (GameLinkState.Disconnected, "Counter-Strike 2 not found"),
            _ => (GameLinkState.Disconnected, "No game linked (load a script that uses game events)"),
        };
    }
}
