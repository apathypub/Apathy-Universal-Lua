using Apathy.Models;

namespace Apathy.Services;

public enum ConnectionState { Disconnected, Unstable, Connected }

/// <summary>
/// Platform connection status shown as the island's dot: green = connected to ChatLive,
/// yellow = connecting / slow connection, red = disconnected. Reported by <see cref="ChatLiveClient"/>.
/// </summary>
public sealed class ConnectionService : ObservableObject
{
    public static ConnectionService Instance { get; } = new();

    private ConnectionService()
    {
    }

    public ConnectionState State { get; private set => SetField(ref field, value); }

    /// <summary>Short description shown in the island tooltip.</summary>
    public string Detail { get; private set => SetField(ref field, value); } = "ChatLive: not connected";

    public void Report(ConnectionState state, string detail)
    {
        State = state;
        Detail = detail;
    }
}
