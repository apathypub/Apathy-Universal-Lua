using System.Reflection;
using System.Windows.Threading;
using Apathy.Controls;
using Apathy.Models;

namespace Apathy.Services;

/// <summary>
/// Polls the keyboard/mouse globally (GetAsyncKeyState), so hotkeys work while another window
/// such as a fullscreen app has focus, and keys are not swallowed the way RegisterHotKey does.
/// Raises <see cref="MenuKeyPressed"/> and updates <see cref="Keybind.IsActive"/> for every
/// Keybind property on <see cref="MenuSettings"/>, including ones inside nested settings objects
/// (e.g. <c>Enemy.EnabledKey</c>).
/// </summary>
public sealed class InputPoller
{
    private static readonly Dictionary<Type, PropertyInfo[]> SettingsProperties = new();

    private readonly DispatcherTimer _timer;
    private readonly Dictionary<int, bool> _previous = new();
    private readonly Dictionary<int, (bool Down, bool Pressed)> _thisTick = new();

    public InputPoller()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(10) };
        _timer.Tick += (_, _) => Poll();
    }

    public event EventHandler? MenuKeyPressed;

    public void Start() => _timer.Start();

    public void Stop() => _timer.Stop();

    private void Poll()
    {
        // While a keybind is being assigned, forget previous key states so the key that was just
        // bound doesn't count as a fresh press on the next tick.
        if (SkKeybind.IsAnyCapturing)
        {
            _previous.Clear();
            return;
        }

        _thisTick.Clear();
        var settings = SettingsStore.Current;

        if (settings.MenuKey.Key != 0 && Query(settings.MenuKey.Key).Pressed)
            MenuKeyPressed?.Invoke(this, EventArgs.Empty);

        foreach (var bind in Keybinds(settings))
        {
            if (ReferenceEquals(bind, settings.MenuKey)) continue;
            var (down, pressed) = bind.Key != 0 ? Query(bind.Key) : (false, false);
            bind.Update(down, pressed);
        }
    }

    private static IEnumerable<Keybind> Keybinds(ObservableObject settings)
    {
        foreach (var property in PropertiesOf(settings.GetType()))
        {
            switch (property.GetValue(settings))
            {
                case Keybind bind:
                    yield return bind;
                    break;
                case ObservableObject nested:
                    foreach (var nestedBind in Keybinds(nested)) yield return nestedBind;
                    break;
            }
        }
    }

    // Properties that are keybinds or nested settings objects, cached per type.
    private static PropertyInfo[] PropertiesOf(Type type)
    {
        if (!SettingsProperties.TryGetValue(type, out var properties))
        {
            properties = type.GetProperties()
                .Where(p => p.GetIndexParameters().Length == 0 && typeof(ObservableObject).IsAssignableFrom(p.PropertyType))
                .ToArray();
            SettingsProperties[type] = properties;
        }
        return properties;
    }

    private (bool Down, bool Pressed) Query(int virtualKey)
    {
        if (_thisTick.TryGetValue(virtualKey, out var state)) return state;

        bool down = Native.IsKeyDown(virtualKey);
        bool wasDown = _previous.TryGetValue(virtualKey, out var previous) ? previous : down;
        _previous[virtualKey] = down;

        return _thisTick[virtualKey] = (down, down && !wasDown);
    }
}
