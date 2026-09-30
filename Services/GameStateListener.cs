using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Apathy.Services;

/// <summary>The weapon in your hands.</summary>
public sealed record WeaponInfo(string Name, string Type, int Ammo, int Reserve);

/// <summary>The local player's own state and stats as reported by GSI.</summary>
public sealed record PlayerInfo
{
    public string Name { get; init; } = "";
    public string Team { get; init; } = "";
    public int Health { get; init; }
    public int Armor { get; init; }
    public bool Helmet { get; init; }
    public int Money { get; init; }
    public int EquipValue { get; init; }
    public int Flashed { get; init; }
    public int Smoked { get; init; }
    public int Burning { get; init; }
    public int Kills { get; init; }
    public int Deaths { get; init; }
    public int Assists { get; init; }
    public int Mvps { get; init; }
    public int Score { get; init; }
    public int RoundKills { get; init; }
    public int RoundHeadshots { get; init; }
    public WeaponInfo? Weapon { get; init; }
}

/// <summary>Map, mode, match phase ("warmup", "live", "gameover"...), round number and score.</summary>
public sealed record MatchInfo(string Map, string Mode, string Phase, int Round, int ScoreCt, int ScoreT);

/// <summary>A game event for scripts: kill, death, round_start, round_end, bomb_*, map.</summary>
public sealed record GameEvent(string Name, IReadOnlyDictionary<string, object?> Data);

/// <summary>
/// Receives CS2 Game State Integration updates. GSI is Valve's official API: the game reads
/// cfg\gamestate_integration_*.cfg at startup and POSTs JSON game state to the listed local URL.
/// Only data the game already shows you is requested (map, round, your own player and stats);
/// nothing is read from the game process. Changes between updates become <see cref="GameEvent"/>s.
/// </summary>
public sealed class GameStateListener : IDisposable
{
    public const int Port = 31773;
    private const string ConfigFileName = "gamestate_integration_apathy.cfg";
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(30); // heartbeat is 10s
    private static readonly byte[] OkResponse = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n");

    private readonly TcpListener _listener = new(IPAddress.Loopback, Port);
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _lock = new();
    private DateTime? _bombExplodesAt;
    private DateTime _lastUpdate = DateTime.MinValue;
    private string? _map;
    private string? _phase;
    private string? _bomb;
    private PlayerInfo? _me;
    private MatchInfo? _match;

    /// <summary>Raised on the listener's thread when something happens in the game.</summary>
    public event Action<GameEvent>? EventRaised;

    /// <summary>The map you're playing on; null in the main menu or when CS2 stopped reporting.</summary>
    public string? Map
    {
        get { lock (_lock) return DateTime.UtcNow - _lastUpdate < ConnectionTimeout ? _map : null; }
    }

    /// <summary>"freezetime", "live" or "over".</summary>
    public string? RoundPhase
    {
        get { lock (_lock) return _phase; }
    }

    /// <summary>Your own player (null until CS2 has reported it).</summary>
    public PlayerInfo? Me
    {
        get { lock (_lock) return _me; }
    }

    public MatchInfo? Match
    {
        get { lock (_lock) return _match; }
    }

    /// <summary>False when the port was already taken.</summary>
    public bool IsListening { get; private set; }

    /// <summary>True when CS2 sent something recently.</summary>
    public bool IsConnected
    {
        get { lock (_lock) return DateTime.UtcNow - _lastUpdate < ConnectionTimeout; }
    }

    /// <summary>Time until the planted bomb explodes, or null when no bomb is planted.</summary>
    public TimeSpan? BombTimeLeft
    {
        get
        {
            lock (_lock)
            {
                if (_bombExplodesAt is not { } explodesAt) return null;
                var left = explodesAt - DateTime.UtcNow;
                return left > TimeSpan.Zero ? left : TimeSpan.Zero;
            }
        }
    }

    public void Start()
    {
        try
        {
            _listener.Start();
            IsListening = true;
            _ = AcceptLoopAsync();
        }
        catch (SocketException)
        {
            IsListening = false;
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        _stop.Dispose();
    }

    /// <summary>
    /// Writes (or updates) the GSI cfg in CS2's cfg folder. CS2 only reads it at startup, so after a
    /// change a running game has to be restarted once. Ok is false when CS2 isn't found or the file
    /// can't be written; Changed is true when the file was created or updated.
    /// </summary>
    public static (bool Ok, bool Changed) InstallConfig()
    {
        if (Cs2Locator.FindGameFolder() is not { } game) return (false, false);
        try
        {
            string path = Path.Combine(game, "cfg", ConfigFileName);
            if (File.Exists(path) && File.ReadAllText(path) == ConfigText) return (true, false);
            File.WriteAllText(path, ConfigText);
            return (true, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (false, false);
        }
    }

    private static string ConfigText => $$"""
        "Apathy"
        {
            "uri"       "http://127.0.0.1:{{Port}}/"
            "timeout"   "5.0"
            "buffer"    "0.1"
            "throttle"  "0.1"
            "heartbeat" "10.0"
            "data"
            {
                "provider"           "1"
                "map"                "1"
                "round"              "1"
                "phase_countdowns"   "1"
                "player_id"          "1"
                "player_state"       "1"
                "player_weapons"     "1"
                "player_match_stats" "1"
                "map_round_wins"     "1"
            }
        }
        """;

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }
            _ = HandleClientAsync(client);
        }
    }

    // CS2 keeps the connection open and sends one POST per update.
    private async Task HandleClientAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                while (await ReadRequestBodyAsync(stream, _stop.Token) is { } body)
                {
                    Apply(body);
                    await stream.WriteAsync(OkResponse, _stop.Token);
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException
                                           or ObjectDisposedException or JsonException or FormatException)
            {
                // Connection dropped or bad data: CS2 simply reconnects on its next update.
            }
        }
    }

    private static async Task<string?> ReadRequestBodyAsync(NetworkStream stream, CancellationToken token)
    {
        // Headers end with an empty line; they're short, so read byte by byte.
        var header = new List<byte>();
        var one = new byte[1];
        while (!EndsWithBlankLine(header))
        {
            if (await stream.ReadAsync(one, token) == 0 || header.Count > 16 * 1024) return null;
            header.Add(one[0]);
        }

        var length = Regex.Match(Encoding.ASCII.GetString(header.ToArray()), @"Content-Length:\s*(\d+)", RegexOptions.IgnoreCase);
        int size = length.Success ? int.Parse(length.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        if (size > 1024 * 1024) return null;

        var body = new byte[size];
        await stream.ReadExactlyAsync(body, token);
        return Encoding.UTF8.GetString(body);
    }

    private static bool EndsWithBlankLine(List<byte> data) =>
        data.Count >= 4 && data[^4] == '\r' && data[^3] == '\n' && data[^2] == '\r' && data[^1] == '\n';

    private void Apply(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        string? map = Str(root, "map", "name");
        string? roundPhase = Str(root, "round", "phase");
        string? winner = Str(root, "round", "win_team");
        string? bomb = Str(root, "round", "bomb");
        string? countdownPhase = Str(root, "phase_countdowns", "phase");
        double? phaseEndsIn = root.TryGetProperty("phase_countdowns", out var countdown)
                              && countdown.TryGetProperty("phase_ends_in", out var endsIn)
            ? ReadNumber(endsIn)
            : null;

        MatchInfo? match = null;
        if (root.TryGetProperty("map", out var mapBlock) && map != null)
        {
            match = new MatchInfo(map, Str(mapBlock, "mode") ?? "", Str(mapBlock, "phase") ?? "", Int(mapBlock, "round"),
                Int(mapBlock.TryGetProperty("team_ct", out var ct) ? ct : default, "score"),
                Int(mapBlock.TryGetProperty("team_t", out var t) ? t : default, "score"));
        }

        // The "player" block is whoever you're watching; only use it while that's you.
        PlayerInfo? me = null;
        if (root.TryGetProperty("player", out var player) && Str(player, "steamid") is { } playerId
            && playerId == Str(root, "provider", "steamid"))
        {
            player.TryGetProperty("state", out var state);
            player.TryGetProperty("match_stats", out var stats);
            me = new PlayerInfo
            {
                Name = Str(player, "name") ?? "",
                Team = Str(player, "team") ?? "",
                Health = Int(state, "health"),
                Armor = Int(state, "armor"),
                Helmet = state.ValueKind == JsonValueKind.Object && state.TryGetProperty("helmet", out var helmet)
                         && helmet.ValueKind == JsonValueKind.True,
                Money = Int(state, "money"),
                EquipValue = Int(state, "equip_value"),
                Flashed = Int(state, "flashed"),
                Smoked = Int(state, "smoked"),
                Burning = Int(state, "burning"),
                Kills = Int(stats, "kills"),
                Deaths = Int(stats, "deaths"),
                Assists = Int(stats, "assists"),
                Mvps = Int(stats, "mvps"),
                Score = Int(stats, "score"),
                RoundKills = Int(state, "round_kills"),
                RoundHeadshots = Int(state, "round_killhs"),
                Weapon = ActiveWeapon(player),
            };
        }

        var events = new List<GameEvent>();
        lock (_lock)
        {
            _lastUpdate = DateTime.UtcNow;

            // During "bomb" the countdown is the explosion time; during "defuse" it's the defuse time,
            // so keep the explosion time from before the defuse started.
            if (countdownPhase == "bomb" && phaseEndsIn is { } secondsLeft)
                _bombExplodesAt = DateTime.UtcNow + TimeSpan.FromSeconds(secondsLeft);
            else if (countdownPhase != "defuse" && bomb != "planted")
                _bombExplodesAt = null;

            bool sameMap = map != null && map == _map;
            if (map != null && map != _map) events.Add(Event("map", ("name", map)));
            if (roundPhase != _phase)
            {
                if (roundPhase == "live") events.Add(Event("round_start"));
                else if (roundPhase == "over") events.Add(Event("round_end", ("winner", winner)));
            }
            if (bomb != _bomb && bomb is "planted" or "defused" or "exploded") events.Add(Event("bomb_" + bomb));

            // Stats reset on a new map, so only compare within one map. Watching someone else keeps
            // the last known own stats, so nothing fires until you're back.
            if (me != null && _me != null && sameMap)
            {
                if (me.Kills > _me.Kills)
                    events.Add(Event("kill", ("kills", me.Kills), ("round_kills", me.RoundKills),
                                     ("headshot", me.RoundHeadshots > _me.RoundHeadshots),
                                     ("weapon", me.Weapon?.Name)));
                if (me.Deaths > _me.Deaths) events.Add(Event("death", ("deaths", me.Deaths)));
                if (me.Health < _me.Health && _me.Health > 0)
                    events.Add(Event("damage", ("health", me.Health), ("lost", _me.Health - me.Health)));
                if (me.Flashed > 0 && _me.Flashed == 0) events.Add(Event("flashed", ("amount", me.Flashed)));
                if (me.Mvps > _me.Mvps) events.Add(Event("mvp", ("mvps", me.Mvps)));
            }
            if (me?.Weapon is { } weapon && weapon.Name != _me?.Weapon?.Name)
                events.Add(Event("weapon", ("name", weapon.Name), ("type", weapon.Type)));
            if (match is { Phase: "gameover" } && _match?.Phase != "gameover")
                events.Add(Event("match_end", ("ct", match.ScoreCt), ("t", match.ScoreT)));

            // CS2 only sends the map block while you're on a server, so no map means you're in the main menu.
            _map = map;
            _phase = roundPhase;
            _bomb = bomb;
            if (me != null) _me = me;
            _match = match;
        }

        foreach (var gameEvent in events) EventRaised?.Invoke(gameEvent);
    }

    // "weapons": { "weapon_0": { "name": "weapon_ak47", "type": "Rifle", "ammo_clip": 30, "ammo_reserve": 90, "state": "active" } }
    private static WeaponInfo? ActiveWeapon(JsonElement player)
    {
        if (!player.TryGetProperty("weapons", out var weapons) || weapons.ValueKind != JsonValueKind.Object) return null;
        foreach (var slot in weapons.EnumerateObject())
        {
            if (Str(slot.Value, "state") != "active") continue;
            string name = Str(slot.Value, "name") ?? "";
            return new WeaponInfo(name.StartsWith("weapon_", StringComparison.Ordinal) ? name["weapon_".Length..] : name,
                                  Str(slot.Value, "type") ?? "", Int(slot.Value, "ammo_clip"), Int(slot.Value, "ammo_reserve"));
        }
        return null;
    }

    private static GameEvent Event(string name, params (string Key, object? Value)[] data) =>
        new(name, data.ToDictionary(d => d.Key, d => d.Value));

    private static string? Str(JsonElement element, params string[] path)
    {
        foreach (string key in path)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(key, out element)) return null;
        }
        return element.ValueKind == JsonValueKind.String ? element.GetString() : null;
    }

    private static int Int(JsonElement element, string key) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value) && ReadNumber(value) is { } number
            ? (int)number
            : 0;

    private static double? ReadNumber(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) => number,
        _ => null,
    };
}
