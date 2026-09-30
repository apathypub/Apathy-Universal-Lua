using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Apathy.Services;

namespace Apathy.Overlay;

/// <summary>
/// Base for the small on-screen overlay windows: transparent, always on top, click-through,
/// never activated (so the game keeps focus) and sized to their content. Each one is positioned
/// inside an <see cref="Area"/> (the CS2 window, or the primary screen when CS2 isn't running).
/// Shows over games running in borderless / "Fullscreen Windowed" mode.
/// </summary>
public class OverlayWindow : Window
{
    private Rect _area = Rect.Empty;

    public OverlayWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        IsHitTestVisible = false;
        UseLayoutRounding = true;
        SourceInitialized += (_, _) => Native.MakeOverlay(new WindowInteropHelper(this).Handle);
        SizeChanged += (_, _) => Reposition();
    }

    /// <summary>Region (in WPF units) the overlay is laid out in.</summary>
    protected Rect Area => _area;

    /// <summary>Places the overlay inside <paramref name="pixels"/> (physical screen pixels).</summary>
    public void PlaceIn(Int32Rect pixels)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        PlaceIn(new Rect(pixels.X / dpi.DpiScaleX, pixels.Y / dpi.DpiScaleY,
                         pixels.Width / dpi.DpiScaleX, pixels.Height / dpi.DpiScaleY));
    }

    /// <summary>Places the overlay inside <paramref name="area"/> (WPF units).</summary>
    public void PlaceIn(Rect area)
    {
        if (area == _area) return;
        _area = area;
        Reposition();
    }

    /// <summary>Sets Left/Top from <see cref="Area"/> and the current size.</summary>
    protected virtual void Reposition()
    {
    }
}
