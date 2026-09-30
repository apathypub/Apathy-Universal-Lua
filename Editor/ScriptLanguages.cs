using System.IO;
using System.Xml;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;

namespace Apathy.Editor;

/// <summary>
/// A scripting language the editor opens. Only <see cref="Runs"/> languages execute in apathy.
/// <see cref="Badge"/> / <see cref="Color"/> mark its files in the explorer and tabs.
/// When <see cref="BuiltinHighlighting"/> is set, AvalonEdit's built-in definition is used instead of an embedded .xshd.
/// </summary>
public sealed record ScriptLanguage(string Name, string Extension, bool Runs, string Template, string Badge, string Color, string? BuiltinHighlighting = null);

public static class ScriptLanguages
{
    private const string LuaTemplate =
        """
        -- @name        My script
        -- @author      you
        -- @version     1.0
        -- @description What this script does.
        -- Ask for extra abilities here, e.g.: -- @permissions game, chat, http, files, clipboard

        -- Runs inside apathy (Lua 5.2). API: log, ui, hud, timer, color, keys, game, chat, ...
        -- Hints pop up while you type (Ctrl+Space to ask); hover a name to see what it does.
        log("hello from " .. script.name)

        local enabled = ui.checkbox("Enabled", true)

        on_tick(function()
            if not enabled:get() then return end
            -- runs every 50 ms
        end)

        """;

    private const string EnmaTemplate =
        """
        // @name        My script
        // @author      you
        // @version     1.0
        // @description What this script does.
        // Ask for extra abilities here, e.g.: // @permissions game, chat, http, files, clipboard

        // Enma compiles to Lua and runs inside apathy with the same API (log, ui, hud, timer, game, ...).
        // F5 runs it; Ctrl+Shift+B shows the generated Lua. Problems are underlined while you type.
        let enabled = ui.checkbox("Enabled", true)
        let ticks = 0

        log("hello from {script.name}")

        on tick {
            if !enabled:get() { return }
            ticks += 1 // runs every 50 ms
        }

        every 5 {
            log("{ticks} ticks so far")
        }

        """;

    private const string CSharpTemplate =
        """
        // @permissions input
        // Runs inside apathy (.NET). API: log(), on_tick(), input.*, system.*, media.*, notify()
        using System;

        log($"hello from {script_name}");

        on_tick(() =>
        {
            // runs every 50 ms
        });

        on_unload(() =>
        {
            log("bye");
        });
        """;

    private const string CppTemplate =
        """
        // @permissions input
        // Compile: MSVC (cl.exe) or GCC (g++) must be in PATH or VS installed.
        // API: g_api->log(), g_api->send_key(), g_api->mouse_button(), g_api->mouse_scroll()
        #include "apathy.h"

        static ApathyApi* g_api = nullptr;

        extern "C" void apathy_init(ApathyApi* api) {
            g_api = api;
            if (g_api) g_api->log("hello from C++");
        }

        extern "C" void apathy_tick() {
            // runs every ~50 ms
        }

        extern "C" void apathy_unload() {
            // cleanup
        }
        """;

    public static IReadOnlyList<ScriptLanguage> All { get; } =
    [
        new("Lua",        ".lua",  true,  LuaTemplate,    "LUA",  "#4A9BE8"),
        new("Enma",       ".enma", true,  EnmaTemplate,   "ENMA", "#E0457B"),
        new("C#",         ".cs",   true,  CSharpTemplate,         "C#",  "#9B59B6", "C#"),
        new("C++",        ".cpp",  true,  CppTemplate,    "C++",  "#E74C3C", "C++"),
        new("Luau",       ".luau", false, "local message: string = \"hello\"\nprint(message)\n", "LUAU", "#00A2FF"),
        new("Teal",       ".tl",   false, "local message: string = \"hello\"\nprint(message)\n", "TL",   "#3AC2B4"),
        new("MoonScript", ".moon", false, "message = \"hello\"\nprint message\n",                "MOON", "#D6B23A"),
        new("Fennel",     ".fnl",  false, "(local message \"hello\")\n(print message)\n",        "FNL",  "#8FBC5A"),
    ];

    public static ScriptLanguage Default => All[0];

    public static ScriptLanguage? ForPath(string path) =>
        All.FirstOrDefault(l => string.Equals(l.Extension, Path.GetExtension(path), StringComparison.OrdinalIgnoreCase));

    private static bool _registered;

    /// <summary>Loads the embedded .xshd definitions (Editor/Highlighting) into AvalonEdit.
    /// Languages with <see cref="ScriptLanguage.BuiltinHighlighting"/> use AvalonEdit's built-in definitions instead.</summary>
    public static void RegisterHighlighting()
    {
        if (_registered) return;
        _registered = true;

        var assembly = typeof(ScriptLanguages).Assembly;
        foreach (var language in All)
        {
            if (language.BuiltinHighlighting != null)
            {
                var builtin = HighlightingManager.Instance.GetDefinition(language.BuiltinHighlighting);
                if (builtin != null)
                    HighlightingManager.Instance.RegisterHighlighting(language.Name, [language.Extension], builtin);
                continue;
            }

            string resource = $"Apathy.Editor.Highlighting.{language.Name}.xshd";
            using var stream = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"Missing embedded resource {resource}.");
            using var reader = XmlReader.Create(stream);
            var definition = HighlightingLoader.Load(reader, HighlightingManager.Instance);
            HighlightingManager.Instance.RegisterHighlighting(language.Name, [language.Extension], definition);
        }
    }

    public static IHighlightingDefinition? Highlighting(ScriptLanguage? language) =>
        language is null ? null : HighlightingManager.Instance.GetDefinition(language.Name);
}
