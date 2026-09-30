using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Apathy.Overlay;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace Apathy.Services;

// Shared state between CSharpHost and CSharpScriptGlobals.
public sealed class CSharpInstance(string name, IReadOnlySet<string> permissions)
{
    public readonly string Name = name;
    public readonly IReadOnlySet<string> Permissions = permissions;
    public bool Started;
    public CSharpScriptGlobals? Globals;

    public void Require(string perm)
    {
        if (!Permissions.Contains(perm))
            throw new InvalidOperationException(
                $"needs '{perm}' permission — add  // @permissions {perm}  at the top");
    }
}

/// <summary>
/// Runs .cs scripts via Roslyn CSharpScript. Permissions declared with <c>// @permissions input</c>.
/// </summary>
public sealed class CSharpHost : IScriptHost
{
    private readonly Dictionary<string, CSharpInstance> _running = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _timer;

    public CSharpHost()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(50) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    public bool IsRunning(string name) => _running.ContainsKey(name);
    public IEnumerable<string> RunningNames => _running.Keys;

    public bool Run(string name, string path, bool reload = false)
    {
        if (_running.ContainsKey(name) && !reload) return true;
        Stop(name);

        string source;
        try { source = File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LuaLog.Write($"[{name}] cannot read file: {ex.Message}");
            NotificationService.Show($"{name} failed to load", ex.Message, NotificationKind.Error);
            return false;
        }

        var requested = ScriptPermissions.Parse(source, w => LuaLog.Write($"[{name}] {w}"));
        if (!ScriptPermissions.Approve(name, requested)) return false;

        var instance = new CSharpInstance(name, requested);
        var globals  = new CSharpScriptGlobals(instance);
        _running[name] = instance;
        instance.Globals = globals;

        _ = RunTopLevelAsync(instance, source);
        LuaLog.Write($"[{name}] loaded");
        return true;
    }

    private async Task RunTopLevelAsync(CSharpInstance instance, string source)
    {
        try
        {
            var options = BuildOptions();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await CSharpScript.RunAsync(source, options, globals: instance.Globals!,
                globalsType: typeof(CSharpScriptGlobals), cancellationToken: cts.Token);

            await System.Windows.Application.Current!.Dispatcher.InvokeAsync(() => instance.Started = true);
        }
        catch (OperationCanceledException)
        {
            await ReportAsync(instance, "script top level timed out (15 s)");
        }
        catch (CompilationErrorException ex)
        {
            await ReportAsync(instance, string.Join("; ", ex.Diagnostics.Take(3)));
        }
        catch (Exception ex)
        {
            await ReportAsync(instance, ex.Message);
        }
    }

    private async Task ReportAsync(CSharpInstance instance, string msg)
    {
        await System.Windows.Application.Current!.Dispatcher.InvokeAsync(() =>
        {
            LuaLog.Write($"[{instance.Name}] error: {msg}");
            NotificationService.Show($"{instance.Name} stopped", msg, NotificationKind.Error);
            _running.Remove(instance.Name);
        });
    }

    private static ScriptOptions BuildOptions() => ScriptOptions.Default
        .AddReferences(
            typeof(object).Assembly,
            typeof(System.Collections.Generic.List<>).Assembly,
            typeof(System.Linq.Enumerable).Assembly,
            typeof(System.Net.Http.HttpClient).Assembly,
            typeof(CSharpScriptGlobals).Assembly)   // the app itself, for the globals' types
        .AddImports("System", "System.Collections.Generic", "System.Linq",
                    "System.Threading.Tasks", "System.IO");

    public void Stop(string name)
    {
        if (!_running.Remove(name, out var inst)) return;
        if (inst.Globals != null)
            foreach (var cb in inst.Globals.UnloadCallbacks.ToArray())
                try { cb(); } catch (Exception ex) { LuaLog.Write($"[{name}] unload: {ex.Message}"); }
        LuaLog.Write($"[{name}] unloaded");
    }

    public void StopAll()
    {
        foreach (var name in _running.Keys.ToArray()) Stop(name);
    }

    private void Tick()
    {
        foreach (var inst in _running.Values.ToArray())
        {
            if (!inst.Started || inst.Globals == null) continue;
            foreach (var cb in inst.Globals.TickCallbacks.ToArray())
            {
                try { cb(); }
                catch (Exception ex)
                {
                    LuaLog.Write($"[{inst.Name}] error: {ex.Message}");
                    NotificationService.Show($"{inst.Name} stopped", ex.Message, NotificationKind.Error);
                    _running.Remove(inst.Name);
                    break;
                }
            }
        }
    }
}

/// <summary>
/// Globals object injected into every C# script. Public members are accessible directly in the script.
/// </summary>
public class CSharpScriptGlobals(CSharpInstance instance)
{
    internal readonly List<Action> TickCallbacks   = [];
    internal readonly List<Action> UnloadCallbacks = [];

    // ── Identity ──────────────────────────────────────────────────────────────
    public string script_name => instance.Name;

    // ── Logging ───────────────────────────────────────────────────────────────
    public void log(string msg) => LuaLog.Write($"[{instance.Name}] {msg}");

    // ── Callbacks ─────────────────────────────────────────────────────────────
    public void on_tick(Action fn)   { if (fn != null) TickCallbacks.Add(fn); }
    public void on_unload(Action fn) { if (fn != null) UnloadCallbacks.Add(fn); }

    // ── Keyboard state ────────────────────────────────────────────────────────
    public bool key_down(int vk)    => Native.IsKeyDown(vk);
    public bool key_toggled(int vk) => Native.IsKeyToggled(vk);

    // ── Notifications ─────────────────────────────────────────────────────────
    public void notify(string text)               => NotificationService.Show(instance.Name, text);
    public void notify(string title, string text) => NotificationService.Show(title, text);

    // ── Time ──────────────────────────────────────────────────────────────────
    public string clock() => DateTime.Now.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

    // ── Namespaced APIs (exposed as properties) ───────────────────────────────
    public CsInputApi   input   { get; } = new(instance);
    public CsSystemApi  system  { get; } = new();
    public CsMediaApi   media   { get; } = new();
    public CsMenuApi    menu    { get; } = new();

    // ── input.* ───────────────────────────────────────────────────────────────
    public sealed class CsInputApi(CSharpInstance inst)
    {
        private void R() => inst.Require("input");
        public void key_down(int vk)         { R(); if (vk != 0) Native.SendKey(vk, true); }
        public void key_up(int vk)           { R(); if (vk != 0) Native.SendKey(vk, false); }
        public void key_tap(int vk)          { R(); if (vk == 0) return; Native.SendKey(vk, true); Native.SendKey(vk, false); }
        public void key_tap(int mod, int vk) { R(); Native.SendKey(mod, true); Native.SendKey(vk, true); Native.SendKey(vk, false); Native.SendKey(mod, false); }
        public void move(int x, int y)       { R(); Native.MoveMouse(x, y); }
        public void mouse_down(int btn = 1)  { R(); Native.SendMouseButton(btn, true); }
        public void mouse_up(int btn = 1)    { R(); Native.SendMouseButton(btn, false); }
        public void click(int btn = 1)       { R(); Native.SendMouseButton(btn, true); Native.SendMouseButton(btn, false); }
        public void scroll(int delta)        { R(); if (delta != 0) Native.ScrollMouse(delta); }
    }

    // ── system.* ──────────────────────────────────────────────────────────────
    public sealed class CsSystemApi
    {
        public (int Width, int Height) screen() => Native.ScreenSize();
        public (int X, int Y)          cursor() => Native.CursorPosition();
        public double                  idle()   => Math.Round(Native.IdleTime().TotalSeconds, 1);
        public double                  uptime() => Math.Floor(Environment.TickCount64 / 1000.0);
    }

    // ── media.* ───────────────────────────────────────────────────────────────
    public sealed class CsMediaApi
    {
        private const byte VkNext = 0xB0, VkPrev = 0xB1, VkPlay = 0xB3;
        private const byte VkMute = 0xAD, VkVolD = 0xAE, VkVolU = 0xAF;
        public void play_pause()      => Native.TapKey(VkPlay);
        public void next()            => Native.TapKey(VkNext);
        public void previous()        => Native.TapKey(VkPrev);
        public void volume_up()       => Native.TapKey(VkVolU);
        public void volume_down()     => Native.TapKey(VkVolD);
        public void mute()            => Native.TapKey(VkMute);
        public string? now_playing()  => SpotifyInfo.NowPlaying();
        public bool    is_playing()   => SpotifyInfo.NowPlaying() != null;
    }

    // ── menu.* ────────────────────────────────────────────────────────────────
    public sealed class CsMenuApi
    {
        public string user() => Session.Username;
    }
}
