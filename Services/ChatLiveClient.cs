using System.Collections.ObjectModel;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Apathy.Models;

namespace Apathy.Services;

public sealed class ChatUser(string name, bool isYou)
{
    public string Name { get; } = name;
    public bool IsYou { get; } = isYou;
}

public sealed class ChatMessage(string name, string text, DateTime time, bool isSystem = false, bool isOwn = false,
                                bool isMention = false)
{
    public string Name { get; } = name;
    public string Text { get; } = text;
    public DateTime Time { get; } = time;
    public string TimeText => Time.ToString("HH:mm");
    public bool IsSystem { get; } = isSystem;
    public bool IsOwn { get; } = isOwn;
    /// <summary>Someone else's message containing @yourname.</summary>
    public bool IsMention { get; } = isMention;
    /// <summary>False for a follow-up from the same person within a few minutes (drawn without name/avatar).</summary>
    public bool ShowHeader { get; set; } = true;
    /// <summary>First message of a day: a "Today" / date divider is drawn above it.</summary>
    public bool ShowDate { get; set; }
    public string DateText =>
        Time.Date == DateTime.Today ? "Today"
        : Time.Date == DateTime.Today.AddDays(-1) ? "Yesterday"
        : Time.ToString("d MMM yyyy");
}

/// <summary>
/// Connection to a ChatLive server (the ChatServer project) over a WebSocket. Reconnects with backoff,
/// pings every 10 s to measure latency and reports the result to <see cref="ConnectionService"/>.
/// Must be started from the UI thread: all collections are updated there.
/// </summary>
public sealed class ChatLiveClient : ObservableObject
{
    private const int MaxMessages = 200;
    private const int MaxFrameBytes = 1024 * 1024;
    private const int SlowLatencyMs = 1000;
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PongTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan[] Backoff =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];

    public static ChatLiveClient Instance { get; } = new();

    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private CancellationTokenSource? _cts;
    private ClientWebSocket? _socket;
    private DateTime? _pingSentAt;
    private int _latencyMs;

    private ChatLiveClient()
    {
    }

    public ObservableCollection<ChatMessage> Messages { get; } = [];
    public ObservableCollection<ChatUser> Users { get; } = [];

    /// <summary>Name the server assigned (may get a #2 suffix if taken).</summary>
    public string Name { get; private set => SetField(ref field, value); } = "";
    public bool IsConnected { get; private set => SetField(ref field, value); }

    /// <summary>A chat message from someone else arrived.</summary>
    public event Action<ChatMessage>? MessageReceived;

    public void Start(string url, string name)
    {
        Stop();
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("ws" or "wss"))
        {
            ConnectionService.Instance.Report(ConnectionState.Disconnected, "ChatLive: no valid server address (ws://...)");
            return;
        }
        _cts = new CancellationTokenSource();
        _ = RunAsync(uri, name, _cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        ConnectionService.Instance.Report(ConnectionState.Disconnected, "ChatLive: disconnected");
    }

    public async Task<bool> SendAsync(string text)
    {
        if (_socket is not { State: WebSocketState.Open } socket || _cts is not { } cts) return false;
        try
        {
            await SendAsync(socket, new { type = "message", text }, cts.Token);
            return true;
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            return false;
        }
    }

    private async Task RunAsync(Uri uri, string name, CancellationToken ct)
    {
        int failures = 0;
        while (!ct.IsCancellationRequested)
        {
            ConnectionService.Instance.Report(
                failures >= 3 ? ConnectionState.Disconnected : ConnectionState.Unstable,
                failures == 0 ? "ChatLive: connecting..." : failures >= 3 ? "ChatLive: server unreachable, retrying" : "ChatLive: reconnecting...");

            using var socket = new ClientWebSocket();
            using var connection = CancellationTokenSource.CreateLinkedTokenSource(ct);
            try
            {
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    timeout.CancelAfter(ConnectTimeout);
                    await socket.ConnectAsync(uri, timeout.Token);
                }
                _socket = socket;
                _pingSentAt = null;
                _latencyMs = 0;
                await SendAsync(socket, new { type = "join", name }, ct);
                failures = 0;
                IsConnected = true;
                UpdateStatus();

                _ = PingLoopAsync(socket, connection.Token);
                await ReceiveLoopAsync(socket, ct);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException)
            {
                // Connection failed or dropped; retried below.
            }
            finally
            {
                connection.Cancel();
                // After a restart (new address) a newer connection may already own the state.
                if (_socket == socket)
                {
                    _socket = null;
                    if (IsConnected) Messages.Add(new ChatMessage("", "Disconnected from ChatLive", DateTime.Now, isSystem: true));
                    IsConnected = false;
                    Users.Clear();
                }
            }

            if (ct.IsCancellationRequested) break;
            failures++;
            try
            {
                await Task.Delay(Backoff[Math.Min(failures - 1, Backoff.Length - 1)], ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task PingLoopAsync(ClientWebSocket socket, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await Task.Delay(PingInterval, ct);
                if (_pingSentAt is { } sent && DateTime.UtcNow - sent > PongTimeout)
                {
                    socket.Abort(); // no answer: treat as dropped so RunAsync reconnects
                    return;
                }
                if (_pingSentAt == null)
                {
                    _pingSentAt = DateTime.UtcNow;
                    await SendAsync(socket, new { type = "ping" }, ct);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException)
        {
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var frame = new MemoryStream();
        while (socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", ct);
                return;
            }
            frame.Write(buffer, 0, result.Count);
            if (frame.Length > MaxFrameBytes) return;
            if (!result.EndOfMessage) continue;

            Handle(Encoding.UTF8.GetString(frame.GetBuffer(), 0, (int)frame.Length));
            frame.SetLength(0);
        }
    }

    private void Handle(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            switch (Str(root, "type"))
            {
                case "welcome":
                    Name = Str(root, "name");
                    break;
                case "history":
                    Messages.Clear();
                    foreach (var item in root.GetProperty("messages").EnumerateArray()) Add(ToMessage(item));
                    break;
                case "message":
                    var message = ToMessage(root);
                    Add(message);
                    if (!message.IsOwn) MessageReceived?.Invoke(message);
                    break;
                case "system":
                    Add(new ChatMessage("", Str(root, "text"), DateTime.Now, isSystem: true));
                    break;
                case "presence":
                    Users.Clear();
                    foreach (var user in root.GetProperty("users").EnumerateArray())
                    {
                        string userName = user.GetString() ?? "";
                        Users.Add(new ChatUser(userName, isYou: userName == Name));
                    }
                    UpdateStatus();
                    break;
                case "pong":
                    if (_pingSentAt is { } sent) _latencyMs = (int)(DateTime.UtcNow - sent).TotalMilliseconds;
                    _pingSentAt = null;
                    UpdateStatus();
                    break;
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            // Malformed frame from the server: ignore it.
        }
    }

    private ChatMessage ToMessage(JsonElement item)
    {
        string name = Str(item, "name");
        string text = Str(item, "text");
        var time = item.TryGetProperty("time", out var t) ? t.GetDateTime().ToLocalTime() : DateTime.Now;
        bool own = name == Name;
        bool mention = !own && Name.Length > 0 && text.Contains("@" + Name, StringComparison.OrdinalIgnoreCase);
        return new ChatMessage(name, text, time, isOwn: own, isMention: mention);
    }

    private void Add(ChatMessage message)
    {
        var previous = Messages.LastOrDefault();
        message.ShowDate = previous == null || previous.Time.Date != message.Time.Date;
        if (!message.ShowDate && !message.IsSystem && previous is { IsSystem: false }
            && previous.Name == message.Name && message.Time - previous.Time < TimeSpan.FromMinutes(5))
        {
            message.ShowHeader = false;
        }
        Messages.Add(message);
        while (Messages.Count > MaxMessages) Messages.RemoveAt(0);
    }

    private void UpdateStatus()
    {
        if (!IsConnected) return;
        if (_latencyMs > SlowLatencyMs)
            ConnectionService.Instance.Report(ConnectionState.Unstable, $"ChatLive: slow connection ({_latencyMs} ms)");
        else
            ConnectionService.Instance.Report(ConnectionState.Connected, $"ChatLive: {Users.Count} online");
    }

    private async Task SendAsync(ClientWebSocket socket, object payload, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        // A WebSocket allows only one send at a time (pings and messages can overlap).
        await _sendLock.WaitAsync(ct);
        try
        {
            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private static string Str(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
}
