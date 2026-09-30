using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Apathy.Services;

/// <summary>Finds the local Counter-Strike 2 install through Steam's registry key and library folders.</summary>
public static class Cs2Locator
{
    private const string GameFolder = @"steamapps\common\Counter-Strike Global Offensive\game\csgo";

    /// <summary>The "game\csgo" folder (holds pak01_dir.vpk and cfg\), or null when CS2 isn't found.</summary>
    public static string? FindGameFolder()
    {
        try
        {
            return SteamLibraries()
                .Select(library => Path.Combine(library, GameFolder))
                .FirstOrDefault(folder => File.Exists(Path.Combine(folder, "pak01_dir.vpk")));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static IEnumerable<string> SteamLibraries()
    {
        if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) is not string steam
            || string.IsNullOrWhiteSpace(steam))
        {
            yield break;
        }

        steam = Path.GetFullPath(steam);
        yield return steam;

        // Additional library folders: "path"  "D:\\SteamLibrary"
        string folders = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(folders)) yield break;
        foreach (Match match in Regex.Matches(File.ReadAllText(folders), "\"path\"\\s+\"([^\"]+)\""))
            yield return match.Groups[1].Value.Replace(@"\\", @"\");
    }
}
