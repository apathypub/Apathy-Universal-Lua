using System.IO;
using System.Text.RegularExpressions;

namespace Apathy.Services;

/// <summary>Sharing scripts between apathy users: a script is just its .lua text (the manifest and
/// permissions travel in its header comments), so "export" is the file's text and "import" writes it
/// back into the scripts folder under a safe name.</summary>
public static class ScriptSharing
{
    public static string Export(string path) => File.ReadAllText(path);

    /// <summary>
    /// Writes shared <paramref name="text"/> into the scripts folder. The name comes from the manifest's
    /// <c>@name</c> (or <paramref name="fallbackName"/>), made safe and unique. Returns the new path.
    /// </summary>
    public static string Import(string text, string fallbackName = "shared_script")
    {
        var manifest = ScriptManifest.Parse(text);
        string baseName = Slug(manifest.Title ?? fallbackName);

        Directory.CreateDirectory(ScriptService.Folder);
        string name = baseName;
        for (int i = 2; File.Exists(Path.Combine(ScriptService.Folder, name + ".lua")); i++) name = $"{baseName}_{i}";

        string path = Path.Combine(ScriptService.Folder, name + ".lua");
        File.WriteAllText(path, text);
        return path;
    }

    /// <summary>A plain, safe file name (letters, digits, '_', '-') derived from a title.</summary>
    private static string Slug(string title)
    {
        string slug = Regex.Replace(title.Trim().ToLowerInvariant(), @"[^a-z0-9]+", "_").Trim('_');
        if (slug.Length > 40) slug = slug[..40].Trim('_');
        return slug.Length == 0 ? "shared_script" : slug;
    }
}
