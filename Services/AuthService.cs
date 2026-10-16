using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace Apathy.Services;

public enum LoginOutcome { Ok, WrongCredentials, NoPremium, DeviceLocked, RateLimited, ServerError, Offline }

/// <summary>A logged-in apathy.pub account. PremiumUntil is null for lifetime premium.</summary>
public sealed record Account(string Username, string Token, DateTimeOffset? PremiumUntil);

public sealed record LoginResult(LoginOutcome Outcome, Account? Account, string Message)
{
    public bool Ok => Outcome == LoginOutcome.Ok;
}

/// <summary>
/// Logs in against the apathy.pub client API: only accounts with an active premium get in.
/// The server answers with a session token, which is what "Remember me" keeps (never the password)
/// and what the periodic check in <see cref="SessionGuard"/> sends. The API is described in
/// docs/apathy-pub-api.md; set APATHY_API_URL to point the client at a test server.
/// </summary>
public static class AuthService
{
    public static Uri BaseUrl { get; } = new(
        (Environment.GetEnvironmentVariable("APATHY_API_URL") is { Length: > 0 } custom ? custom : "https://apathy.pub")
            .TrimEnd('/') + "/");

    /// <summary>Where the login window sends people without an account or without premium (the website, not the API).</summary>
    public static Uri RegisterUrl { get; } = new("https://apathy.pub/register.php");
    public static Uri PremiumUrl { get; } = new("https://apathy.pub/premium.php");

    private static readonly HttpClient Http = CreateClient();

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static Task<LoginResult> LoginAsync(string username, string password, CancellationToken ct = default)
    {
#if DEBUG
        // Debug builds only: log in without a server to work on the UI. Release builds have no offline account.
        if (username == "1" && password == "1")
            return Task.FromResult(new LoginResult(LoginOutcome.Ok, new Account("1", "debug", null), ""));
#endif
        var body = new LoginRequest(username, password, HardwareId.Value, ClientVersion);
        return SendAsync(() => new HttpRequestMessage(HttpMethod.Post, "api/client/login")
        {
            Content = JsonContent.Create(body, options: Json),
        }, isResume: false, ct);
    }

    /// <summary>Checks a remembered token; also used to re-check premium while apathy runs.</summary>
    public static Task<LoginResult> ResumeAsync(string token, CancellationToken ct = default)
    {
#if DEBUG
        if (token == "debug") return Task.FromResult(new LoginResult(LoginOutcome.Ok, new Account("1", "debug", null), ""));
#endif
        return SendAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "api/client/session");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return request;
        }, isResume: true, ct);
    }

    /// <summary>Ends the session on the server (best effort; the local token is forgotten either way).</summary>
    public static async Task LogoutAsync(string token)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/client/logout");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var _ = await Http.SendAsync(request);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
        }
    }

    private static async Task<LoginResult> SendAsync(Func<HttpRequestMessage> build, bool isResume, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            using var request = build();
            response = await Http.SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new LoginResult(LoginOutcome.Offline, null, "Can't reach apathy.pub. Check your internet connection.");
        }

        using (response)
        {
            AccountResponse? data = null;
            try
            {
                data = await response.Content.ReadFromJsonAsync<AccountResponse>(Json, ct);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or HttpRequestException)
            {
                // Not JSON (a proxy or error page): handled by the status code below.
            }
            string? serverMessage = string.IsNullOrWhiteSpace(data?.Error) ? null : data!.Error;

            switch (response.StatusCode)
            {
                case HttpStatusCode.Unauthorized:
                    return new LoginResult(LoginOutcome.WrongCredentials, null, serverMessage
                        ?? (isResume ? "Your session has expired. Log in again." : "Invalid username or password."));
                case HttpStatusCode.Forbidden:
                    return new LoginResult(LoginOutcome.NoPremium, null, serverMessage
                        ?? "This account doesn't have an active premium.");
                case HttpStatusCode.Conflict:
                    // The login is correct but this PC isn't one the account is locked to.
                    return new LoginResult(LoginOutcome.DeviceLocked, null, serverMessage
                        ?? "This account is locked to another PC. Don't share your login; reset your PC on apathy.pub to move.");
                case HttpStatusCode.TooManyRequests:
                    return new LoginResult(LoginOutcome.RateLimited, null, serverMessage
                        ?? "Too many attempts. Wait a minute and try again.");
            }

            if (!response.IsSuccessStatusCode || data is not { Token.Length: > 0, Username.Length: > 0 })
                return new LoginResult(LoginOutcome.ServerError, null, serverMessage
                    ?? $"apathy.pub answered with an error ({(int)response.StatusCode}). Try again later.");

            // The server should already answer 403, but never let a non-premium answer through.
            if (!data.Premium || data.PremiumUntil is { } until && until <= DateTimeOffset.UtcNow)
                return new LoginResult(LoginOutcome.NoPremium, null, "This account doesn't have an active premium.");

            return new LoginResult(LoginOutcome.Ok, new Account(data.Username, data.Token, data.PremiumUntil), "");
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { BaseAddress = BaseUrl, Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("apathy", ClientVersion));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    private static string ClientVersion { get; } =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    /// <summary>
    /// SHA-256 of Windows' MachineGuid, so the server can limit how many PCs share one account
    /// without learning anything about the PC itself. Empty when the registry key can't be read.
    /// </summary>
    private static readonly Lazy<string> HardwareId = new(() =>
    {
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            if (key?.GetValue("MachineGuid") is not string guid) return "";
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("apathy:" + guid)));
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException
                                       or PlatformNotSupportedException)
        {
            return "";
        }
    });

    private sealed record LoginRequest(string Username, string Password, string Hwid, string ClientVersion);

    private sealed class AccountResponse
    {
        public string? Username { get; set; }
        public string? Token { get; set; }
        public bool Premium { get; set; }

        [JsonPropertyName("premium_until")]
        public DateTimeOffset? PremiumUntil { get; set; }

        public string? Error { get; set; }
    }
}
