using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Windows;
using Apathy.Models;

namespace Apathy.Services;

public enum LogLevel { Info, Success, Error }

public sealed record LogLine(string Text, LogLevel Level);

/// <summary>
/// Rolling text log shown in the Lua tab (as <see cref="Text"/>) and the editor's Output panel
/// (as colored <see cref="Lines"/>); scripts write to it via print() / log().
/// </summary>
public sealed class LuaLog : ObservableObject
{
    private const int MaxLines = 200;

    public static LuaLog Instance { get; } = new();

    public string Text { get; private set => SetField(ref field, value); } = "";

    public ObservableCollection<LogLine> Lines { get; } = [];

    public static void Write(string message) => Instance.Add(message);

    public void Clear()
    {
        Lines.Clear();
        Text = "";
    }

    private void Add(string message)
    {
        // Marshal to the UI thread so scripts can log from any context.
        if (Application.Current is { } app && !app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.BeginInvoke(() => Add(message));
            return;
        }

        message ??= "";
        var level = Classify(message);
        string stamp = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        foreach (var line in message.Split('\n'))
            Lines.Add(new LogLine($"[{stamp}] {line.TrimEnd('\r')}", level));

        while (Lines.Count > MaxLines) Lines.RemoveAt(0);

        var builder = new StringBuilder();
        foreach (var line in Lines) builder.AppendLine(line.Text);
        Text = builder.ToString();
    }

    // Messages look like "[script] error: ...", "[script] loaded", "[script] cannot read file: ...".
    private static LogLevel Classify(string message) =>
        message.Contains("] error:", StringComparison.Ordinal) || message.Contains("] cannot ", StringComparison.Ordinal)
            ? LogLevel.Error
            : message.EndsWith("] loaded", StringComparison.Ordinal) ? LogLevel.Success : LogLevel.Info;
}
