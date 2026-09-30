using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Apathy.Services;

/// <summary>
/// Sends chat messages to CS2 through its console port (launch option <c>-netconport 2121</c>).
/// Only <c>say</c> / <c>say_team</c> is ever sent, and the text is cleaned of quotes, ';' and line
/// breaks so it can't carry any other console command. At most one message per second.
/// </summary>
public static class CsChat
{
    private const int MaxLength = 120;
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(1);
    private static readonly SemaphoreSlim SendLock = new(1, 1);

    private static TcpClient? _client;
    private static DateTime _lastSent = DateTime.MinValue;
    private static bool _connecting;

    public static bool IsConnected => _client?.Connected == true;

    /// <summary>
    /// Current connection state, and — because the port is only reachable while CS2 runs with
    /// -netconport — kicks off a background connect attempt when not connected, so a repeated call
    /// (e.g. from on_tick) reflects reality within a moment instead of always returning false.
    /// </summary>
    public static bool Probe()
    {
        if (!IsConnected && !_connecting) _ = ProbeAsync();
        return IsConnected;
    }

    private static async Task ProbeAsync()
    {
        _connecting = true;
        try
        {
            await SendLock.WaitAsync();
            try
            {
                await EnsureConnectedAsync(SettingsStore.Current.NetconPort);
            }
            finally
            {
                SendLock.Release();
            }
        }
        finally
        {
            _connecting = false;
        }
    }

    /// <summary>Sends any console command (no sanitising, no rate-limit). Used for cl_clanname etc.</summary>
    public static async Task<string?> ConsoleAsync(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return "empty command";
        await SendLock.WaitAsync();
        try
        {
            int port = SettingsStore.Current.NetconPort;
            if (!await EnsureConnectedAsync(port))
                return $"CS2's console port {port} isn't open — add -netconport {port} to CS2 launch options";
            try
            {
                await _client!.GetStream().WriteAsync(Encoding.UTF8.GetBytes(command.TrimEnd() + "\n"));
                return null;
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
                _client?.Dispose(); _client = null;
                return "lost connection to CS2 console port";
            }
        }
        finally { SendLock.Release(); }
    }

    /// <summary>Null when sent, otherwise the reason it wasn't.</summary>
    public static async Task<string?> SayAsync(string text, bool team)
    {
        string message = Clean(text);
        if (message.Length == 0) return "empty message";

        await SendLock.WaitAsync();
        try
        {
            if (DateTime.UtcNow - _lastSent < MinInterval) return "too fast (one message per second)";
            int port = SettingsStore.Current.NetconPort;
            if (!await EnsureConnectedAsync(port))
                return $"CS2's console port {port} isn't open. Put exactly \"-netconport {port}\" in CS2's launch options (nothing else after it) and fully restart CS2.";

            byte[] line = Encoding.UTF8.GetBytes($"{(team ? "say_team" : "say")} \"{message}\"\n");
            try
            {
                await _client!.GetStream().WriteAsync(line);
                _lastSent = DateTime.UtcNow;
                return null;
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
                _client?.Dispose();
                _client = null;
                return "lost the connection to CS2's console port";
            }
        }
        finally
        {
            SendLock.Release();
        }
    }

    /// <summary>Chat text that can only ever be the argument of say: no quotes, ';' or control characters.</summary>
    public static string Clean(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (c == '"') builder.Append('\'');
            else if (c == ';') continue;
            else if (char.IsControl(c)) builder.Append(' ');
            else builder.Append(c);
        }
        string clean = builder.ToString().Trim();
        return clean.Length > MaxLength ? clean[..MaxLength] : clean;
    }

    private static async Task<bool> EnsureConnectedAsync(int port)
    {
        if (_client?.Connected == true) return true;
        _client?.Dispose();
        _client = null;

        var client = new TcpClient();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            // "localhost" resolves to both 127.0.0.1 and ::1; CS2 may open netconport on only one, so
            // ConnectAsync tries each until one succeeds.
            await client.ConnectAsync("localhost", port, timeout.Token);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            client.Dispose();
            return false;
        }

        _client = client;
        _ = DrainAsync(client);
        return true;
    }

    // CS2 streams its console output to connected clients; read and discard it so it never backs up.
    private static async Task DrainAsync(TcpClient client)
    {
        var buffer = new byte[4096];
        try
        {
            var stream = client.GetStream();
            while (await stream.ReadAsync(buffer) > 0)
            {
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
        }
        client.Dispose();
    }
}
