using System.Diagnostics;

namespace Apathy.Services;

/// <summary>
/// Reads what Spotify is playing from the Spotify window title (it shows "Artist - Title" while
/// playing). Read-only: it only looks at the window title, nothing is injected or controlled here.
/// </summary>
public static class SpotifyInfo
{
    private const string ProcessName = "Spotify";

    // Titles Spotify shows when nothing is playing.
    private static readonly string[] Idle = ["Spotify", "Spotify Free", "Spotify Premium", "Advertisement"];

    /// <summary>"Artist - Title" when playing, or null when paused / not running.</summary>
    public static string? NowPlaying()
    {
        try
        {
            foreach (var process in Process.GetProcessesByName(ProcessName))
            {
                using (process)
                {
                    string title = process.MainWindowTitle;
                    if (!string.IsNullOrWhiteSpace(title) && !Idle.Contains(title, StringComparer.OrdinalIgnoreCase))
                        return title;
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // process exited between enumeration and access
        }
        return null;
    }
}
