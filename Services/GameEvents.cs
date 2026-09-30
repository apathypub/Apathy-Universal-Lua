using System.Windows;

namespace Apathy.Services;

/// <summary>
/// CS2 game events for Lua scripts (game.*), via Game State Integration. Started the first time a
/// script uses the game API: installs the GSI cfg and listens on localhost. Read-only: scripts learn
/// what happened (kills, rounds, bomb...), they can't act inside the game.
/// </summary>
public static class GameEvents
{
    public static readonly string[] Names =
    [
        "kill", "death", "damage", "flashed", "mvp", "weapon", "round_start", "round_end",
        "bomb_planted", "bomb_defused", "bomb_exploded", "map", "match_end",
    ];

    private static GameStateListener? _listener;
    private static bool _installed;

    /// <summary>Raised on the UI thread.</summary>
    public static event Action<GameEvent>? Raised;

    /// <summary>True once a script has started the game link (so a status check never triggers setup).</summary>
    public static bool IsActive => _listener != null;

    /// <summary>Status without starting anything: "off", "connected", "waiting" or "not installed".</summary>
    public static string CurrentStatus =>
        _listener == null ? "off"
        : _listener.IsConnected ? "connected"
        : _installed && _listener.IsListening ? "waiting" : "not installed";

    public static GameStateListener Listener
    {
        get
        {
            EnsureStarted();
            return _listener!;
        }
    }

    /// <summary>"connected", "waiting" (installed, CS2 not sending yet) or "not installed".</summary>
    public static string Status =>
        Listener.IsConnected ? "connected" : _installed && Listener.IsListening ? "waiting" : "not installed";

    /// <summary>Starts listening and installs the GSI cfg (once).</summary>
    public static void EnsureStarted()
    {
        if (_listener != null) return;

        _listener = new GameStateListener();
        _listener.EventRaised += e => Application.Current?.Dispatcher.BeginInvoke(() => Raised?.Invoke(e));
        _listener.Start();
        if (!_listener.IsListening)
            LuaLog.Write($"[game] port {GameStateListener.Port} is in use; CS2 game events won't arrive.");

        var (ok, changed) = GameStateListener.InstallConfig();
        _installed = ok;
        if (!ok)
            LuaLog.Write("[game] Counter-Strike 2 wasn't found; game events won't arrive.");
        else if (changed)
            NotificationService.Show("CS2 game events", "Restart Counter-Strike 2 once so it starts sending game events to apathy.");
    }
}
