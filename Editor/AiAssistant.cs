using Anthropic;
using Anthropic.Models.Messages;

namespace Apathy.Editor;

/// <summary>
/// Coding agent for the editor. Claude works directly in the scripts folder through the text-editor
/// tool (list, read, create, edit files), then replies with a short summary of what it did.
/// The system prompt teaches it apathy's Lua API so its scripts actually run here.
/// </summary>
public sealed class AiAssistant
{
    private const string Model = "claude-opus-5";
    // Guard against a runaway tool loop in a single request.
    private const int MaxSteps = 20;

    private const string SystemPrompt =
        """
        You are the coding agent built into the apathy script editor. apathy is a Windows desktop platform
        where users write Lua scripts that add small tools, HUD text and menu controls.

        You work directly in the user's scripts folder with the str_replace_based_edit_tool. Paths are
        plain file names in that folder, e.g. "weather_hud.lua"; view "." lists the files. There are no
        subfolders.

        How to work:
        - When the user wants a script or a change, do it in the files: create new files with a short
          snake_case name, or edit the existing file. Do not paste whole scripts into the chat.
        - Start every new script with a manifest header so it's ready to share:
            -- @name        Readable title
            -- @author      apathy
            -- @version     1.0
            -- @description One line about what it does.
          and, only if it uses game/chat/http/files/clipboard, a "-- @permissions ..." line.
        - View a file before editing it unless its current text is already in the message.
        - Prefer str_replace for small changes; use create to write a new file or to rewrite one completely.
        - When you are done, reply with a short summary in the user's language (often Polish): which
          files you created or changed, what the script does, and how to use it (e.g. open it and press
          Run / F5, where its controls appear). A few sentences, no full code listings.
        - If the user only asks a question, just answer it; don't touch files.
        - The user may also write Enma (.enma), a brace-style language that compiles to Lua and uses the same
          API. When they ask for Enma, or edit an .enma file, write Enma. Reference:

        """ + Enma.EnmaDocs.Reference + "\n" + ApiReference;

    /// <summary>What scripts can do in apathy; shared with the Enma Studio agents.</summary>
    internal const string ApiReference =
        """
        Runtime: MoonSharp (Lua 5.2 compatible) in a soft sandbox. There is no io library, no file access,
        no os.execute and no loading of other files. Only .lua and .enma files run; Luau, Teal, MoonScript and
        Fennel files can be written, but apathy cannot run them - say so when relevant.

        apathy API (globals):
        - log(text), print(...)            write to the Output log
        - clock()                          current time as "HH:mm:ss"
        - on_tick(fn)                      fn runs every 50 ms while the script is loaded
        - on_unload(fn)                    fn runs when the script is unloaded
        - key_down(vk)                     true while a Win32 virtual-key code is held, e.g. key_down(keys.SPACE)
        - key_pressed(vk)                  true once per press (edge since this script's previous call; call every tick)
        - key_toggled(vk)                  Caps/Num/Scroll Lock state, e.g. key_toggled(keys.CAPSLOCK)
        - system.screen() -> w, h; system.cursor() -> x, y; system.idle() -> seconds since last input (AFK);
          system.uptime() -> seconds since Windows started   (all read-only)
        - keys.NAME                        key codes: keys.F1..F12, keys.A..Z, keys.D0..D9, keys.NUMPAD0..9,
          keys.SPACE, ENTER, ESCAPE, TAB, SHIFT, CTRL, ALT, INSERT, DELETE, HOME, END, UP/DOWN/LEFT/RIGHT,
          MOUSE1..MOUSE5 ...; keys.name(vk) -> "F1" or nil
        - script.name                      this script's name
        - timer.after(seconds, fn), timer.every(seconds, fn) -> t: t:cancel(), t:active()   (prefer these
          over counting ticks; every() is at least 0.05 s)
        - time.ms() (ms since start, for animations/cooldowns), time.now() (unix seconds),
          time.format(".NET pattern") e.g. time.format("HH:mm")
        - color.rgb(r, g, b), color.hsv(h 0-360, s 0-1, v 0-1), color.lerp(a, b, t) -> "#RRGGBB";
          color.parse(hex) -> r, g, b or nil
        - json.encode(table) -> text, json.decode(text) -> table
        - string additions (also as methods): s:split(sep) -> list, s:trim(), s:starts_with(x),
          s:ends_with(x), s:contains(x) (plain text)
        - math additions: math.clamp(x, min, max), math.lerp(a, b, t), math.round(x, decimals)
        - menu.set_accent("#RRGGBB"), menu.get_accent(), menu.user() (logged-in name),
          menu.scripts() -> names of running scripts, menu.is_loaded(name)
        - media.play_pause(), media.next(), media.previous(), media.volume_up(), media.volume_down(),
          media.mute(), media.now_playing() -> string or nil, media.is_playing()   (alias: spotify)
        - ui.checkbox(label, default) -> h: h:get(), h:set(bool), h.value
        - ui.slider(label, min, max, default, suffix, decimals) -> h: h:get(), h:set(number), h.value, h:min(), h:max()
        - ui.combo(label, {"A", "B"}, default_index) -> h: h:get() (1-based), h:text(), h:set(index or text), h.value,
          h:items(), h:count()
        - ui.color(label, "#RRGGBB") -> h: h:get() -> "#RRGGBB", h:set(hex), h.value, h:rgb() -> r, g, b
        - ui.button(label, fn)
        - ui.label(text) -> h: h:get(), h:set(text)
        - ui.keybind(label, default_vk) -> h: h:key(), h:name(), h:down(), h:pressed(), h:active()
        - hud.label(text) -> h: h:get(), h:set(text), h:show(bool), h:visible(), h:remove()   (text under the watermark)
        - island.button(label, fn)         adds a button to the floating island
        - platform.status()                "connected", "unstable" or "disconnected"; platform.detail() text
        - notify(title, text) or notify(text)
        - confirm(question, function(accepted) ... end)   Accept/Decline pop-up

        Permissions: these APIs work only if the script's FIRST line declares them, e.g.
          -- @permissions game, chat
        (game, chat, http, files, clipboard). apathy asks the user once. Always add the line when you use them.
        - game.on(event, fn(data))         CS2 events via Game State Integration        [game]
            "kill" {kills, round_kills, headshot, weapon}, "death" {deaths}, "damage" {health, lost},
            "flashed" {amount}, "mvp" {mvps}, "weapon" {name, type}, "round_start", "round_end" {winner},
            "bomb_planted", "bomb_defused", "bomb_exploded", "map" {name}, "match_end" {ct, t}
        - game.me() -> {name, team, health, armor, helmet, money, equip_value, flashed, smoked, burning,
                        kills, deaths, assists, mvps, score, round_kills, round_headshots} or nil   [game]
        - game.weapon() -> {name, type, ammo, reserve} or nil; game.score() -> {ct, t, round, phase, mode, map}
        - game.map(), game.round_phase() ("freezetime"/"live"/"over"), game.status()
          ("connected"/"waiting"/"not installed"; CS2 must be restarted once after the first use)
        - game.connected(), game.team() ("CT"/"T"/nil), game.alive(), game.bomb_time() -> seconds or nil,
          game.running() (cs2.exe in the process list), game.focused() (CS2 is the active window)   [game]
        - chat.say(text), chat.say_team(text)   message in the CS2 chat (max 1 per second, 120 chars) [chat]
          chat.connected(). Needs the CS2 launch option -netconport 2121 (port set in Misc > CS2).
        - http.get(url, fn(body, status, err)), http.post(url, body, fn, content_type)   async  [http]
        - files.read(name), files.write(name, text), files.append(name, text), files.exists(name),
          files.delete(name), files.list()   the script's own data folder, plain names   [files]
        - clipboard.get(), clipboard.set(text)   [clipboard]

        Example: trashtalk after your own kill =
          -- @permissions game, chat
          game.on("kill", function(e) chat.say("...") end)

        There is no API that reads game memory, presses keys or moves the mouse; scripts can't act in the
        game (no bhop, auto-fire, aim or key spam). Say so when asked and suggest what is possible instead.

        ui.* controls appear in the script's own box in the menu's General tab and their values are saved.
        Callbacks must return quickly; long loops block the app.
        """;

    private readonly List<MessageParam> _history = [];
    private AnthropicClient? _client;
    private string? _clientKey;

    public void Reset() => _history.Clear();

    /// <summary>
    /// Sends <paramref name="prompt"/> and runs the tool loop until Claude is done. Text arrives through
    /// <paramref name="onText"/>, each file operation through <paramref name="onTool"/>.
    /// Returns true when the model declined the request.
    /// </summary>
    public async Task<bool> AskAsync(string apiKey, string prompt, AiFileTools tools,
                                     Action<string> onText, Action<AiToolResult> onTool, CancellationToken token)
    {
        if (_client == null || _clientKey != apiKey)
        {
            _client = new AnthropicClient { ApiKey = apiKey };
            _clientKey = apiKey;
        }

        int start = _history.Count;
        _history.Add(new MessageParam { Role = Role.User, Content = prompt });
        try
        {
            for (int step = 0; step < MaxSteps; step++)
            {
                var response = await _client.Messages.Create(new MessageCreateParams
                {
                    Model = Model,
                    MaxTokens = 16000,
                    System = new List<TextBlockParam> { new() { Text = SystemPrompt } },
                    // Caches the conversation so far; each step only pays full price for what's new.
                    CacheControl = new CacheControlEphemeral(),
                    Tools = [new ToolUnion(new ToolTextEditor20250728())],
                    Messages = [.. _history],
                }, token);

                List<ContentBlockParam> assistant = [];
                List<ContentBlockParam> results = [];
                foreach (var block in response.Content)
                {
                    if (block.TryPickText(out TextBlock? text))
                    {
                        assistant.Add(new TextBlockParam { Text = text.Text });
                        onText(text.Text);
                    }
                    else if (block.TryPickThinking(out ThinkingBlock? thinking))
                    {
                        // Echoed back unchanged: the signature must match.
                        assistant.Add(new ThinkingBlockParam { Thinking = thinking.Thinking, Signature = thinking.Signature });
                    }
                    else if (block.TryPickRedactedThinking(out RedactedThinkingBlock? redacted))
                    {
                        assistant.Add(new RedactedThinkingBlockParam { Data = redacted.Data });
                    }
                    else if (block.TryPickToolUse(out ToolUseBlock? toolUse))
                    {
                        assistant.Add(new ToolUseBlockParam { ID = toolUse.ID, Name = toolUse.Name, Input = toolUse.Input });
                        var result = tools.Execute(toolUse.Input);
                        onTool(result);
                        results.Add(new ToolResultBlockParam { ToolUseID = toolUse.ID, Content = result.Content, IsError = result.IsError });
                    }
                }

                _history.Add(new MessageParam { Role = Role.Assistant, Content = assistant });
                if (response.StopReason == StopReason.Refusal) return true;
                if (results.Count == 0) return false;
                _history.Add(new MessageParam { Role = Role.User, Content = results });
            }

            onText("\n(Stopped: too many steps in one request.)");
            return false;
        }
        catch
        {
            // Failed or stopped mid-way: forget this exchange so the next request starts from a valid history.
            _history.RemoveRange(start, _history.Count - start);
            throw;
        }
    }
}
