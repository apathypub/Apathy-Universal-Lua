namespace Apathy.Services;

/// <summary>Who is logged in (set once after the login window).</summary>
public static class Session
{
    public static string Username { get; set; } = "user";
}
