using System.IO;
using MoonSharp.Interpreter;

namespace Apathy.Services;

/// <summary>
/// A script's own data folder (data\&lt;script&gt;\ next to the executable) for the "files" permission.
/// Only plain file names are accepted, so a script can't reach anything outside its folder.
/// </summary>
public static class ScriptFiles
{
    private const int MaxFileBytes = 1024 * 1024;

    public static string Root { get; } = Path.Combine(AppContext.BaseDirectory, "data");

    public static string? Read(string script, string name)
    {
        string path = PathFor(script, name);
        return Guard(() => File.Exists(path) ? File.ReadAllText(path) : null);
    }

    public static void Write(string script, string name, string text, bool append)
    {
        string path = PathFor(script, name);
        Guard(() =>
        {
            long size = (append && File.Exists(path) ? new FileInfo(path).Length : 0) + text.Length;
            if (size > MaxFileBytes) throw new ScriptRuntimeException($"{name} would be larger than 1 MB.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (append) File.AppendAllText(path, text);
            else File.WriteAllText(path, text);
            return true;
        });
    }

    public static bool Exists(string script, string name) => File.Exists(PathFor(script, name));

    public static void Delete(string script, string name)
    {
        string path = PathFor(script, name);
        Guard(() =>
        {
            // File.Delete ignores a missing file but throws when the script's folder doesn't exist yet.
            if (File.Exists(path)) File.Delete(path);
            return true;
        });
    }

    public static List<string> List(string script)
    {
        string folder = Path.Combine(Root, script);
        return Guard(() => Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder).Select(Path.GetFileName).OfType<string>().Order().ToList()
            : []);
    }

    private static string PathFor(string script, string name)
    {
        string file = Path.GetFileName(name.Replace('\\', '/'));
        if (file != name.Trim() || file is "" or "." or ".." || file.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ScriptRuntimeException($"'{name}' isn't a valid file name (plain names only, no folders).");
        return Path.Combine(Root, script, file);
    }

    // Disk errors become Lua errors the script can see (or pcall).
    private static T Guard<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ScriptRuntimeException(ex.Message);
        }
    }
}
