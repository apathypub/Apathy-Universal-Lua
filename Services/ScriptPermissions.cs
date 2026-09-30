using System.Text.RegularExpressions;
using System.Windows;

namespace Apathy.Services;

/// <summary>
/// Extra abilities a Lua script can ask for with a header line, e.g. <c>-- @permissions game, chat, http</c>.
/// The first time a script needs a permission the user hasn't granted it yet, apathy asks; the answer is
/// saved with the settings. Without the permission the matching API raises an error.
/// </summary>
public static class ScriptPermissions
{
    public static readonly IReadOnlyDictionary<string, string> Descriptions = new Dictionary<string, string>
    {
        ["game"] = "read CS2 game events and your stats (Game State Integration)",
        ["chat"] = "send messages to the CS2 chat",
        ["http"] = "download data from the internet",
        ["files"] = "save and read its own data files",
        ["clipboard"] = "read and change the clipboard",
        ["input"] = "simulate keyboard and mouse input",
    };

    private static readonly Regex Header = new(@"^\s*(?:--|//)\s*@permissions\s+(.+)$", RegexOptions.Multiline);

    /// <summary>Permissions named in the script's <c>-- @permissions</c> line (unknown names are reported).</summary>
    public static HashSet<string> Parse(string source, Action<string> warn)
    {
        var requested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Header.Match(source) is not { Success: true } match) return requested;

        foreach (string raw in match.Groups[1].Value.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            string name = raw.Trim().ToLowerInvariant();
            if (Descriptions.ContainsKey(name)) requested.Add(name);
            else warn($"unknown permission '{name}' (use {string.Join(", ", Descriptions.Keys)})");
        }
        return requested;
    }

    /// <summary>
    /// True when the script may use everything it asks for: already granted, or the user agrees now.
    /// </summary>
    public static bool Approve(string script, IReadOnlyCollection<string> requested)
    {
        var granted = SettingsStore.Current.ScriptPermissions;
        granted.TryGetValue(script, out var current);
        var missing = requested.Where(p => current?.Contains(p, StringComparer.OrdinalIgnoreCase) != true).ToList();
        if (missing.Count == 0) return true;

        string list = string.Join("\n", missing.Select(p => $"  -  {Descriptions[p]}"));
        var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
        bool allowed = ConfirmDialog.Ask(owner, "Script permissions",
            $"The script \"{script}\" wants to:\n{list}\n\nAllow it?", "Allow", "Don't allow") == true;
        if (!allowed) return false;

        granted[script] = [.. (current ?? []).Union(missing, StringComparer.OrdinalIgnoreCase)];
        return true;
    }

    public static IReadOnlyList<string> GrantedTo(string script) =>
        SettingsStore.Current.ScriptPermissions.TryGetValue(script, out var granted) ? granted : [];

    public static void Revoke(string script) => SettingsStore.Current.ScriptPermissions.Remove(script);
}
