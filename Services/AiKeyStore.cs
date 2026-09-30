using System.IO;
using System.Text;

namespace Apathy.Services;

/// <summary>
/// The user's Anthropic API key in ai.key next to the executable, DPAPI-encrypted
/// (unreadable on another PC or Windows account).
/// </summary>
public static class AiKeyStore
{
    private static string FilePath { get; } = Path.Combine(AppContext.BaseDirectory, "ai.key");

    public static string? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            return Native.Unprotect(Convert.FromBase64String(File.ReadAllText(FilePath))) is { } key
                ? Encoding.UTF8.GetString(key)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            return null;
        }
    }

    /// <summary>False when the key couldn't be encrypted or written.</summary>
    public static bool Save(string key)
    {
        if (Native.Protect(Encoding.UTF8.GetBytes(key)) is not { } encrypted) return false;
        try
        {
            File.WriteAllText(FilePath, Convert.ToBase64String(encrypted));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static void Clear()
    {
        try
        {
            File.Delete(FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
