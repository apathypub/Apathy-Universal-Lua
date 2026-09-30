using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace Apathy.Services;

/// <summary>
/// Checks logins against users.json next to the executable (username + PBKDF2-SHA256 salt/hash,
/// same format as AppLauncher). Offline placeholder: anyone who can edit the file can add an account,
/// so swap this for a call to your own server once there is one.
/// </summary>
public sealed class AuthService(string usersFilePath)
{
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const int Iterations = 100_000;

    // Created on first run so the menu can be opened out of the box. Remove once real accounts exist.
    private const string DemoUsername = "1";
    private const string DemoPassword = "1";

    public static string DefaultPath { get; } = Path.Combine(AppContext.BaseDirectory, "users.json");

    /// <summary>True when the username exists and the password matches. Slow on purpose (PBKDF2), call off the UI thread.</summary>
    public bool Verify(string username, string password)
    {
        var user = LoadUsers().FirstOrDefault(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase));
        if (user == null) return false;

        byte[] expected = Convert.FromBase64String(user.Hash);
        byte[] actual = Hash(password, Convert.FromBase64String(user.Salt));
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>Writes users.json with the demo account if the file doesn't exist yet.</summary>
    public void EnsureSeeded()
    {
        if (File.Exists(usersFilePath)) return;
        try
        {
            File.WriteAllText(usersFilePath, JsonSerializer.Serialize(new[] { CreateUser(DemoUsername, DemoPassword) },
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Read-only folder: no accounts, so every login simply fails.
        }
    }

    private List<UserRecord> LoadUsers() =>
        File.Exists(usersFilePath)
            ? JsonSerializer.Deserialize<List<UserRecord>>(File.ReadAllText(usersFilePath)) ?? []
            : [];

    private static UserRecord CreateUser(string username, string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        return new UserRecord
        {
            Username = username,
            Salt = Convert.ToBase64String(salt),
            Hash = Convert.ToBase64String(Hash(password, salt)),
        };
    }

    private static byte[] Hash(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashSize);

    private sealed class UserRecord
    {
        public string Username { get; set; } = "";
        public string Salt { get; set; } = "";
        public string Hash { get; set; } = "";
    }
}
