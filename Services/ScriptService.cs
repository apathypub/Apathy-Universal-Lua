using System.IO;
using Apathy.Models;

namespace Apathy.Services;

/// <summary>
/// Lists runnable script files in the "scripts" folder and dispatches load/unload to the
/// appropriate host: <see cref="LuaHost"/> (.lua, and .enma compiled to Lua), <see cref="CSharpHost"/> (.cs),
/// or <see cref="CppHost"/> (.cpp).
/// </summary>
public static class ScriptService
{
    private static readonly LuaHost            LuaHost      = new();
    private static readonly CSharpHost         CSharpHost   = new();
    private static readonly CppHost            CppHost      = new();

    static ScriptService()
    {
        SettingsStore.Replaced += (_, _) =>
        {
            LuaHost.StopAll();
            CSharpHost.StopAll();
            CppHost.StopAll();
            StartActive();
        };
    }

    /// <summary>Per-script control panels contributed by Lua scripts (bound by the General tab).</summary>
    public static System.Collections.ObjectModel.ObservableCollection<Models.LuaScriptPanel> Panels => LuaHost.Panels;

    public static string Folder { get; } = Path.Combine(AppContext.BaseDirectory, "scripts");

    /// <summary>Raised after a script is loaded or unloaded.</summary>
    public static event EventHandler? Changed;

    public static bool IsLoaded(string name) =>
        LuaHost.IsRunning(name) || CSharpHost.IsRunning(name) ||
        CppHost.IsRunning(name);

    public static IReadOnlyList<string> Loaded() =>
        LuaHost.RunningNames
            .Concat(CSharpHost.RunningNames)
            .Concat(CppHost.RunningNames)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static void Delete(ScriptEntry script)
    {
        if (script.IsLoaded) Unload(script);
        File.Delete(script.Path);
        Changed?.Invoke(null, EventArgs.Empty);
    }

    private static readonly string[] RunnableExtensions = [".lua", ".enma", ".cs", ".cpp"];

    public static IReadOnlyList<ScriptEntry> List()
    {
        var active = SettingsStore.Current.ActiveScripts;
        var results = new List<ScriptEntry>();

        // Regular scripts folder
        if (Directory.Exists(Folder))
        {
            results.AddRange(new[] { ".lua", ".enma", ".cs", ".cpp" }
                .SelectMany(ext => Directory.EnumerateFiles(Folder, "*" + ext))
                .Select(path => new ScriptEntry(Path.GetFileNameWithoutExtension(path), path, isInternal: false)));
        }

        foreach (var s in results) s.IsLoaded = IsActive(active, s.Name);
        return results.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static void Load(ScriptEntry script, bool reload = false)
    {
        bool wasLoaded = IsLoaded(script.Name);
        var active = SettingsStore.Current.ActiveScripts;
        if (!IsActive(active, script.Name)) active.Add(script.Name);

        script.IsLoaded = HostFor(script.Path).Run(script.Name, script.Path, reload);

        if (script.IsLoaded && !wasLoaded)
            NotificationService.Show("Script loaded", script.Name, NotificationKind.Success);
        else if (!script.IsLoaded)
            Remove(active, script.Name);

        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static void Unload(ScriptEntry script)
    {
        Remove(SettingsStore.Current.ActiveScripts, script.Name);
        HostFor(script.Path).Stop(script.Name);
        script.IsLoaded = false;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static void StartActive()
    {
        var active = SettingsStore.Current.ActiveScripts;
        foreach (var script in List().Where(s => IsActive(active, s.Name)))
            HostFor(script.Path).Run(script.Name, script.Path);
    }

    public static bool IsActive(IEnumerable<string> active, string name) =>
        active.Contains(name, StringComparer.OrdinalIgnoreCase);

    private static void Remove(IList<string> active, string name)
    {
        for (int i = active.Count - 1; i >= 0; i--)
            if (string.Equals(active[i], name, StringComparison.OrdinalIgnoreCase)) active.RemoveAt(i);
    }

    private static IScriptHost HostFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".cs"  => CSharpHost,
        ".cpp" => CppHost,
        _      => LuaHost,
    };
}

// Minimal interface so HostFor() can return any host.
internal interface IScriptHost
{
    bool IsRunning(string name);
    IEnumerable<string> RunningNames { get; }
    bool Run(string name, string path, bool reload = false);
    void Stop(string name);
    void StopAll();
}
