using Apathy.Services;

namespace Apathy.Editor;

public enum LuaSymbolKind { Keyword, Function, Module, Field, Method, Constant, Event, Variable, Snippet }

/// <summary>One completion / hover entry: a name, how to call it and what it does.</summary>
public sealed record LuaSymbol(string Name, LuaSymbolKind Kind, string Signature, string Doc, string? Insert = null)
{
    /// <summary>Text put into the document when chosen (snippets insert more than their name).</summary>
    public string InsertText => Insert ?? Name;
}

/// <summary>
/// What the editor knows about apathy's Lua: keywords, the standard library as sandboxed by MoonSharp,
/// and the apathy API from <see cref="LuaHost"/>. Keep it in sync with LuaHost when the API changes.
/// </summary>
public static class LuaApi
{
    /// <summary>Everything valid at the start of an expression (keywords, globals, snippets).</summary>
    public static List<LuaSymbol> Globals { get; } = [];

    /// <summary>Members of a global table: "ui" -> checkbox, slider...</summary>
    public static Dictionary<string, List<LuaSymbol>> Modules { get; } = new(StringComparer.Ordinal);

    /// <summary>Fields / methods of a value type returned by the API: "Checkbox" -> get, set, value.</summary>
    public static Dictionary<string, List<LuaSymbol>> Types { get; } = new(StringComparer.Ordinal);

    /// <summary>Which type a call returns: "ui.checkbox" -> "Checkbox".</summary>
    public static Dictionary<string, string> Returns { get; } = new(StringComparer.Ordinal);

    /// <summary>game.on event names, with their data fields as a type named "event:&lt;name&gt;".</summary>
    public static List<LuaSymbol> Events { get; } = [];

    static LuaApi()
    {
        // ---- Language ----
        foreach (string word in (string[])["and", "break", "do", "else", "elseif", "end", "false", "for", "function",
                     "goto", "if", "in", "local", "nil", "not", "or", "repeat", "return", "then", "true", "until", "while"])
            Globals.Add(new LuaSymbol(word, LuaSymbolKind.Keyword, word, "Lua keyword."));

        // $0 marks where the caret goes after inserting.
        Snippet("fn", "function name() ... end", "Local function.", "local function name()\n    $0\nend");
        Snippet("forp", "for k, v in pairs(t) do ... end", "Loop over every key/value of a table.",
                "for key, value in pairs(t) do\n    $0\nend");
        Snippet("fori", "for i, v in ipairs(t) do ... end", "Loop over a list in order.",
                "for i, value in ipairs(t) do\n    $0\nend");
        Snippet("tick", "on_tick(function() ... end)", "Code that runs every 50 ms.",
                "on_tick(function()\n    $0\nend)");
        Snippet("every", "timer.every(1, function() ... end)", "Code that runs every second.",
                "timer.every(1, function()\n    $0\nend)");
        Snippet("onevent", "game.on(\"kill\", function(e) ... end)", "React to a CS2 event (needs @permissions game).",
                "game.on(\"kill\", function(e)\n    $0\nend)");
        Snippet("header", "-- @name / @author / @version ...", "Script manifest header for sharing.",
                "-- @name        My script\n-- @author      you\n-- @version     1.0\n-- @description What this script does.\n");

        Global("print(...)", "Writes its arguments to the Output log.");
        Global("pairs(t)", "Iterates every key/value of a table: for k, v in pairs(t) do ... end");
        Global("ipairs(t)", "Iterates a list 1..n in order: for i, v in ipairs(t) do ... end");
        Global("next(t, key)", "The key/value after key in the table (nil at the end).");
        Global("type(v) -> string", "\"nil\", \"number\", \"string\", \"boolean\", \"table\", \"function\" or \"userdata\".");
        Global("tostring(v) -> string", "Converts any value to text.");
        Global("tonumber(v, base) -> number", "Parses a number; nil if the text isn't one.");
        Global("select(n, ...)", "Arguments from n on; select(\"#\", ...) counts them.");
        Global("error(message)", "Stops with an error (catch it with pcall).");
        Global("assert(v, message)", "Errors with message when v is false or nil; otherwise returns v.");
        Global("pcall(fn, ...) -> ok, result", "Calls fn and catches errors: ok is false when it failed.");
        Global("xpcall(fn, handler)", "Like pcall, but handler(err) formats the error.");
        Global("setmetatable(t, mt) -> t", "Sets the metatable of a table (classes, __index, operators).");
        Global("getmetatable(t)", "The metatable of a value.");
        Global("rawget(t, k)", "Reads t[k] without metamethods.");
        Global("rawset(t, k, v)", "Writes t[k] without metamethods.");
        Global("rawequal(a, b)", "Compares without metamethods.");
        Global("rawlen(t)", "Length without metamethods.");
        Global("unpack(t) -> ...", "The elements of a list as separate values.");

        Module("string", "Text functions. Also callable as methods: (\"abc\"):upper()",
            ("byte(s, i) -> number", "Character code at i."),
            ("char(...) -> string", "Text from character codes."),
            ("find(s, pattern, init, plain) -> start, end", "Finds a Lua pattern (or plain text when plain is true)."),
            ("format(fmt, ...) -> string", "printf-style: string.format(\"%d kills, %.1f KD\", k, kd)."),
            ("gmatch(s, pattern)", "Iterator over every match: for word in s:gmatch(\"%a+\") do ... end"),
            ("gsub(s, pattern, repl) -> string, count", "Replaces every match of a pattern."),
            ("len(s) -> number", "Length in bytes (same as #s)."),
            ("lower(s) -> string", "Lower-case copy."),
            ("upper(s) -> string", "Upper-case copy."),
            ("match(s, pattern) -> ...", "First match (or its captures)."),
            ("rep(s, n, sep) -> string", "s repeated n times."),
            ("reverse(s) -> string", "Reversed text."),
            ("sub(s, i, j) -> string", "Substring from i to j (negative counts from the end)."),
            ("split(s, sep) -> table", "apathy: splits on plain text sep (default \",\"): (\"a,b\"):split(\",\") -> {\"a\", \"b\"}."),
            ("trim(s) -> string", "apathy: removes spaces and newlines at both ends."),
            ("starts_with(s, prefix) -> boolean", "apathy: true if s begins with prefix."),
            ("ends_with(s, suffix) -> boolean", "apathy: true if s ends with suffix."),
            ("contains(s, text) -> boolean", "apathy: true if s contains text (plain, not a pattern)."),
            ("plain(s) -> string", "apathy: fancy Unicode letters as plain ones: (\"𝕖𝕫 ᴇᴢ ｅｚ\"):plain() -> \"ez ez ez\"."));

        Module("table", "List and table functions.",
            ("insert(t, [pos,] value)", "Appends value (or inserts it at pos)."),
            ("remove(t, pos) -> value", "Removes and returns the element at pos (default: the last)."),
            ("concat(t, sep) -> string", "Joins a list of strings: table.concat(names, \", \")."),
            ("sort(t, less)", "Sorts a list in place; less(a, b) customises the order."),
            ("unpack(t) -> ...", "The elements of a list as separate values."),
            ("pack(...) -> table", "The arguments as a list with a field n."),
            ("keys(t) -> table", "The keys of a table as a list."),
            ("values(t) -> table", "The values of a table as a list."),
            ("count(t) -> number", "How many entries a table has (also non-list keys)."),
            ("find(t, value) -> key", "The key of the first entry equal to value, or nil."),
            ("contains(t, value) -> boolean", "True if some entry equals value."),
            ("map(t, fn(v, k)) -> table", "A new table with fn applied to every value."),
            ("filter(list, fn(v)) -> table", "The list entries for which fn returns true."),
            ("reduce(list, fn(acc, v), start) -> value", "Folds a list into one value: table.reduce(t, function(a, v) return a + v end, 0)."),
            ("slice(list, from, to) -> table", "Part of a list (from and to included)."),
            ("reverse(list) -> table", "The list in reverse order."),
            ("merge(a, b, ...) -> table", "A new table with the entries of all tables (later ones win)."),
            ("copy(t) -> table", "A deep copy of a table."),
            ("is_empty(t) -> boolean", "True when the table has no entries."));

        Module("math", "Math functions and constants.",
            ("abs(x)", "Absolute value."), ("ceil(x)", "Rounds up."), ("floor(x)", "Rounds down."),
            ("max(x, ...)", "Largest argument."), ("min(x, ...)", "Smallest argument."),
            ("sqrt(x)", "Square root."), ("sin(x)", "Sine (radians)."), ("cos(x)", "Cosine (radians)."),
            ("tan(x)", "Tangent (radians)."), ("exp(x)", "e^x."), ("log(x, base)", "Logarithm."),
            ("pow(x, y)", "x^y."), ("fmod(x, y)", "Remainder of x / y."), ("modf(x) -> int, frac", "Integer and fraction parts."),
            ("random(m, n)", "Random number: () 0..1, (n) 1..n, (m, n) m..n."),
            ("randomseed(x)", "Seeds math.random."),
            ("clamp(x, min, max)", "apathy: x limited to min..max."),
            ("lerp(a, b, t)", "apathy: linear blend from a (t = 0) to b (t = 1)."),
            ("round(x, decimals)", "apathy: rounds to the nearest (half away from zero), with optional decimals."));
        Member("math", "pi", LuaSymbolKind.Constant, "math.pi", "3.14159...");
        Member("math", "huge", LuaSymbolKind.Constant, "math.huge", "Infinity.");

        Module("os", "Time functions (the rest of os is disabled in the sandbox).",
            ("time(t) -> number", "Unix time in seconds."),
            ("clock() -> number", "CPU time used, in seconds."),
            ("date(format, time) -> string", "Formats a date, e.g. os.date(\"%H:%M\")."),
            ("difftime(t2, t1) -> number", "t2 - t1 in seconds."));

        Module("coroutine", "Coroutines (functions that can pause with yield).",
            ("create(fn)", "New coroutine."), ("resume(co, ...)", "Runs it until it yields or ends."),
            ("yield(...)", "Pauses the running coroutine."), ("status(co) -> string", "\"suspended\", \"running\" or \"dead\"."),
            ("wrap(fn)", "A function that resumes the coroutine each call."), ("running()", "The running coroutine."));

        Module("bit32", "Bitwise operations on 32-bit integers.",
            ("band(...)", "Bitwise and."), ("bor(...)", "Bitwise or."), ("bxor(...)", "Bitwise xor."),
            ("bnot(x)", "Bitwise not."), ("lshift(x, n)", "Shift left."), ("rshift(x, n)", "Shift right."),
            ("btest(...)", "True if the and of the arguments isn't 0."));

        Module("json", "JSON text <-> Lua tables (great with http).",
            ("encode(value) -> string", "Table to JSON text (same as json.serialize)."),
            ("decode(text) -> table", "JSON text to a table (same as json.parse)."),
            ("serialize(value) -> string", "Table to JSON text."),
            ("parse(text) -> table", "JSON text to a table."),
            ("isnull(v) -> boolean", "True for JSON null."),
            ("null()", "The JSON null value."));

        // ---- apathy API ----
        Global("log(...)", "Writes its arguments to the Output log; tables are shown in full.");
        Global("inspect(value) -> string", "A readable dump of any value, tables included.");
        Global("require(name) -> module", "Runs scripts\\lib\\name.lua (or .enma) once and returns what it returns. \"utils.text\" -> lib\\utils\\text.lua.");
        Global("spawn(fn, ...) -> Task", "Runs fn as a task that can pause with wait(seconds) without blocking anything. Starts on the next tick.");
        Global("wait(seconds)", "Pauses the current spawn() task; other code keeps running. Only inside spawn.");
        Global("clock() -> string", "Current time as \"HH:mm:ss\".");
        Global("on_tick(fn)", "fn runs every 50 ms while the script is loaded.");
        Global("on_unload(fn)", "fn runs when the script is stopped (restore what you changed here).");
        Global("key_down(vk) -> boolean", "True while the key is held, e.g. key_down(keys.SPACE).");
        Global("key_pressed(vk) -> boolean", "True once per press (down now, up at this script's previous key_pressed call). Call it every tick.");
        Global("key_toggled(vk) -> boolean", "On/off state of a lock key: key_toggled(keys.CAPSLOCK).");
        Global("notify(title, text)", "Shows a toast. notify(text) uses the script name as the title.");
        Global("confirm(question, fn(accepted))", "Accept / Decline pop-up; fn gets true or false.");

        Module("script", "The running script.");
        Member("script", "name", LuaSymbolKind.Field, "script.name -> string", "This script's file name without .lua.");

        Module("keys", "Named key codes for key_down and ui.keybind: keys.F1, keys.SPACE, keys.A, keys.MOUSE4...",
            ("name(vk) -> string", "The name of a key code (\"F1\"), or nil."));
        foreach (var (name, code) in ScriptKeys.All)
            Member("keys", name, LuaSymbolKind.Constant, $"keys.{name} = 0x{code:X2}", $"Virtual-key code {code}.");

        Module("time", "Time helpers.",
            ("ms() -> number", "Milliseconds since apathy started; use differences for animations and cooldowns."),
            ("now() -> number", "Unix time in seconds (with fractions)."),
            ("format(pattern) -> string", "Current time as text using a .NET pattern: \"HH:mm\", \"dd.MM.yyyy\"."));

        Module("timer", "Delayed and repeating callbacks.",
            ("after(seconds, fn) -> Timer", "Calls fn once after the delay."),
            ("every(seconds, fn) -> Timer", "Calls fn repeatedly (at least 0.05 s apart) until cancelled."));
        Returns["timer.after"] = Returns["timer.every"] = "Timer";

        Module("color", "\"#RRGGBB\" color helpers.",
            ("rgb(r, g, b) -> string", "0..255 channels to \"#RRGGBB\"."),
            ("hsv(h, s, v) -> string", "Hue 0..360, saturation and value 0..1 to \"#RRGGBB\" (rainbows!)."),
            ("parse(hex) -> r, g, b", "Splits \"#RRGGBB\" into 0..255 channels; nil if it isn't a color."),
            ("lerp(a, b, t) -> string", "Blends color a into b (t 0..1)."));

        Module("menu", "The apathy menu.",
            ("set_accent(hex)", "Changes the menu accent color: menu.set_accent(\"#FF4060\")."),
            ("get_accent() -> string", "The accent color as \"#RRGGBB\"."),
            ("user() -> string", "The logged-in apathy user name."),
            ("premium_until() -> number", "Unix time when the user's premium ends; nil for lifetime premium."),
            ("scripts() -> table", "Names of the scripts running now."),
            ("is_loaded(name) -> boolean", "True if the script with that name is running."));

        Module("system", "Read-only facts about the PC.",
            ("screen() -> width, height", "Primary screen size in pixels."),
            ("cursor() -> x, y", "Mouse position on the screen in pixels."),
            ("idle() -> number", "Seconds since the last keyboard or mouse input (AFK detection)."),
            ("uptime() -> number", "Seconds since Windows started."));

        (string, string)[] media =
        [
            ("play_pause()", "Media key: play / pause."), ("next()", "Media key: next track."),
            ("previous()", "Media key: previous track."), ("volume_up()", "Media key: volume up."),
            ("volume_down()", "Media key: volume down."), ("mute()", "Media key: mute."),
            ("now_playing() -> string", "\"Artist - Title\" from Spotify's window, or nil."),
            ("is_playing() -> boolean", "True while Spotify is playing something."),
        ];
        Module("media", "Media keys (Spotify and any player that honors them).", media);
        Module("spotify", "Alias of media.", media);

        Module("ui", "Controls in this script's box in the General tab; their values are saved with the config.",
            ("checkbox(label, default) -> Checkbox", "An on/off option."),
            ("slider(label, min, max, default, suffix, decimals) -> Slider", "A number option, e.g. ui.slider(\"Speed\", 0, 10, 5, \"x\", 1)."),
            ("combo(label, items, default_index) -> Combo", "A drop-down: ui.combo(\"Mode\", {\"Slow\", \"Fast\"}, 1)."),
            ("color(label, default_hex) -> Color", "A color picker: ui.color(\"Tint\", \"#FF4060\")."),
            ("keybind(label, default_vk) -> Keybind", "A rebindable hotkey: ui.keybind(\"Toggle\", keys.INSERT)."),
            ("button(label, fn)", "A button that calls fn."),
            ("label(text) -> Label", "A line of text."));
        foreach (string control in (string[])["checkbox", "slider", "combo", "color", "keybind", "label"])
            Returns[$"ui.{control}"] = char.ToUpperInvariant(control[0]) + control[1..];

        Module("hud", "Text lines under the watermark.",
            ("label(text) -> HudLine", "Adds a line; keep the handle to update it."));
        Returns["hud.label"] = "HudLine";

        Module("island", "The floating island.",
            ("button(label, fn)", "Adds a button that calls fn (removed when the script stops)."));

        Module("platform", "apathy platform state.",
            ("status() -> string", "\"connected\", \"unstable\" or \"disconnected\"."),
            ("detail() -> string", "The connection description shown in the island tooltip."));

        const string Game = " Needs  -- @permissions game";
        Module("game", "CS2 events and your own stats (read-only, via Game State Integration)." + Game,
            ("on(event, fn(data))", "Runs fn when a CS2 event happens: game.on(\"kill\", function(e) ... end)." + Game),
            ("me() -> Me", "Your player: health, money, kills... or nil." + Game),
            ("weapon() -> Weapon", "Your active weapon: name, type, ammo, reserve; or nil." + Game),
            ("score() -> Score", "The match: ct, t, round, phase, mode, map; or nil." + Game),
            ("map() -> string", "Current map name." + Game),
            ("round_phase() -> string", "\"freezetime\", \"live\" or \"over\"." + Game),
            ("status() -> string", "\"connected\", \"waiting\" or \"not installed\"." + Game),
            ("connected() -> boolean", "True while CS2 is sending game state." + Game),
            ("team() -> string", "\"CT\" or \"T\", or nil." + Game),
            ("alive() -> boolean", "True while your health is above 0." + Game),
            ("bomb_time() -> number", "Seconds until the planted bomb explodes, or nil." + Game),
            ("running() -> boolean", "True while cs2.exe is running (process list only)." + Game),
            ("focused() -> boolean", "True while the CS2 window is the active window." + Game),
            ("console_lines() -> table", "Lines CS2 printed to its console since the last call; nil without the launch option -condebug. Call it every tick." + Game),
            ("read_text(x, y, w, h, fn(lines, err, boxes))", "Reads the text in a screen area (pixels, e.g. from system.cursor()) with Windows OCR. boxes[i] = {text, x, y, w, h}: where each line is on the screen. Only works inside the CS2 window while it's active; otherwise lines is nil and err says why." + Game));
        Returns["game.me"] = "Me";
        Returns["game.weapon"] = "Weapon";
        Returns["game.score"] = "Score";

        const string Chat = " Needs  -- @permissions chat";
        Module("chat", "Messages in the CS2 chat (max 1 per second, 120 chars)." + Chat,
            ("say(text)", "Sends to all chat." + Chat),
            ("say_team(text)", "Sends to team chat." + Chat),
            ("connected() -> boolean", "True when CS2's console port is reachable (launch option -netconport 2121)." + Chat),
            ("say_exec(text)", "Sends to all chat without the chat box: writes cfg\\apathy_exec.cfg and presses the exec key. Needs once in CS2: bind \"F8\" \"exec apathy_exec\"." + Chat),
            ("say_team_exec(text)", "Like say_exec, to team chat." + Chat),
            ("exec(command)", "Runs any console command the same way as say_exec. Queued until CS2 has focus." + Chat),
            ("set_exec_key(key)", "The key your CS2 bind uses (default keys.F8)." + Chat),
            ("exec_key() -> number", "The current exec key." + Chat),
            ("console(command)", "Runs a console command through the console port (-netconport 2121)." + Chat));

        const string Input = " Needs  -- @permissions input";
        Module("input", "Presses keys, clicks and places the cursor in whatever window has focus." + Input,
            ("key_down(vk)", "Presses and holds a key: input.key_down(keys.W). Release it with key_up." + Input),
            ("key_up(vk)", "Releases a key pressed with key_down." + Input),
            ("key_tap(vk, ...)", "Presses and releases a key. With several keys, all but the last are held as modifiers: input.key_tap(keys.CTRL, keys.V)." + Input),
            ("type_string(text)", "Types text as Unicode key presses (no clipboard)." + Input),
            ("move(x, y)", "Puts the cursor at screen pixel x, y. Games using raw input ignore it." + Input),
            ("down(button)", "Presses a mouse button: 1 left (default), 2 right, 3 middle, 4 / 5 side." + Input),
            ("up(button)", "Releases a mouse button pressed with down." + Input),
            ("click(button)", "Presses and releases a mouse button (default 1, left)." + Input),
            ("scroll(notches)", "Turns the mouse wheel: positive up, negative down." + Input),
            ("post_click(x, y, button) -> boolean", "Clicks at screen point x, y inside the CS2 window by sending it straight to CS2; your cursor doesn't move. button 1 left (default), 2 right. False when CS2 isn't running." + Input),
            ("post_key(vk) -> boolean", "Taps a key in the CS2 window only (e.g. keys.ESCAPE), without it reaching other windows." + Input));

        Module("storage", "Values kept between runs and restarts (nil, booleans, numbers, strings, tables). No permission needed.",
            ("get(key, default) -> value", "The stored value, or default."),
            ("set(key, value)", "Stores value (nil deletes it). Saved automatically."),
            ("delete(key)", "Removes the key."),
            ("keys() -> table", "All stored keys."),
            ("clear()", "Removes everything this script stored."));

        Module("events", "Messages between scripts (delivered on the next tick).",
            ("on(name, fn(...)) -> Handler", "Calls fn whenever any script emits name."),
            ("emit(name, ...)", "Sends name with values (nil, booleans, numbers, strings, tables) to every script listening."));
        Returns["events.on"] = "Timer";

        Module("base64", "Base64 text encoding.",
            ("encode(text) -> string", "Text to Base64."),
            ("decode(text) -> string", "Base64 back to text; nil if it isn't Base64."));

        Module("hash", "Hashes as lowercase hex.",
            ("sha256(text) -> string", "SHA-256 of the text."),
            ("md5(text) -> string", "MD5 of the text (checksums only, not for security)."),
            ("hmac_sha256(text, key) -> string", "HMAC-SHA256, e.g. for signing API requests."));

        Module("util", "Small utilities.",
            ("uuid() -> string", "A random unique id."),
            ("random(min, max) -> number", "A cryptographically random whole number in min..max; util.random() gives 0..1."),
            ("url_encode(text) -> string", "Escapes text for use in a URL."),
            ("url_decode(text) -> string", "Reverses url_encode."));

        const string Http = " Needs  -- @permissions http";
        Module("http", "Web requests, answered asynchronously." + Http,
            ("get(url, fn(body, status, err))", "Downloads url (max 1 MB)." + Http),
            ("post(url, body, fn(body, status, err), content_type)", "Posts body (default JSON)." + Http),
            ("request(options, fn(body, status, err, headers))", "Any request: { url = ..., method = \"PUT\", headers = { Authorization = \"Bearer ...\" }, body = ..., content_type = ... }. headers in the answer have lowercase names." + Http));

        const string Files = " Needs  -- @permissions files";
        Module("files", "The script's own data folder; plain file names." + Files,
            ("read(name) -> string", "File text, or nil." + Files),
            ("write(name, text)", "Replaces the file." + Files),
            ("append(name, text)", "Adds to the end of the file." + Files),
            ("exists(name) -> boolean", "True if the file exists." + Files),
            ("delete(name)", "Deletes the file." + Files),
            ("list() -> table", "The file names." + Files));

        const string Clip = " Needs  -- @permissions clipboard";
        Module("clipboard", "The Windows clipboard." + Clip,
            ("get() -> string", "Clipboard text, or nil." + Clip),
            ("set(text) -> boolean", "Copies text." + Clip));

        // ---- Values returned by the API ----
        Type("Checkbox", ("get() -> boolean", "Current value."), ("set(on)", "Checks / unchecks it."), ("value", "Read / write the value."));
        Type("Slider", ("get() -> number", "Current value."), ("set(n)", "Moves the slider."), ("value", "Read / write the value."),
             ("min() -> number", "The lowest value."), ("max() -> number", "The highest value."));
        Type("Combo", ("get() -> number", "Selected position (1-based)."), ("text() -> string", "Selected item's text."),
             ("set(index_or_text)", "Selects by position or by text."), ("value", "Read / write the position."),
             ("items() -> table", "All item texts."), ("count() -> number", "Number of items."));
        Type("Color", ("get() -> string", "The color as \"#RRGGBB\"."), ("set(hex)", "Changes the color."), ("value", "Read / write the color."),
             ("rgb() -> r, g, b", "The color as 0..255 channels."));
        Type("Keybind", ("key() -> number", "The bound key code."), ("name() -> string", "The bound key's name (\"F1\"), or nil."),
             ("down() -> boolean", "True while held."), ("pressed() -> boolean", "True on the tick it went down."),
             ("active() -> boolean", "True when on, respecting the bind's mode (hold / toggle)."));
        Type("Label", ("get() -> string", "The current text."), ("set(text)", "Changes the text."));
        Type("HudLine", ("get() -> string", "The current text."), ("set(text)", "Changes the text."),
             ("show(visible)", "Shows / hides the line."), ("visible() -> boolean", "True while shown."), ("remove()", "Removes the line."));
        Type("Timer", ("cancel()", "Stops the timer."), ("active() -> boolean", "True until it fired (after) or was cancelled."));
        Type("Task", ("cancel()", "Stops the task at its next wait()."), ("active() -> boolean", "True until the task finished or was cancelled."));
        Returns["spawn"] = "Task";
        Type("Me", ("name", "Your name."), ("team", "\"CT\" or \"T\"."), ("health", "0..100."), ("armor", "0..100."),
             ("helmet", "boolean."), ("money", "Current money."), ("equip_value", "Value of your equipment."),
             ("flashed", "0..255."), ("smoked", "0..255."), ("burning", "0..255."), ("kills", "Match kills."),
             ("deaths", "Match deaths."), ("assists", "Match assists."), ("mvps", "MVP stars."), ("score", "Scoreboard score."),
             ("round_kills", "Kills this round."), ("round_headshots", "Headshot kills this round."));
        Type("Weapon", ("name", "e.g. \"weapon_ak47\"."), ("type", "e.g. \"Rifle\"."), ("ammo", "Rounds in the magazine."), ("reserve", "Reserve ammo."));
        Type("Score", ("ct", "CT rounds won."), ("t", "T rounds won."), ("round", "Round number."), ("phase", "Match phase."),
             ("mode", "Game mode."), ("map", "Map name."));

        Event("kill", "You got a kill.", ("kills", "Match kills."), ("round_kills", "Kills this round."),
              ("headshot", "true if it was a headshot."), ("weapon", "Weapon used."));
        Event("death", "You died.", ("deaths", "Match deaths."));
        Event("damage", "You lost health.", ("health", "Health now."), ("lost", "Health lost."));
        Event("flashed", "You got flashed.", ("amount", "0..255."));
        Event("mvp", "You got an MVP.", ("mvps", "MVP stars."));
        Event("weapon", "You switched weapons.", ("name", "Weapon name."), ("type", "Weapon type."));
        Event("round_start", "The round went live.");
        Event("round_end", "The round ended.", ("winner", "\"CT\" or \"T\"."));
        Event("bomb_planted", "The bomb was planted.");
        Event("bomb_defused", "The bomb was defused.");
        Event("bomb_exploded", "The bomb exploded.");
        Event("map", "A new map loaded.", ("name", "Map name."));
        Event("match_end", "The match ended.", ("ct", "CT score."), ("t", "T score."));
    }

    /// <summary>Looks up a dotted name for the hover tooltip: "ui.checkbox", "math", "print".</summary>
    public static LuaSymbol? Find(string qualified)
    {
        int dot = qualified.LastIndexOf('.');
        if (dot < 0) return Globals.FirstOrDefault(s => s.Name == qualified && s.Kind != LuaSymbolKind.Snippet);
        return Modules.TryGetValue(qualified[..dot], out var members)
            ? members.FirstOrDefault(s => s.Name == qualified[(dot + 1)..])
            : null;
    }

    // ---- Builders ----

    private static string NameOf(string signature)
    {
        int end = signature.IndexOfAny(['(', ' ']);
        return end < 0 ? signature : signature[..end];
    }

    private static void Global(string signature, string doc) =>
        Globals.Add(new LuaSymbol(NameOf(signature), LuaSymbolKind.Function, signature, doc));

    private static void Snippet(string name, string signature, string doc, string insert) =>
        Globals.Add(new LuaSymbol(name, LuaSymbolKind.Snippet, signature, doc, insert));

    private static void Module(string module, string doc, params (string Signature, string Doc)[] members)
    {
        Globals.Add(new LuaSymbol(module, LuaSymbolKind.Module, module, doc));
        var list = Modules.TryGetValue(module, out var existing) ? existing : Modules[module] = [];
        foreach (var (signature, memberDoc) in members)
            list.Add(new LuaSymbol(NameOf(signature), LuaSymbolKind.Function, $"{module}.{signature}", memberDoc));
    }

    private static void Member(string module, string name, LuaSymbolKind kind, string signature, string doc) =>
        Modules[module].Add(new LuaSymbol(name, kind, signature, doc));

    // Methods show as Type:name(...) (called with a colon), fields as Type.name.
    private static void Type(string type, params (string Signature, string Doc)[] members) =>
        Types[type] = members.Select(m => m.Signature.Contains('(')
            ? new LuaSymbol(NameOf(m.Signature), LuaSymbolKind.Method, $"{type}:{m.Signature}", m.Doc)
            : new LuaSymbol(m.Signature, LuaSymbolKind.Field, $"{type}.{m.Signature}", m.Doc)).ToList();

    private static void Event(string name, string doc, params (string Field, string Doc)[] fields)
    {
        string data = fields.Length == 0 ? "no data" : "{" + string.Join(", ", fields.Select(f => f.Field)) + "}";
        Events.Add(new LuaSymbol(name, LuaSymbolKind.Event, $"\"{name}\"  {data}", doc));
        Types[$"event:{name}"] = fields.Select(f => new LuaSymbol(f.Field, LuaSymbolKind.Field, $"{name}.{f.Field}", f.Doc)).ToList();
    }
}
