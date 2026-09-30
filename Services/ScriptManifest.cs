using System.IO;
using System.Text.RegularExpressions;

namespace Apathy.Services;

/// <summary>
/// A script's public info, read from header comments so authors can describe what they share:
/// <c>-- @name</c>, <c>-- @author</c>, <c>-- @version</c>, <c>-- @description</c> and
/// <c>-- @permissions</c> (see <see cref="ScriptPermissions"/>).
/// </summary>
public sealed record ScriptManifest(string? Title, string? Author, string? Version, string? Description,
                                    IReadOnlyList<string> Permissions)
{
    private static readonly Regex Field = new(@"^\s*--\s*@(\w+)\s+(.+?)\s*$", RegexOptions.Multiline);

    public string DisplayTitle(string fallback) => string.IsNullOrWhiteSpace(Title) ? fallback : Title!;

    public static ScriptManifest Parse(string source)
    {
        string? title = null, author = null, version = null, description = null;
        foreach (Match match in Field.Matches(source))
        {
            string value = match.Groups[2].Value;
            switch (match.Groups[1].Value.ToLowerInvariant())
            {
                case "name": title ??= value; break;
                case "author": author ??= value; break;
                case "version": version ??= value; break;
                case "description": case "desc": description ??= value; break;
            }
        }

        var permissions = ScriptPermissions.Parse(source, _ => { }).OrderBy(p => p).ToList();
        return new ScriptManifest(title, author, version, description, permissions);
    }

    public static ScriptManifest Read(string path)
    {
        try
        {
            return Parse(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ScriptManifest(null, null, null, null, []);
        }
    }
}
