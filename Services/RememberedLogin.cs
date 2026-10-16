using System.IO;
using System.Text;
using System.Text.Json;

namespace Apathy.Services;

/// <summary>
/// "Remember me" on the login window: the username and the apathy.pub session token (never the
/// password) in login.json next to the executable. The token is DPAPI-encrypted, so the file is
/// useless on another PC or Windows account, and the server can revoke it at any time.
/// </summary>
public static class RememberedLogin
{
    private static string FilePath { get; } = Path.Combine(AppContext.BaseDirectory, "login.json");

    public static (string Username, string? Token)? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(FilePath));
            if (saved == null) return null;
            string? token = saved.Token.Length > 0 && Native.Unprotect(Convert.FromBase64String(saved.Token)) is { } bytes
                ? Encoding.UTF8.GetString(bytes)
                : null;
            return (saved.Username, token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Keeps the username (and the token when <paramref name="token"/> is set) for the next start.</summary>
    public static void Save(string username, string? token)
    {
        string encrypted = token != null && Native.Protect(Encoding.UTF8.GetBytes(token)) is { } bytes
            ? Convert.ToBase64String(bytes)
            : "";
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new Saved { Username = username, Token = encrypted }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not remembered this time; logging in still works.
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

    private sealed class Saved
    {
        public string Username { get; set; } = "";
        public string Token { get; set; } = "";
    }
}
