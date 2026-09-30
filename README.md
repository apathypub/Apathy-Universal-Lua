# apathy

A Windows desktop companion app with its own scripting platform: a compact menu, a floating
**island** with shortcuts, on-screen HUD text and notifications, a built-in code editor with an
AI assistant, and **Enma**, a small language that compiles to Lua.

## Login

| Username | Password |
| --- | --- |
| `1` | `1` |

This demo account is created on first start in `users.json` next to the executable. Passwords are
stored as PBKDF2-SHA256 hashes. To add accounts, edit `users.json` or replace `AuthService` with
your own backend.

## Features

### Menu

Press **Insert** to show or hide it (the key can be changed in Misc).

| Tab | What it does |
| --- | --- |
| **General** | Controls added by your running scripts, plus shortcuts to the editor and the script list |
| **Lua** | Your scripts: load / unload, script info and permissions, share scripts through the clipboard, log output |
| **ChatLive** | Chat with other users over a WebSocket chat server |
| **Misc** | Auto start with Windows, notifications, menu key, accent color, DPI scale, watermark, island options, CS2 chat port |
| **Config** | Save, load, delete and reset presets; import and export them through the clipboard |

Tabs can be reordered by dragging them.

### Island

A small floating bar at the top of the screen with shortcuts to the menu, the scripts, the editor
and ChatLive, and buttons added by scripts. It can be dragged, locked or collapsed.

### Scripts

| Language | Runs with |
| --- | --- |
| **Lua** (`.lua`) | MoonSharp (Lua 5.2), sandboxed: no `io`, `os.execute` or file access |
| **Enma** (`.enma`) | Compiled to Lua; same API, same line numbers in errors |
| **C#** (`.cs`) | Roslyn scripting |
| **C++** (`.cpp`) | Compiled to a DLL with MSVC (`cl.exe`) or GCC (`g++`) |

The Lua and Enma API includes: `log`, `on_tick`, `on_unload`, `timer.after` / `timer.every`,
`ui.*` controls (checkbox, slider, combo, color, keybind, button, label) that appear in the menu and
are saved with configs, `hud.label` text on screen, `island.button`, `notify`, `confirm`, `color.*`,
`json.*`, `time.*`, `keys.*`, media keys (`media.*`) and `menu.*`.

Extra abilities need a permission line at the top of the script, and apathy asks once before
granting them:

```lua
-- @permissions game, chat, http, files, clipboard, input
```

| Permission | Allows |
| --- | --- |
| `game` | Counter-Strike 2 events and your own stats through Game State Integration |
| `chat` | Sending `say` / `say_team` lines to CS2 |
| `http` | `http.get` / `http.post` |
| `files` | Reading and writing files in the script's own data folder |
| `clipboard` | Reading and setting the clipboard |
| `input` | Key presses, mouse clicks and placing the cursor |

Scripts can't read or write the memory of other processes.

Example:

```lua
-- @name        Clock
-- @description Shows the time on screen with a toggle in the menu.
local enabled = ui.checkbox("Show clock", true)
local label = hud.label("")

on_tick(function()
    label:show(enabled:get())
    label:set(clock())
end)
```

### Editor

Open it from the General tab or the island.

- Tabs, syntax highlighting (Lua, Enma, C#, C++, Luau, Teal, MoonScript, Fennel), completion
  (Ctrl+Space) and find / replace (Ctrl+F / Ctrl+H)
- **F5** runs the open script, **Shift+F5** stops it
- Enma errors and warnings are underlined while you type (**F8** jumps to the next one)
- **AI assistant** (Ctrl+I) that creates and edits scripts in the scripts folder
- **Enma Studio** (Ctrl+E):
  - **Team**: a planner, a coder and a reviewer agent build a script together
  - **Train**: model, effort and instructions per agent, plus lessons and examples from your
    thumbs up / down ratings
  - **Evals**: test tasks with automatic checks and a score history
  - **Lua**: the compiled Lua of the open Enma file (Ctrl+Shift+B)
  - **Docs**: the Enma language reference

The AI assistant and the agents use the [Anthropic API](https://console.anthropic.com) with your own
API key. Enter it in the editor (key icon); it is stored encrypted for your Windows account, and usage
is billed to your Anthropic account.

### CS2 integration

- **Game State Integration**: the first script with the `game` permission writes
  `gamestate_integration_apathy.cfg` into CS2's `cfg` folder. Restart CS2 once afterwards.
- **Chat**: scripts with the `chat` permission need the CS2 launch option `-netconport 2121`
  (Steam → CS2 → Properties → Launch options). The port can be changed in Misc.

## Build and run

Requirements: Windows 10 or later and the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet build
```

```bash
dotnet run
```

A ready-made build is attached to the [Releases](../../releases) page (it needs the
[.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)).

## Files created at runtime

Everything is stored next to the executable and is listed in `.gitignore`.

| File / folder | Contents |
| --- | --- |
| `users.json` | Local accounts |
| `login.json` | The remembered login (encrypted) |
| `ai.key` | Your Anthropic API key (encrypted) |
| `configs/` | Saved presets |
| `scripts/` | Your scripts |
| `data/` | Files written by scripts with the `files` permission |
| `agents/` | Enma Studio agent profiles, evals and their history |

## Project layout

| Folder | Contents |
| --- | --- |
| `Views/` | Menu tabs |
| `Controls/` | Custom controls: slider, color picker, keybind, combo boxes, tab icons |
| `Themes/` | Colors and control styles |
| `Overlay/` | Watermark, HUD lines and notification windows |
| `Services/` | Script hosts (Lua, C#, C++), permissions, configs, CS2 Game State Integration, chat, login |
| `Editor/` | Code editor, AI assistant and Enma Studio (`Editor/Agents`) |
| `Enma/` | The Enma compiler: lexer, parser and Lua code generator |
| `Models/` | Settings and script control models |
| `scripts/` | Scripts copied next to the executable on build (empty by default) |
