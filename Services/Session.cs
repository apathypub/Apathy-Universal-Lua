namespace Apathy.Services;

/// <summary>Who is logged in (set after the login window, cleared by <see cref="SessionGuard"/>).</summary>
public static class Session
{
    public static string Username { get; set; } = "user";

    /// <summary>The apathy.pub session token; empty before login.</summary>
    public static string Token { get; set; } = "";

    /// <summary>When premium ends; null for lifetime premium.</summary>
    public static DateTimeOffset? PremiumUntil { get; set; }

    public static void Start(Account account)
    {
        Username = account.Username;
        Token = account.Token;
        PremiumUntil = account.PremiumUntil;
    }
}
