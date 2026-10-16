using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Threading;
using Apathy.Models;
using Apathy.Overlay;
using MoonSharp.Interpreter;

namespace Apathy.Services;

/// <summary>
/// Runs the menu's .lua scripts in a sandboxed MoonSharp interpreter (no io.*, os.execute or raw file
/// access) and exposes a safe API: logging, the clock, the menu accent color, media keys, per-frame
/// callbacks, menu controls (ui.*), HUD text (hud.*), island buttons and notifications. Scripts can ask
/// for more through <see cref="ScriptPermissions"/>: CS2 events (game.*), chat messages (chat.*),
/// internet (http.*), their own data files (files.*), the clipboard and keyboard / mouse-click
/// automation (input.*). There is no API that reads or writes the memory of other processes.
/// </summary>
public sealed partial class LuaHost : IScriptHost
{
    // Media virtual-key codes.
    private const byte VkMediaNext = 0xB0, VkMediaPrev = 0xB1, VkMediaPlayPause = 0xB3;
    private const byte VkVolumeMute = 0xAD, VkVolumeDown = 0xAE, VkVolumeUp = 0xAF;

    private sealed class Instance
    {
        public required string Name;
        public required Script Script;
        public readonly List<DynValue> TickCallbacks = [];
        public readonly List<DynValue> UnloadCallbacks = [];
        public LuaScriptPanel? Panel;
        public readonly List<LuaHudLine> HudLines = [];
        public readonly List<IslandShortcut> Shortcuts = [];
        public readonly List<(string Event, DynValue Callback)> GameHandlers = [];
        public IReadOnlySet<string> Permissions = new HashSet<string>();
        public readonly HashSet<string> UsedKeys = new(StringComparer.Ordinal);
        public readonly List<LuaTimer> Timers = [];
        public readonly Dictionary<int, bool> KeyStates = []; // key_pressed: state at the previous call
        public readonly ConsoleTail ConsoleLog = new();
        public readonly List<LuaTask> Tasks = [];
        public readonly List<EventHandler> EventHandlers = [];
        public System.Text.Json.Nodes.JsonObject? Storage; // storage.*, read on first use
        public bool StorageDirty;
        public bool InTask; // true while a spawn() task runs, so wait() can yield
    }

    // game.console_lines(): where this script left off in CS2's console.log.
    private sealed class ConsoleTail
    {
        public string? Path;
        public DateTime PathCheckedAt = DateTime.MinValue;
        public long Position = -1; // -1 = not opened yet: start at the end, old sessions are history
        public readonly System.Text.Decoder Decoder = new System.Text.UTF8Encoding(false).GetDecoder();
        public readonly System.Text.StringBuilder Partial = new();
    }

    private const int MaxConsoleReadBytes = 1024 * 1024;

    /// <summary>Complete lines added to console.log since the last call; null when there is no console.log.</summary>
    private static List<string>? ReadConsoleLines(ConsoleTail tail)
    {
        if (tail.Path == null && DateTime.UtcNow - tail.PathCheckedAt > TimeSpan.FromSeconds(5))
        {
            tail.PathCheckedAt = DateTime.UtcNow;
            if (Cs2Locator.FindGameFolder() is { } folder) tail.Path = Path.Combine(folder, "console.log");
        }
        if (tail.Path == null) return null;

        FileStream stream;
        try
        {
            // CS2 keeps the file open for writing while it runs.
            stream = new FileStream(tail.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        using (stream)
        {
            long length = stream.Length;
            if (tail.Position < 0 || length < tail.Position)
            {
                // First look: skip what's already there. Shorter than before: a new session cleared it.
                tail.Position = tail.Position < 0 ? length : 0;
                tail.Decoder.Reset();
                tail.Partial.Clear();
            }

            var lines = new List<string>();
            if (length == tail.Position) return lines;

            stream.Position = tail.Position;
            var bytes = new byte[Math.Min(length - tail.Position, MaxConsoleReadBytes)];
            int read = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
            tail.Position += read;

            var chars = new char[tail.Decoder.GetCharCount(bytes, 0, read)];
            tail.Decoder.GetChars(bytes, 0, read, chars, 0);
            foreach (char c in chars)
            {
                if (c != '\n')
                {
                    tail.Partial.Append(c);
                    continue;
                }
                lines.Add(tail.Partial.ToString().TrimEnd('\r'));
                tail.Partial.Clear();
            }
            return lines;
        }
    }

    // A timer.after / timer.every callback; Interval is null for one-shot timers.
    private sealed class LuaTimer(DynValue callback, double due, double? interval)
    {
        public readonly DynValue Callback = callback;
        public double Due = due;
        public readonly double? Interval = interval;
        public bool Cancelled;
    }

    // Monotonic clock for timers and time.ms().
    private static readonly Stopwatch Uptime = Stopwatch.StartNew();

    private readonly Dictionary<string, Instance> _running = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _timer;

    /// <summary>Per-script control panels shown in the General tab (bound by the UI).</summary>
    public ObservableCollection<LuaScriptPanel> Panels { get; } = [];

    static LuaHost()
    {
        UserData.RegisterType<CheckboxHandle>();
        UserData.RegisterType<SliderHandle>();
        UserData.RegisterType<LabelHandle>();
        UserData.RegisterType<KeybindHandle>();
        UserData.RegisterType<HudHandle>();
        UserData.RegisterType<ComboHandle>();
        UserData.RegisterType<ColorHandle>();
        UserData.RegisterType<TimerHandle>();
    }

    public LuaHost()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(50) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        GameEvents.Raised += OnGameEvent;
    }

    // Calls every game.on(name, fn) handler of the running scripts with the event's data as a table.
    private void OnGameEvent(GameEvent gameEvent)
    {
        foreach (var instance in _running.Values.ToArray())
        foreach (var (name, callback) in instance.GameHandlers.ToArray())
        {
            if (name != gameEvent.Name || !IsCurrent(instance)) continue;
            var data = new Table(instance.Script);
            foreach (var (key, value) in gameEvent.Data) data[key] = DynValue.FromObject(instance.Script, value);
            if (!SafeCall(instance, callback, DynValue.NewTable(data))) break; // script errored and was stopped
        }
    }

    public bool IsRunning(string name) => _running.ContainsKey(name);

    public IEnumerable<string> RunningNames => _running.Keys;

    /// <summary>
    /// Loads and executes a script's top level; returns whether it's running afterwards. A script is
    /// never run twice at once: if it's already loaded, this is a no-op unless <paramref name="reload"/>
    /// is set (the editor's Run, to pick up edits).
    /// </summary>
    public bool Run(string name, string path, bool reload = false)
    {
        if (_running.ContainsKey(name) && !reload)
        {
            LuaLog.Write($"[{name}] already loaded");
            return true;
        }
        Stop(name);

        string source;
        try
        {
            source = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LuaLog.Write($"[{name}] cannot read file: {ex.Message}");
            NotificationService.Show($"{name} failed to load", ex.Message, NotificationKind.Error);
            return false;
        }

        // Extra abilities come only from the "-- @permissions" line, and only once the user agreed.
        // (Read from the original file, so Enma's "// @permissions" works too.)
        var requested = ScriptPermissions.Parse(source, warning => LuaLog.Write($"[{name}] {warning}"));

        // Enma compiles to Lua with the same line numbers, so Lua errors still point at the .enma line.
        if (string.Equals(Path.GetExtension(path), ".enma", StringComparison.OrdinalIgnoreCase))
        {
            var compiled = Enma.EnmaCompiler.Compile(source);
            foreach (var warning in compiled.Warnings) LuaLog.Write($"[{name}] warning {warning.Line}:{warning.Column}: {warning.Message}");
            if (compiled.Lua is not { } lua)
            {
                var first = compiled.Errors.First();
                foreach (var error in compiled.Errors) LuaLog.Write($"[{name}] error: {error.Line}:{error.Column}: {error.Message}");
                NotificationService.Show($"{name} failed to compile", $"line {first.Line}: {first.Message}", NotificationKind.Error);
                return false;
            }
            source = lua;
        }
        if (!ScriptPermissions.Approve(name, requested))
        {
            LuaLog.Write($"[{name}] not started: permissions not granted ({string.Join(", ", requested)})");
            return false;
        }

        var script = new Script(CoreModules.Preset_SoftSandbox);
        var instance = new Instance { Name = name, Script = script, Permissions = requested };
        RegisterApi(instance);

        try
        {
            // Run the top level under the watchdog too, so an endless loop there can't freeze the app.
            var chunk = script.LoadString(source, codeFriendlyName: name);
            if (!RunWatched(script, chunk, []))
            {
                LuaLog.Write($"[{name}] error: the script's top level ran too long — check for an endless loop.");
                NotificationService.Show($"{name} failed to load", "It ran too long — check for an endless loop.", NotificationKind.Error);
                return false;
            }
            _running[name] = instance;
            LuaLog.Write($"[{name}] loaded");
            return true;
        }
        catch (InterpreterException ex)
        {
            LuaLog.Write($"[{name}] error: {ex.DecoratedMessage}");
            NotificationService.Show($"{name} failed to load", ex.DecoratedMessage, NotificationKind.Error);
            return false;
        }
    }

    // Scripts run on the UI thread, so a runaway loop would freeze apathy. Every callback (and the
    // top-level chunk) runs as a coroutine that auto-yields every few thousand instructions; if it
    // hasn't finished within the time budget, it's abandoned and the script is stopped.
    private const int WatchdogInstructions = 20_000;
    private static readonly TimeSpan WatchdogBudget = TimeSpan.FromMilliseconds(250);

    /// <summary>Runs <paramref name="fn"/> with a time budget; false if it ran too long.</summary>
    private static bool RunWatched(Script script, DynValue fn, DynValue[] args)
    {
        var coroutine = script.CreateCoroutine(fn).Coroutine;
        coroutine.AutoYieldCounter = WatchdogInstructions;
        var stopwatch = Stopwatch.StartNew();
        coroutine.Resume(args);
        // ForceSuspended = hit the auto-yield (still working); Suspended = the script yielded on purpose.
        while (coroutine.State == CoroutineState.ForceSuspended)
        {
            if (stopwatch.Elapsed > WatchdogBudget) return false;
            coroutine.Resume();
        }
        return true;
    }

    public void Stop(string name)
    {
        if (!_running.Remove(name, out var instance)) return;

        foreach (var callback in instance.UnloadCallbacks) SafeCall(instance, callback);
        foreach (var timer in instance.Timers) timer.Cancelled = true;
        foreach (var task in instance.Tasks) task.Cancelled = true;
        instance.EventHandlers.Clear();
        SaveStorage(instance);

        // Remove the script's controls and HUD lines.
        if (instance.Panel != null) Panels.Remove(instance.Panel);
        foreach (var line in instance.HudLines) LuaHud.Lines.Remove(line);
        foreach (var shortcut in instance.Shortcuts) IslandShortcuts.Items.Remove(shortcut);

        LuaLog.Write($"[{name}] unloaded");
    }

    private void Tick()
    {
        // Refresh script keybinds first so callbacks see this tick's state.
        KeyPresses.EnsureStarted();
        bool capturing = Controls.SkKeybind.IsAnyCapturing;
        foreach (var instance in _running.Values)
        {
            if (instance.Panel is null) continue;
            foreach (var control in instance.Panel.Controls)
            {
                if (control is LuaKeybindControl keybind) UpdateKeybind(keybind, capturing);
            }
        }

        // Snapshot: a tick callback may stop its own script.
        foreach (var instance in _running.Values.ToArray())
        foreach (var callback in instance.TickCallbacks.ToArray())
        {
            if (!SafeCall(instance, callback)) break; // script errored and was stopped
        }

        // Due timers (checked every tick, so they're accurate to ~50 ms).
        double now = Uptime.Elapsed.TotalSeconds;
        foreach (var instance in _running.Values.ToArray())
        foreach (var timer in instance.Timers.ToArray())
        {
            if (!timer.Cancelled && timer.Due > now) continue;
            if (timer.Cancelled || timer.Interval is not { } interval)
            {
                instance.Timers.Remove(timer);
                if (timer.Cancelled) continue;
                timer.Cancelled = true; // one-shot: done after this call
            }
            else
            {
                // Don't try to catch up after a stall; just keep the rhythm from now on.
                timer.Due += interval;
                if (timer.Due < now) timer.Due = now + interval;
            }
            if (!SafeCall(instance, timer.Callback)) break; // script errored and was stopped
        }

        RunTasks();
        DeliverEvents();
        foreach (var instance in _running.Values) SaveStorage(instance);
    }

    private static void UpdateKeybind(LuaKeybindControl control, bool capturing)
    {
        if (capturing)
        {
            // Don't let the key being assigned register as input.
            control.WasDown = null;
            control.PressCount = null;
            control.IsDown = false;
            control.JustPressed = false;
            control.Keybind.Update(false, false);
            return;
        }

        int vk = control.Keybind.Key;
        bool down = vk != 0 && Native.IsKeyDown(vk);
        // A tap that started and ended since the last tick never shows as "down"; the press count still moved.
        int presses = KeyPresses.Count(vk);
        bool tapped = control.PressCount is { } before && presses != before;
        bool pressed = (down && !(control.WasDown ?? down)) || tapped;
        control.PressCount = presses;
        control.WasDown = down;
        control.IsDown = down;
        control.JustPressed = pressed;
        control.Keybind.Update(down, pressed);
    }

    private bool SafeCall(Instance instance, DynValue callback, params DynValue[] args)
    {
        try
        {
            if (RunWatched(instance.Script, callback, args)) return true;

            LuaLog.Write($"[{instance.Name}] stopped: a callback ran too long — check for an endless loop.");
            NotificationService.Show($"{instance.Name} stopped", "A callback ran too long — check for an endless loop.",
                NotificationKind.Error);
            Stop(instance.Name);
            return false;
        }
        catch (InterpreterException ex)
        {
            Fail(instance, ex);
            return false;
        }
    }

    // A script error stops the script and removes what it added (controls, HUD lines, island buttons).
    private void Fail(Instance instance, InterpreterException ex)
    {
        LuaLog.Write($"[{instance.Name}] error: {ex.DecoratedMessage}");
        NotificationService.Show($"{instance.Name} stopped", ex.DecoratedMessage, NotificationKind.Error);
        if (!IsCurrent(instance)) return;
        instance.UnloadCallbacks.Clear(); // don't run more of a script that just failed
        Stop(instance.Name);
    }

    private bool IsCurrent(Instance instance) =>
        _running.TryGetValue(instance.Name, out var running) && running == instance;

    private void RegisterApi(Instance instance)
    {
        var g = instance.Script.Globals;

        // Logging (print + log both go to the Lua log).
        g["log"] = (Action<string>)(msg => LuaLog.Write($"[{instance.Name}] {msg}"));
        instance.Script.Options.DebugPrint = msg => LuaLog.Write($"[{instance.Name}] {msg}");

        // Time.
        g["clock"] = (Func<string>)(() => DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture));

        // Callbacks.
        g["on_tick"] = (Action<DynValue>)(fn => { if (fn.Type == DataType.Function) instance.TickCallbacks.Add(fn); });
        g["on_unload"] = (Action<DynValue>)(fn => { if (fn.Type == DataType.Function) instance.UnloadCallbacks.Add(fn); });

        // Keyboard state (Win32 virtual-key code). Read-only: nothing is pressed.
        g["key_down"] = (Func<int, bool>)(vk => Native.IsKeyDown(vk));
        g["key_pressed"] = (Func<int, bool>)(vk =>
        {
            // True once per press: down now, up at this script's previous key_pressed(vk) call.
            bool down = Native.IsKeyDown(vk);
            bool wasDown = instance.KeyStates.TryGetValue(vk, out var previous) ? previous : down;
            instance.KeyStates[vk] = down;
            return down && !wasDown;
        });
        g["key_toggled"] = (Func<int, bool>)(vk => Native.IsKeyToggled(vk));

        // Menu accent color.
        var menu = new Table(instance.Script);
        menu["set_accent"] = (Action<string>)(hex =>
        {
            if (TryParseColor(hex, out var color)) SettingsStore.Current.MenuColor = color;
        });
        menu["get_accent"] = (Func<string>)(() =>
        {
            var c = SettingsStore.Current.MenuColor;
            return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        });
        menu["user"] = (Func<string>)(() => Session.Username);
        // Unix time when the user's premium ends; nil for lifetime premium.
        menu["premium_until"] = (Func<double?>)(() => Session.PremiumUntil?.ToUnixTimeSeconds());
        menu["scripts"] = DynValue.NewCallback((_, _) =>
        {
            var list = new Table(instance.Script);
            foreach (string name in _running.Keys.Order(StringComparer.OrdinalIgnoreCase)) list.Append(DynValue.NewString(name));
            return DynValue.NewTable(list);
        });
        menu["is_loaded"] = (Func<string, bool>)(name => name != null && _running.ContainsKey(name));
        g["menu"] = menu;

        // Media control (works with Spotify and any player that honors media keys).
        var media = new Table(instance.Script);
        media["play_pause"] = (Action)(() => Native.TapKey(VkMediaPlayPause));
        media["next"] = (Action)(() => Native.TapKey(VkMediaNext));
        media["previous"] = (Action)(() => Native.TapKey(VkMediaPrev));
        media["volume_up"] = (Action)(() => Native.TapKey(VkVolumeUp));
        media["volume_down"] = (Action)(() => Native.TapKey(VkVolumeDown));
        media["mute"] = (Action)(() => Native.TapKey(VkVolumeMute));
        media["now_playing"] = (Func<string?>)SpotifyInfo.NowPlaying; // read-only, from the window title
        media["is_playing"] = (Func<bool>)(() => SpotifyInfo.NowPlaying() != null);
        g["media"] = media;
        g["spotify"] = media; // friendly alias

        // Toasts: notify(title, text) or notify(text); confirm(question, function(accepted) ... end).
        g["notify"] = DynValue.NewCallback((_, a) =>
        {
            if (a.Count >= 2) NotificationService.Show(Text(a, 0, instance.Name), Text(a, 1, ""));
            else NotificationService.Show(instance.Name, Text(a, 0, ""));
            return DynValue.Nil;
        });
        g["confirm"] = DynValue.NewCallback((_, a) =>
        {
            var fn = a.Count > 1 ? a[1] : DynValue.Nil;
            NotificationService.Ask(instance.Name, Text(a, 0, "Continue?"), accepted =>
            {
                // Skip the callback if the script was unloaded while the question was open.
                if (fn.Type == DataType.Function && IsCurrent(instance)) SafeCall(instance, fn, DynValue.NewBoolean(accepted));
            });
            return DynValue.Nil;
        });

        // Platform connection status: "connected", "unstable" or "disconnected".
        var platform = new Table(instance.Script);
        platform["status"] = (Func<string>)(() => ConnectionService.Instance.State.ToString().ToLowerInvariant());
        platform["detail"] = (Func<string>)(() => ConnectionService.Instance.Detail);
        g["platform"] = platform;

        // Read-only facts about the PC: screen size, mouse position, idle time, uptime.
        var system = new Table(instance.Script);
        system["screen"] = DynValue.NewCallback((_, _) =>
        {
            var (width, height) = Native.ScreenSize();
            return DynValue.NewTuple(DynValue.NewNumber(width), DynValue.NewNumber(height));
        });
        system["cursor"] = DynValue.NewCallback((_, _) =>
        {
            var (x, y) = Native.CursorPosition();
            return DynValue.NewTuple(DynValue.NewNumber(x), DynValue.NewNumber(y));
        });
        system["idle"] = (Func<double>)(() => Math.Round(Native.IdleTime().TotalSeconds, 1));
        system["uptime"] = (Func<double>)(() => Math.Floor(Environment.TickCount64 / 1000.0));
        g["system"] = system;

        // The running script's own name.
        var script = new Table(instance.Script) { ["name"] = instance.Name };
        g["script"] = script;

        // Named key codes: key_down(keys.F1), ui.keybind("Toggle", keys.INSERT), keys.name(vk).
        var keys = new Table(instance.Script);
        foreach (var (name, code) in ScriptKeys.All) keys[name] = code;
        keys["name"] = (Func<int, string?>)ScriptKeys.NameOf;
        g["keys"] = keys;

        // json.encode / json.decode as friendlier names for MoonSharp's json.serialize / json.parse.
        if (g.Get("json") is { Type: DataType.Table } json)
        {
            json.Table["encode"] = json.Table.Get("serialize");
            json.Table["decode"] = json.Table.Get("parse");
        }

        RegisterTime(instance, g);
        RegisterTimer(instance, g);
        RegisterColor(instance, g);
        RegisterHelpers(g);
        RegisterUi(instance, g);
        RegisterHud(instance, g);
        RegisterIsland(instance, g);
        RegisterGame(instance, g);
        RegisterChat(instance, g);
        RegisterHttp(instance, g);
        RegisterFiles(instance, g);
        RegisterClipboard(instance, g);
        RegisterInput(instance, g);
        RegisterExtras(instance, g);
    }

    // time.ms() for animations, time.now() for timestamps, time.format(pattern) for display.
    private static void RegisterTime(Instance instance, Table g)
    {
        var time = new Table(instance.Script);
        time["ms"] = (Func<double>)(() => Math.Floor(Uptime.Elapsed.TotalMilliseconds));
        time["now"] = (Func<double>)(() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0);
        time["format"] = DynValue.NewCallback((_, a) =>
        {
            string pattern = Text(a, 0, "HH:mm:ss");
            try
            {
                return DynValue.NewString(DateTime.Now.ToString(pattern, CultureInfo.InvariantCulture));
            }
            catch (FormatException)
            {
                throw new ScriptRuntimeException($"time.format: bad pattern '{pattern}' (try \"HH:mm\" or \"dd.MM.yyyy\")");
            }
        });
        g["time"] = time;
    }

    // timer.after(seconds, fn) / timer.every(seconds, fn) -> handle with :cancel() and :active().
    private void RegisterTimer(Instance instance, Table g)
    {
        var timer = new Table(instance.Script);
        DynValue Schedule(CallbackArguments a, string name, bool repeat)
        {
            double seconds = Math.Max(Number(a, 0, 0), repeat ? 0.05 : 0); // every() can't beat the 50 ms tick
            var fn = a.Count > 1 ? a[1] : DynValue.Nil;
            if (fn.Type != DataType.Function)
                throw new ScriptRuntimeException($"timer.{name}(seconds, fn): the second argument must be a function");

            var entry = new LuaTimer(fn, Uptime.Elapsed.TotalSeconds + seconds, repeat ? seconds : null);
            instance.Timers.Add(entry);
            return UserData.Create(new TimerHandle(() => entry.Cancelled = true,
                                                   () => !entry.Cancelled && IsCurrent(instance)));
        }
        timer["after"] = DynValue.NewCallback((_, a) => Schedule(a, "after", repeat: false));
        timer["every"] = DynValue.NewCallback((_, a) => Schedule(a, "every", repeat: true));
        g["timer"] = timer;
    }

    // color.rgb / color.hsv / color.lerp build "#RRGGBB" strings; color.parse splits one into r, g, b.
    private static void RegisterColor(Instance instance, Table g)
    {
        var color = new Table(instance.Script);
        color["rgb"] = DynValue.NewCallback((_, a) =>
            DynValue.NewString(Hex(Number(a, 0, 0), Number(a, 1, 0), Number(a, 2, 0))));
        color["hsv"] = DynValue.NewCallback((_, a) =>
        {
            var (r, gr, b) = HsvToRgb(Number(a, 0, 0), Number(a, 1, 1), Number(a, 2, 1));
            return DynValue.NewString(Hex(r, gr, b));
        });
        color["parse"] = DynValue.NewCallback((_, a) =>
            TryParseColor(Text(a, 0, ""), out var c)
                ? DynValue.NewTuple(DynValue.NewNumber(c.R), DynValue.NewNumber(c.G), DynValue.NewNumber(c.B))
                : DynValue.Nil);
        color["lerp"] = DynValue.NewCallback((_, a) =>
        {
            if (!TryParseColor(Text(a, 0, ""), out var from) || !TryParseColor(Text(a, 1, ""), out var to))
                return DynValue.Nil;
            double t = Math.Clamp(Number(a, 2, 0.5), 0, 1);
            return DynValue.NewString(Hex(from.R + (to.R - from.R) * t, from.G + (to.G - from.G) * t,
                                          from.B + (to.B - from.B) * t));
        });
        g["color"] = color;
    }

    // Small additions to the standard string and math libraries (so ("a,b"):split(",") works too).
    // Small capitals have no Unicode decomposition to plain letters, unlike 𝕒 / 𝓪 / ｆｕｌｌ.
    private static readonly Dictionary<char, char> SmallCaps =
        "ᴀʙᴄᴅᴇꜰɢʜɪᴊᴋʟᴍɴᴏᴘǫʀꜱᴛᴜᴠᴡʏᴢғ".Zip("abcdefghijklmnopqrstuvwyzf").ToDictionary(p => p.First, p => p.Second);

    private static string PlainLetters(string text)
    {
        var builder = new System.Text.StringBuilder(text.Length);
        foreach (char c in text.Normalize(System.Text.NormalizationForm.FormKC))
            builder.Append(SmallCaps.TryGetValue(c, out char plain) ? plain : c);
        return builder.ToString();
    }

    private static void RegisterHelpers(Table g)
    {
        if (g.Get("string") is { Type: DataType.Table } str)
        {
            var s = str.Table;
            s["split"] = DynValue.NewCallback((c, a) =>
            {
                string text = Arg(a, 0, "split");
                string separator = a.Count > 1 ? a[1].CastToString() ?? "," : ",";
                var parts = new Table(c.GetScript());
                // Plain-text separator (not a Lua pattern); "" splits into single characters.
                foreach (string part in separator.Length == 0 ? text.Select(ch => ch.ToString()) : text.Split(separator))
                    parts.Append(DynValue.NewString(part));
                return DynValue.NewTable(parts);
            });
            s["trim"] = DynValue.NewCallback((_, a) => DynValue.NewString(Arg(a, 0, "trim").Trim()));
            s["starts_with"] = DynValue.NewCallback((_, a) =>
                DynValue.NewBoolean(Arg(a, 0, "starts_with").StartsWith(Arg(a, 1, "starts_with"), StringComparison.Ordinal)));
            s["ends_with"] = DynValue.NewCallback((_, a) =>
                DynValue.NewBoolean(Arg(a, 0, "ends_with").EndsWith(Arg(a, 1, "ends_with"), StringComparison.Ordinal)));
            s["contains"] = DynValue.NewCallback((_, a) =>
                DynValue.NewBoolean(Arg(a, 0, "contains").Contains(Arg(a, 1, "contains"), StringComparison.Ordinal)));
            s["plain"] = DynValue.NewCallback((_, a) => DynValue.NewString(PlainLetters(Arg(a, 0, "plain"))));
        }

        if (g.Get("math") is { Type: DataType.Table } math)
        {
            var m = math.Table;
            m["clamp"] = (Func<double, double, double, double>)((x, min, max) => Math.Min(Math.Max(x, min), max));
            m["lerp"] = (Func<double, double, double, double>)((from, to, t) => from + (to - from) * t);
            m["round"] = DynValue.NewCallback((_, a) =>
                DynValue.NewNumber(Math.Round(Number(a, 0, 0), Math.Clamp((int)Number(a, 1, 0), 0, 15),
                                              MidpointRounding.AwayFromZero)));
        }

        static string Arg(CallbackArguments a, int index, string function) =>
            (index < a.Count ? a[index].CastToString() : null)
            ?? throw new ScriptRuntimeException($"string.{function}: argument {index + 1} must be a string");
    }

    private static string Hex(double r, double g, double b)
    {
        static int Byte(double v) => (int)Math.Round(Math.Clamp(v, 0, 255));
        return $"#{Byte(r):X2}{Byte(g):X2}{Byte(b):X2}";
    }

    /// <summary>Hue in degrees, saturation and value 0..1 -> r, g, b 0..255.</summary>
    private static (double R, double G, double B) HsvToRgb(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        s = Math.Clamp(s, 0, 1);
        v = Math.Clamp(v, 0, 1);
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return ((r + m) * 255, (g + m) * 255, (b + m) * 255);
    }

    /// <summary>Stops the call with a Lua error unless the script declared (and was granted) the permission.</summary>
    private static void Require(Instance instance, string permission)
    {
        if (!instance.Permissions.Contains(permission))
            throw new ScriptRuntimeException(
                $"this needs the '{permission}' permission: put  -- @permissions {permission}  on the first line of the script");
    }

    // Read-only CS2 game events and your own stats, via Game State Integration (see GameEvents).
    private static readonly Cs2Window Cs2GameWindow = new();

    // game.read_text: only pixels inside the CS2 window are read, and only while it's the active window, so a
    // script can't read anything else on the screen.
    private async void ReadText(Instance instance, System.Windows.Int32Rect area, DynValue callback)
    {
        List<ScreenText.Line>? lines = null;
        string? error = null;
        try
        {
            if (Cs2GameWindow.ClientBounds() is not { } window || !Cs2GameWindow.IsForeground)
            {
                error = "CS2 isn't the active window";
            }
            else
            {
                int left = Math.Max(area.X, window.X), top = Math.Max(area.Y, window.Y);
                int right = Math.Min(area.X + area.Width, window.X + window.Width);
                int bottom = Math.Min(area.Y + area.Height, window.Y + window.Height);
                if (right - left < 8 || bottom - top < 8)
                    error = "that area isn't inside the CS2 window";
                else if ((lines = await ScreenText.ReadAsync(new System.Windows.Int32Rect(left, top, right - left, bottom - top))) == null)
                    error = "Windows has no OCR language: Settings > Time & language > Language & region > add English";
            }
        }
        catch (Exception ex) // async void: anything escaping here would take down the whole app
        {
            error = $"reading the screen failed: {ex.Message}";
        }

        if (callback.Type != DataType.Function || !IsCurrent(instance)) return;
        DynValue texts = DynValue.Nil, boxes = DynValue.Nil;
        if (lines != null)
        {
            // fn(lines, err, boxes): lines are the plain texts; boxes[i] = { text, x, y, w, h } in screen pixels.
            var textTable = new Table(instance.Script);
            var boxTable = new Table(instance.Script);
            foreach (var line in lines)
            {
                textTable.Append(DynValue.NewString(line.Text));
                var box = new Table(instance.Script)
                {
                    ["text"] = line.Text, ["x"] = line.Box.X, ["y"] = line.Box.Y, ["w"] = line.Box.Width, ["h"] = line.Box.Height,
                };
                boxTable.Append(DynValue.NewTable(box));
            }
            texts = DynValue.NewTable(textTable);
            boxes = DynValue.NewTable(boxTable);
        }
        SafeCall(instance, callback, texts, error == null ? DynValue.Nil : DynValue.NewString(error), boxes);
    }

    private void RegisterGame(Instance instance, Table g)
    {
        var game = new Table(instance.Script);
        game["on"] = DynValue.NewCallback((_, a) =>
        {
            Require(instance, "game");
            string name = Text(a, 0, "");
            var fn = a.Count > 1 ? a[1] : DynValue.Nil;
            if (!GameEvents.Names.Contains(name))
                LuaLog.Write($"[{instance.Name}] game.on: unknown event '{name}' (use {string.Join(", ", GameEvents.Names)})");
            else if (fn.Type == DataType.Function)
                instance.GameHandlers.Add((name, fn));
            GameEvents.EnsureStarted();
            return DynValue.Nil;
        });
        game["status"] = (Func<string>)(() =>
        {
            Require(instance, "game");
            return GameEvents.Status;
        });
        game["map"] = (Func<string?>)(() =>
        {
            Require(instance, "game");
            return GameEvents.Listener.Map;
        });
        game["round_phase"] = (Func<string?>)(() =>
        {
            Require(instance, "game");
            return GameEvents.Listener.RoundPhase;
        });
        game["me"] = DynValue.NewCallback((_, _) =>
        {
            Require(instance, "game");
            if (GameEvents.Listener.Me is not { } me) return DynValue.Nil;
            var t = new Table(instance.Script)
            {
                ["name"] = me.Name, ["team"] = me.Team, ["health"] = me.Health, ["armor"] = me.Armor,
                ["helmet"] = me.Helmet, ["money"] = me.Money, ["equip_value"] = me.EquipValue,
                ["flashed"] = me.Flashed, ["smoked"] = me.Smoked, ["burning"] = me.Burning,
                ["kills"] = me.Kills, ["deaths"] = me.Deaths, ["assists"] = me.Assists, ["mvps"] = me.Mvps,
                ["score"] = me.Score, ["round_kills"] = me.RoundKills, ["round_headshots"] = me.RoundHeadshots,
            };
            return DynValue.NewTable(t);
        });
        game["weapon"] = DynValue.NewCallback((_, _) =>
        {
            Require(instance, "game");
            if (GameEvents.Listener.Me?.Weapon is not { } weapon) return DynValue.Nil;
            return DynValue.NewTable(new Table(instance.Script)
            {
                ["name"] = weapon.Name, ["type"] = weapon.Type, ["ammo"] = weapon.Ammo, ["reserve"] = weapon.Reserve,
            });
        });
        game["score"] = DynValue.NewCallback((_, _) =>
        {
            Require(instance, "game");
            if (GameEvents.Listener.Match is not { } match) return DynValue.Nil;
            return DynValue.NewTable(new Table(instance.Script)
            {
                ["ct"] = match.ScoreCt, ["t"] = match.ScoreT, ["round"] = match.Round,
                ["phase"] = match.Phase, ["mode"] = match.Mode, ["map"] = match.Map,
            });
        });
        game["connected"] = (Func<bool>)(() =>
        {
            Require(instance, "game");
            return GameEvents.Listener.IsConnected;
        });
        game["team"] = (Func<string?>)(() =>
        {
            Require(instance, "game");
            return GameEvents.Listener.Me?.Team is { Length: > 0 } team ? team : null;
        });
        game["alive"] = (Func<bool>)(() =>
        {
            Require(instance, "game");
            return GameEvents.Listener.Me?.Health > 0;
        });
        game["bomb_time"] = (Func<double?>)(() =>
        {
            Require(instance, "game");
            return GameEvents.Listener.BombTimeLeft is { } left ? Math.Round(left.TotalSeconds, 1) : null;
        });
        // These two only look at the process list / focused window; the game itself isn't touched.
        game["running"] = (Func<bool>)(() =>
        {
            Require(instance, "game");
            return Cs2ProcessId() != 0;
        });
        game["focused"] = (Func<bool>)(() =>
        {
            Require(instance, "game");
            int pid = Cs2ProcessId();
            return pid != 0 && Native.ForegroundProcessId() == pid;
        });
        // game.read_text(x, y, w, h, fn(lines, err)): OCR of a screen area (physical pixels) inside the CS2 window.
        game["read_text"] = DynValue.NewCallback((_, a) =>
        {
            Require(instance, "game");
            var area = new System.Windows.Int32Rect((int)Number(a, 0, 0), (int)Number(a, 1, 0),
                                                    Math.Max(0, (int)Number(a, 2, 0)), Math.Max(0, (int)Number(a, 3, 0)));
            ReadText(instance, area, a.Count > 4 ? a[4] : DynValue.Nil);
            return DynValue.Nil;
        });
        // Reads console.log, which CS2 writes with the launch option -condebug; the game itself isn't touched.
        game["console_lines"] = DynValue.NewCallback((_, _) =>
        {
            Require(instance, "game");
            if (ReadConsoleLines(instance.ConsoleLog) is not { } lines) return DynValue.Nil;
            var table = new Table(instance.Script);
            foreach (string line in lines) table.Append(DynValue.NewString(line));
            return DynValue.NewTable(table);
        });
        g["game"] = game;
    }

    // The CS2 process id (0 = not running), looked up at most once a second since scripts poll it every tick.
    private static int _cs2Pid;
    private static DateTime _cs2PidCheckedAt = DateTime.MinValue;

    private static int Cs2ProcessId()
    {
        if (DateTime.UtcNow - _cs2PidCheckedAt < TimeSpan.FromSeconds(1)) return _cs2Pid;
        _cs2PidCheckedAt = DateTime.UtcNow;
        var processes = Process.GetProcessesByName(Cs2Process.ProcessName);
        _cs2Pid = processes.Length > 0 ? processes[0].Id : 0;
        foreach (var process in processes) process.Dispose();
        return _cs2Pid;
    }

    // chat.say / chat.say_team: text into the CS2 chat through its console port (see CsChat).
    // chat.exec / chat.say_exec / chat.say_team_exec: write cfg\apathy_exec.cfg and tap the exec key (default F8).
    //   Requires once in the CS2 console:  bind "F8" "exec apathy_exec"
    private static int _chatExecKey = 0x77; // F8

    // No BOM: Source reads it as part of the first command ("Unknown command: ï»¿say").
    private static readonly System.Text.Encoding CfgEncoding = new System.Text.UTF8Encoding(false);

    // Commands from every script go through one queue: each key press runs everything queued so far, so two
    // scripts can't overwrite the cfg before CS2 has run it. The key is only pressed while CS2 has focus.
    private const int MaxPendingCfgCommands = 16;
    private static readonly List<string> PendingCfgCommands = [];
    private static bool _cfgWorkerRunning;

    private static void CfgExec(Instance instance, string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return;
        lock (PendingCfgCommands)
        {
            PendingCfgCommands.Add(command.TrimEnd());
            if (PendingCfgCommands.Count > MaxPendingCfgCommands) PendingCfgCommands.RemoveAt(0);
            if (_cfgWorkerRunning) return;
            _cfgWorkerRunning = true;
        }
        _ = Task.Run(() => RunCfgQueue(instance.Name));
    }

    private static async Task RunCfgQueue(string scriptName)
    {
        while (true)
        {
            int pid = Cs2ProcessId();
            if (pid == 0 || Native.ForegroundProcessId() != pid)
            {
                lock (PendingCfgCommands)
                {
                    if (pid == 0) PendingCfgCommands.Clear(); // CS2 closed: nothing will ever run them
                    if (PendingCfgCommands.Count == 0) { _cfgWorkerRunning = false; return; }
                }
                await Task.Delay(100);
                continue;
            }

            string[] batch;
            lock (PendingCfgCommands)
            {
                if (PendingCfgCommands.Count == 0) { _cfgWorkerRunning = false; return; }
                batch = [.. PendingCfgCommands];
                PendingCfgCommands.Clear();
            }

            string? gameFolder = Cs2Locator.FindGameFolder();
            if (gameFolder == null)
            {
                LuaLog.Write($"[{scriptName}] chat.exec: CS2 folder not found");
                continue;
            }
            try
            {
                File.WriteAllText(Path.Combine(gameFolder, "cfg", "apathy_exec.cfg"), string.Join("\n", batch) + "\n", CfgEncoding);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LuaLog.Write($"[{scriptName}] chat.exec: {ex.Message}");
                continue;
            }

            int key = _chatExecKey;
            Native.SendKey(key, true);
            await Task.Delay(25);
            Native.SendKey(key, false);
            await Task.Delay(150); // CS2 reads the file on its next frame; don't rewrite it before that
        }
    }

    private static void SayExec(Instance instance, string text, bool team)
    {
        string safe = CsChat.Clean(text);
        if (safe.Length == 0) return;
        CfgExec(instance, $"{(team ? "say_team" : "say")} \"{safe}\"");
    }

    private static void RegisterChat(Instance instance, Table g)
    {
        var chat = new Table(instance.Script);
        DynValue Say(CallbackArguments a, bool team)
        {
            Require(instance, "chat");
            string text = Text(a, 0, "");
            _ = SayAsync();
            return DynValue.Nil;

            async Task SayAsync()
            {
                if (await CsChat.SayAsync(text, team) is { } problem) LuaLog.Write($"[{instance.Name}] chat: {problem}");
            }
        }
        chat["say"] = DynValue.NewCallback((_, a) => Say(a, team: false));
        chat["say_team"] = DynValue.NewCallback((_, a) => Say(a, team: true));
        chat["connected"] = (Func<bool>)(() =>
        {
            Require(instance, "chat");
            return CsChat.Probe();
        });
        chat["say_exec"] = DynValue.NewCallback((_, a) =>
        {
            Require(instance, "chat");
            SayExec(instance, Text(a, 0, ""), team: false);
            return DynValue.Nil;
        });
        chat["say_team_exec"] = DynValue.NewCallback((_, a) =>
        {
            Require(instance, "chat");
            SayExec(instance, Text(a, 0, ""), team: true);
            return DynValue.Nil;
        });
        // chat.console(cmd) — send any CS2 console command via netconport TCP (no key binding needed)
        // Requires CS2 launch option:  -netconport 2121
        chat["console"] = DynValue.NewCallback((ctx, a) =>
        {
            Require(instance, "chat");
            string cmd = Text(a, 0, "");
            _ = ConsoleTask();
            return DynValue.Nil;
            async Task ConsoleTask()
            {
                if (await CsChat.ConsoleAsync(cmd) is { } err) LuaLog.Write($"[{instance.Name}] chat.console: {err}");
            }
        });
        // chat.exec("command") — write any CS2 console command to apathy_exec.cfg + tap exec key
        chat["exec"] = DynValue.NewCallback((_, a) =>
        {
            Require(instance, "chat");
            CfgExec(instance, Text(a, 0, ""));
            return DynValue.Nil;
        });
        // chat.set_exec_key(keys.F9) — change the trigger key (default F8 = 0x77)
        chat["set_exec_key"] = (Action<int>)(vk => { Require(instance, "chat"); if (vk > 0) _chatExecKey = vk; });
        chat["exec_key"] = (Func<int>)(() => { Require(instance, "chat"); return _chatExecKey; });
        g["chat"] = chat;
    }

    private static readonly System.Net.Http.HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private const int MaxHttpBytes = 1024 * 1024;

    // http.get(url, fn(body, status, err)) / http.post(url, body, fn, content_type): async, answer on the UI thread.
    private void RegisterHttp(Instance instance, Table g)
    {
        var http = new Table(instance.Script);
        http["get"] = DynValue.NewCallback((_, a) =>
        {
            Require(instance, "http");
            Send(instance, System.Net.Http.HttpMethod.Get, Text(a, 0, ""), null, "", a.Count > 1 ? a[1] : DynValue.Nil);
            return DynValue.Nil;
        });
        http["post"] = DynValue.NewCallback((_, a) =>
        {
            Require(instance, "http");
            Send(instance, System.Net.Http.HttpMethod.Post, Text(a, 0, ""), Text(a, 1, ""),
                 Text(a, 3, "application/json"), a.Count > 2 ? a[2] : DynValue.Nil);
            return DynValue.Nil;
        });
        g["http"] = http;
    }

    private async void Send(Instance instance, System.Net.Http.HttpMethod method, string url, string? body,
                            string contentType, DynValue callback, List<(string Name, string Value)>? headers = null)
    {
        Dictionary<string, string>? responseHeaders = null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            Answer(DynValue.Nil, 0, $"not an http(s) address: {url}");
            return;
        }

        try
        {
            using var request = new System.Net.Http.HttpRequestMessage(method, uri);
            if (body != null) request.Content = new System.Net.Http.StringContent(body, System.Text.Encoding.UTF8, contentType);
            foreach (var (name, value) in headers ?? [])
            {
                if (!request.Headers.TryAddWithoutValidation(name, value)) request.Content?.Headers.TryAddWithoutValidation(name, value);
            }
            using var response = await Http.SendAsync(request, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
            responseHeaders = response.Headers.Concat(response.Content.Headers)
                .ToDictionary(h => h.Key.ToLowerInvariant(), h => string.Join(", ", h.Value));
            await using var stream = await response.Content.ReadAsStreamAsync();
            var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk)) > 0)
            {
                if (buffer.Length + read > MaxHttpBytes)
                {
                    Answer(DynValue.Nil, (int)response.StatusCode, "response larger than 1 MB");
                    return;
                }
                buffer.Write(chunk, 0, read);
            }
            Answer(DynValue.NewString(System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length)),
                   (int)response.StatusCode, null);
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or IOException
                                       or InvalidOperationException or FormatException)
        {
            Answer(DynValue.Nil, 0, ex.Message);
        }

        void Answer(DynValue result, int status, string? error)
        {
            // The script may have been unloaded while the request was running.
            if (callback.Type != DataType.Function || !IsCurrent(instance)) return;
            var headerTable = new Table(instance.Script);
            foreach (var (name, value) in responseHeaders ?? []) headerTable[name] = value;
            SafeCall(instance, callback, result, DynValue.NewNumber(status),
                     error == null ? DynValue.Nil : DynValue.NewString(error), DynValue.NewTable(headerTable));
        }
    }

    // files.*: the script's own data folder (see ScriptFiles).
    private static void RegisterFiles(Instance instance, Table g)
    {
        var files = new Table(instance.Script);
        files["read"] = (Func<string, string?>)(name =>
        {
            Require(instance, "files");
            return ScriptFiles.Read(instance.Name, name);
        });
        files["write"] = (Action<string, string>)((name, text) =>
        {
            Require(instance, "files");
            ScriptFiles.Write(instance.Name, name, text ?? "", append: false);
        });
        files["append"] = (Action<string, string>)((name, text) =>
        {
            Require(instance, "files");
            ScriptFiles.Write(instance.Name, name, text ?? "", append: true);
        });
        files["exists"] = (Func<string, bool>)(name =>
        {
            Require(instance, "files");
            return ScriptFiles.Exists(instance.Name, name);
        });
        files["delete"] = (Action<string>)(name =>
        {
            Require(instance, "files");
            ScriptFiles.Delete(instance.Name, name);
        });
        files["list"] = DynValue.NewCallback((_, _) =>
        {
            Require(instance, "files");
            var list = new Table(instance.Script);
            foreach (string name in ScriptFiles.List(instance.Name)) list.Append(DynValue.NewString(name));
            return DynValue.NewTable(list);
        });
        g["files"] = files;
    }

    // input.*: keyboard and mouse simulation (requires the "input" permission).
    // Key tap with multiple args: input.key_tap(keys.CTRL, keys.C) holds CTRL and taps C.
    // Mouse buttons: 1=left, 2=right, 3=middle, 4=X1, 5=X2. Scroll: +delta = up, -delta = down.
    private static void RegisterInput(Instance instance, Table g)
    {
        var input = new Table(instance.Script);

        // ---- Keyboard ----
        input["key_down"] = (Action<int>)(vk =>
        {
            Require(instance, "input");
            if (vk != 0) Native.SendKey(vk, true);
        });
        input["key_up"] = (Action<int>)(vk =>
        {
            Require(instance, "input");
            if (vk != 0) Native.SendKey(vk, false);
        });

        // key_tap(vk) or key_tap(mod1, mod2, ..., vk): holds all but the last, taps the last, releases.
        input["key_tap"] = DynValue.NewCallback((_, a) =>
        {
            Require(instance, "input");
            var keys = new List<int>();
            for (int i = 0; i < a.Count; i++)
            {
                int k = (int)Math.Round(a[i].CastToNumber() ?? 0);
                if (k != 0) keys.Add(k);
            }
            if (keys.Count == 0) return DynValue.Nil;

            for (int i = 0; i < keys.Count - 1; i++) Native.SendKey(keys[i], true);
            Native.SendKey(keys[^1], true);
            Native.SendKey(keys[^1], false);
            for (int i = keys.Count - 2; i >= 0; i--) Native.SendKey(keys[i], false);
            return DynValue.Nil;
        });

        // ---- Cursor position (absolute, in screen pixels) ----
        input["move"] = (Action<int, int>)((x, y) =>
        {
            Require(instance, "input");
            Native.MoveMouse(x, y);
        });

        // ---- Mouse buttons ----
        input["down"] = DynValue.NewCallback((_, a) =>
        {
            Require(instance, "input");
            Native.SendMouseButton((int)Number(a, 0, 1), true);
            return DynValue.Nil;
        });
        input["up"] = DynValue.NewCallback((_, a) =>
        {
            Require(instance, "input");
            Native.SendMouseButton((int)Number(a, 0, 1), false);
            return DynValue.Nil;
        });
        input["click"] = DynValue.NewCallback((_, a) =>
        {
            Require(instance, "input");
            int btn = (int)Number(a, 0, 1);
            Native.SendMouseButton(btn, true);
            Native.SendMouseButton(btn, false);
            return DynValue.Nil;
        });

        // ---- Scroll ----
        input["scroll"] = (Action<int>)(delta =>
        {
            Require(instance, "input");
            if (delta != 0) Native.ScrollMouse(delta);
        });

        // ---- Posted straight to the CS2 window: your cursor doesn't move (only CS2 can be targeted) ----
        input["post_click"] = DynValue.NewCallback((_, a) =>
        {
            Require(instance, "input");
            Cs2GameWindow.ClientBounds(); // refreshes the window lookup
            IntPtr window = Cs2GameWindow.Handle;
            return DynValue.NewBoolean(window != IntPtr.Zero
                && Native.PostClick(window, (int)Number(a, 0, 0), (int)Number(a, 1, 0), right: (int)Number(a, 2, 1) == 2));
        });
        input["post_key"] = (Func<int, bool>)(vk =>
        {
            Require(instance, "input");
            Cs2GameWindow.ClientBounds();
            IntPtr window = Cs2GameWindow.Handle;
            return window != IntPtr.Zero && vk != 0 && Native.PostKey(window, vk);
        });

        // ---- Unicode text injection (no clipboard needed) ----
        input["type_string"] = (Action<string>)(text =>
        {
            Require(instance, "input");
            if (!string.IsNullOrEmpty(text)) Native.TypeString(text);
        });

        g["input"] = input;
    }

    private static void RegisterClipboard(Instance instance, Table g)
    {
        var clipboard = new Table(instance.Script);
        clipboard["get"] = (Func<string?>)(() =>
        {
            Require(instance, "clipboard");
            try
            {
                return System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : null;
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                return null; // clipboard busy
            }
        });
        clipboard["set"] = (Func<string, bool>)(text =>
        {
            Require(instance, "clipboard");
            try
            {
                System.Windows.Clipboard.SetText(text ?? "");
                return true;
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                return false;
            }
        });
        g["clipboard"] = clipboard;
    }

    // Buttons the script adds to the island; removed when the script unloads.
    private void RegisterIsland(Instance instance, Table g)
    {
        var island = new Table(instance.Script);
        island["button"] = DynValue.NewCallback((_, a) =>
        {
            var fn = a.Count > 1 ? a[1] : DynValue.Nil;
            var shortcut = new IslandShortcut(Text(a, 0, instance.Name), () =>
            {
                if (fn.Type == DataType.Function) SafeCall(instance, fn);
            });
            IslandShortcuts.Items.Add(shortcut);
            instance.Shortcuts.Add(shortcut);
            return DynValue.Nil;
        });
        g["island"] = island;
    }

    // Controls that appear in the script's box in the General tab.
    private void RegisterUi(Instance instance, Table g)
    {
        var ui = new Table(instance.Script);

        ui["checkbox"] = DynValue.NewCallback((_, a) =>
        {
            string label = Text(a, 0, "Option");
            string key = Key(instance, label);
            var options = OptionsFor(instance.Name);
            bool value = options.Checkboxes.TryGetValue(key, out var saved) ? saved : a.Count > 1 && a[1].CastToBool();

            var control = new LuaCheckbox(label, value);
            options.Checkboxes[key] = value;
            control.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(LuaCheckbox.Value)) OptionsFor(instance.Name).Checkboxes[key] = control.Value;
            };
            PanelFor(instance).Controls.Add(control);
            return UserData.Create(new CheckboxHandle(control));
        });

        ui["slider"] = DynValue.NewCallback((_, a) =>
        {
            string label = Text(a, 0, "Value");
            string key = Key(instance, label);
            double min = Number(a, 1, 0), max = Number(a, 2, 100);
            var options = OptionsFor(instance.Name);
            double value = options.Sliders.TryGetValue(key, out var saved) ? saved : Number(a, 3, min);

            var control = new LuaSliderControl(label, value, min, max, Text(a, 4, ""), (int)Number(a, 5, 0));
            options.Sliders[key] = value;
            control.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(LuaSliderControl.Value)) OptionsFor(instance.Name).Sliders[key] = control.Value;
            };
            PanelFor(instance).Controls.Add(control);
            return UserData.Create(new SliderHandle(control));
        });

        ui["button"] = DynValue.NewCallback((_, a) =>
        {
            var fn = a.Count > 1 ? a[1] : DynValue.Nil;
            var control = new LuaButtonControl(Text(a, 0, "Button"), () =>
            {
                if (fn.Type == DataType.Function) SafeCall(instance, fn);
            });
            PanelFor(instance).Controls.Add(control);
            return DynValue.Nil;
        });

        ui["label"] = DynValue.NewCallback((_, a) =>
        {
            var control = new LuaTextControl(Text(a, 0, ""));
            PanelFor(instance).Controls.Add(control);
            return UserData.Create(new LabelHandle(control));
        });

        ui["keybind"] = DynValue.NewCallback((_, a) =>
        {
            string label = Text(a, 0, "Key");
            string key = Key(instance, label);
            var options = OptionsFor(instance.Name);

            // Restore the saved bind, or start from the script's default key.
            var keybind = options.Keybinds.TryGetValue(key, out var saved)
                ? saved
                : new Keybind { Key = (int)Number(a, 1, 0) };
            options.Keybinds[key] = keybind; // same instance stays live, so rebinds are saved automatically

            var control = new LuaKeybindControl(label, keybind);
            PanelFor(instance).Controls.Add(control);
            return UserData.Create(new KeybindHandle(control));
        });

        // ui.combo(label, {"A", "B", "C"}, default_index) — indexes are 1-based like Lua tables.
        ui["combo"] = DynValue.NewCallback((_, a) =>
        {
            string label = Text(a, 0, "Mode");
            string key = Key(instance, label);
            var items = new List<string>();
            if (a.Count > 1 && a[1].Type == DataType.Table)
            {
                var list = a[1].Table;
                for (int i = 1; i <= list.Length; i++) items.Add(list.Get(i).CastToString() ?? "");
            }
            if (items.Count == 0) items.Add("-");

            var options = OptionsFor(instance.Name);
            int index = options.Combos.TryGetValue(key, out var saved) ? saved : (int)Number(a, 2, 1) - 1;
            index = Math.Clamp(index, 0, items.Count - 1); // the saved list may have been shorter or longer

            var control = new LuaComboControl(label, items, index);
            options.Combos[key] = index;
            control.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(LuaComboControl.Index) && control.Index >= 0)
                    OptionsFor(instance.Name).Combos[key] = control.Index;
            };
            PanelFor(instance).Controls.Add(control);
            return UserData.Create(new ComboHandle(control));
        });

        // ui.color(label, "#RRGGBB") — a swatch with a color picker.
        ui["color"] = DynValue.NewCallback((_, a) =>
        {
            string label = Text(a, 0, "Color");
            string key = Key(instance, label);
            var options = OptionsFor(instance.Name);
            if (!(options.Colors.TryGetValue(key, out var saved) && TryParseColor(saved, out var color))
                && !TryParseColor(Text(a, 1, "#FFFFFF"), out color))
                color = Colors.White;

            var control = new LuaColorControl(label, color);
            options.Colors[key] = ColorHandle.ToHex(color);
            control.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(LuaColorControl.Color))
                    OptionsFor(instance.Name).Colors[key] = ColorHandle.ToHex(control.Color);
            };
            PanelFor(instance).Controls.Add(control);
            return UserData.Create(new ColorHandle(control));
        });

        g["ui"] = ui;
    }

    // Text lines the script shows under the watermark.
    private void RegisterHud(Instance instance, Table g)
    {
        var hud = new Table(instance.Script);
        hud["label"] = DynValue.NewCallback((_, a) =>
        {
            var line = new LuaHudLine { Text = Text(a, 0, "") };
            LuaHud.Lines.Add(line);
            instance.HudLines.Add(line);
            return UserData.Create(new HudHandle(line));
        });
        g["hud"] = hud;
    }

    private LuaScriptPanel PanelFor(Instance instance)
    {
        if (instance.Panel == null)
        {
            instance.Panel = new LuaScriptPanel(instance.Name);
            Panels.Add(instance.Panel);
        }
        return instance.Panel;
    }

    /// <summary>The saved option state for a script, created on first use.</summary>
    private static ScriptOptionState OptionsFor(string script)
    {
        var all = SettingsStore.Current.ScriptOptions;
        if (!all.TryGetValue(script, out var state))
        {
            state = new ScriptOptionState();
            all[script] = state;
        }
        return state;
    }

    /// <summary>A stable key for a control (its label; deduplicated within the script).</summary>
    private static string Key(Instance instance, string label)
    {
        string key = label;
        for (int i = 2; !instance.UsedKeys.Add(key); i++) key = $"{label}#{i}";
        return key;
    }

    /// <summary>Stops every running script (used when a config is loaded/reset).</summary>
    public void StopAll()
    {
        foreach (var name in _running.Keys.ToArray()) Stop(name);
    }

    private static string Text(CallbackArguments a, int i, string fallback) =>
        i < a.Count && a[i].Type == DataType.String ? a[i].String : fallback;

    private static double Number(CallbackArguments a, int i, double fallback) =>
        i < a.Count && a[i].Type == DataType.Number ? a[i].Number : fallback;

    // Handles returned to Lua (userdata) so scripts can read/update their controls and HUD lines.
    public sealed class CheckboxHandle(LuaCheckbox c)
    {
        public bool get() => c.Value;
        public void set(bool v) => c.Value = v;
        public bool value { get => c.Value; set => c.Value = value; }
    }

    public sealed class SliderHandle(LuaSliderControl c)
    {
        public double get() => c.Value;
        public void set(double v) => c.Value = v;
        public double value { get => c.Value; set => c.Value = value; }
        public double min() => c.Min;
        public double max() => c.Max;
    }

    public sealed class LabelHandle(LuaTextControl c)
    {
        public string get() => c.Text;
        public void set(string text) => c.Text = text ?? "";
    }

    public sealed class KeybindHandle(LuaKeybindControl c)
    {
        public int key() => c.Keybind.Key;
        public string? name() => ScriptKeys.NameOf(c.Keybind.Key); // "F1", or nil when unbound / unnamed
        public bool down() => c.IsDown;          // key currently held
        public bool pressed() => c.JustPressed;  // went down this tick
        public bool active() => c.Keybind.IsActive; // respects the bind's mode
    }

    public sealed class ComboHandle(LuaComboControl c)
    {
        public int get() => c.Index + 1;
        public string text() => c.Index >= 0 && c.Index < c.Items.Count ? c.Items[c.Index] : "";

        // set(2) selects by position, set("Fast") by text.
        public void set(DynValue v)
        {
            int index = v.Type == DataType.Number
                ? (int)v.Number - 1
                : c.Items.ToList().IndexOf(v.CastToString() ?? "");
            if (index >= 0 && index < c.Items.Count) c.Index = index;
        }

        public int value { get => get(); set => set(DynValue.NewNumber(value)); }

        // The item texts as a Lua list (MoonSharp passes the calling Script in).
        public Table items(Script script)
        {
            var list = new Table(script);
            foreach (string item in c.Items) list.Append(DynValue.NewString(item));
            return list;
        }

        public int count() => c.Items.Count;
    }

    public sealed class ColorHandle(LuaColorControl c)
    {
        public string get() => ToHex(c.Color);
        public void set(string hex)
        {
            if (TryParseColor(hex ?? "", out var color)) c.Color = color;
        }
        public string value { get => get(); set => set(value); }
        public DynValue rgb() => DynValue.NewTuple(DynValue.NewNumber(c.Color.R), DynValue.NewNumber(c.Color.G),
                                                   DynValue.NewNumber(c.Color.B));

        internal static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    public sealed class TimerHandle(Action stop, Func<bool> isActive)
    {
        public void cancel() => stop();
        public bool active() => isActive();
    }

    public sealed class HudHandle(LuaHudLine line)
    {
        public string get() => line.Text;
        public void set(string text) => line.Text = text ?? "";
        public void show(bool visible) => line.Visible = visible;
        public bool visible() => line.Visible;
        public void remove()
        {
            line.Visible = false;
            LuaHud.Lines.Remove(line);
        }
    }

    private static bool TryParseColor(string hex, out Color color)
    {
        try
        {
            color = (Color)ColorConverter.ConvertFromString(hex.StartsWith('#') ? hex : "#" + hex);
            color.A = 255;
            return true;
        }
        catch (Exception ex) when (ex is FormatException or NotSupportedException)
        {
            color = default;
            return false;
        }
    }
}
