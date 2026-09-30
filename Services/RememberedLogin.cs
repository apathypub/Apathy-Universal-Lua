using System.IO;
using System.Text;
using System.Text.Json;

namespace Apathy.Services;

/// <summary>
/// "Remember me" on the login window: username + password in login.json next to the executable.
/// The password is DPAPI-encrypted, so the file is useless on another PC or Windows account.
/// </summary>
public static class RememberedLogin
{
    private static string FilePath { get; } = Path.Combine(AppContext.BaseDirectory, "login.json");

    public static (string Username, string Password)? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(FilePath));
            if (saved == null || Native.Unprotect(Convert.FromBase64String(saved.Password)) is not { } password) return null;
            return (saved.Username, Encoding.UTF8.GetString(password));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            return null;
        }
    }

    public static void Save(string username, string password)
    {
        if (Native.Protect(Encoding.UTF8.GetBytes(password)) is not { } encrypted) return;
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new Saved { Username = username, Password = Convert.ToBase64String(encrypted) }));
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
        public string Password { get; set; } = "";
    }
}
