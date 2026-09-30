using Apathy.Models;

namespace Apathy.Editor;

public enum ChatEntryKind { User, Ai, Action, Error }

/// <summary>
/// One row in the AI panel: the user's message, the AI's text, a file action ("Created x.lua",
/// click to open <see cref="FilePath"/>) or an error.
/// </summary>
public sealed class ChatEntry(ChatEntryKind kind, string text = "", string? filePath = null) : ObservableObject
{
    public ChatEntryKind Kind { get; } = kind;
    public string? FilePath { get; } = filePath;
    public string Text { get; set => SetField(ref field, value); } = text;
}
