using System.Collections.ObjectModel;
using System.Windows;
using Apathy.Overlay;

namespace Apathy.Services;

public enum NotificationKind { Info, Success, Error, Question }

public sealed class Toast(string title, string message, NotificationKind kind, Action<bool>? answer = null)
{
    public string Title { get; } = title;
    public string Message { get; } = message;
    public NotificationKind Kind { get; } = kind;
    public bool IsQuestion => Kind == NotificationKind.Question;
    internal Action<bool>? Answer { get; } = answer;
}

/// <summary>Toasts in the bottom-right corner of the screen (see <see cref="ToastWindow"/>).</summary>
public static class NotificationService
{
    private const int MaxVisible = 4;
    private static readonly TimeSpan InfoLifetime = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan QuestionLifetime = TimeSpan.FromSeconds(30);

    private static ToastWindow? _window;

    public static ObservableCollection<Toast> Items { get; } = [];

    /// <summary>Informational toast; skipped while Notifications is off in Misc, or if an identical one is already up.</summary>
    public static void Show(string title, string message, NotificationKind kind = NotificationKind.Info)
    {
        if (!SettingsStore.Current.Notifications) return;
        // Don't let a script (or a per-tick call) stack the same toast over and over.
        if (Items.Any(t => t.Title == title && t.Message == message && t.Kind == kind)) return;
        Add(new Toast(title, message, kind), InfoLifetime);
    }

    /// <summary>
    /// Asks the user to accept or decline. Always shown, even with Notifications off, because the
    /// caller is waiting for the answer; no answer within 30 s counts as declined.
    /// </summary>
    public static void Ask(string title, string message, Action<bool> answer) =>
        Add(new Toast(title, message, NotificationKind.Question, answer), QuestionLifetime);

    public static void Respond(Toast toast, bool accepted)
    {
        if (!Items.Remove(toast)) return;
        toast.Answer?.Invoke(accepted);
    }

    public static void Dismiss(Toast toast)
    {
        if (toast.IsQuestion) Respond(toast, false);
        else Items.Remove(toast);
    }

    private static async void Add(Toast toast, TimeSpan lifetime)
    {
        var dispatcher = Application.Current.Dispatcher;
        if (!dispatcher.CheckAccess())
        {
            _ = dispatcher.BeginInvoke(() => Add(toast, lifetime));
            return;
        }

        _window ??= new ToastWindow();
        Items.Add(toast);

        // Drop the oldest plain toasts first; pending questions stay until answered.
        while (Items.Count > MaxVisible && Items.FirstOrDefault(t => !t.IsQuestion) is { } oldest)
            Items.Remove(oldest);

        await Task.Delay(lifetime);
        Dismiss(toast);
    }
}
