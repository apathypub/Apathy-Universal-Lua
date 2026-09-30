using System.Windows;
using System.Windows.Threading;
using Apathy.Services;

namespace Apathy.Overlay;

/// <summary>
/// Drives the on-screen overlay: the watermark (top-right of the primary screen) and the Lua HUD
/// lines under it. Both stay up independently of the menu.
/// </summary>
public sealed class OverlayController : IDisposable
{
    private readonly WatermarkWindow _watermark = new();
    private readonly HudWindow _hud = new();
    private readonly DispatcherTimer _timer;

    public OverlayController()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(50) };
        _timer.Tick += (_, _) => Update();
    }

    public void Start() => _timer.Start();

    public void Dispose()
    {
        _timer.Stop();
        _watermark.Close();
        _hud.Close();
    }

    private void Update()
    {
        var screen = SystemParameters.WorkArea;

        _watermark.Refresh();
        _watermark.PlaceIn(screen);
        SetShown(_watermark, SettingsStore.Current.Watermark);

        // Script HUD lines sit just under the watermark.
        _hud.TopOffset = 10 + (_watermark.ActualHeight > 0 ? _watermark.ActualHeight : 22) + 4;
        _hud.PlaceIn(screen);
        SetShown(_hud, LuaHud.HasVisibleLines);
    }

    private static void SetShown(Window window, bool shown)
    {
        if (shown && !window.IsVisible) window.Show();
        else if (!shown && window.IsVisible) window.Hide();
    }
}
