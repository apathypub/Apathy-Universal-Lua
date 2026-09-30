using System.Diagnostics;
using System.Windows;

namespace Apathy.Services;

/// <summary>
/// Tracks the CS2 game window so overlays can be placed inside it at any resolution or window mode.
/// Only the window's position/size is queried; the game process itself isn't touched.
/// </summary>
public sealed class Cs2Window
{
    private const string ProcessName = "cs2";
    private static readonly TimeSpan LookupInterval = TimeSpan.FromSeconds(1);

    private IntPtr _handle;
    private int _processId;
    private DateTime _nextLookup;

    /// <summary>True when the window exists but is minimized (overlays should hide).</summary>
    public bool IsMinimized { get; private set; }

    /// <summary>True while CS2 is the focused (foreground) window.</summary>
    public bool IsForeground => _processId != 0 && Native.ForegroundProcessId() == _processId;

    /// <summary>The CS2 window (as found by the last <see cref="ClientBounds"/> call), or zero.</summary>
    public IntPtr Handle => Native.WindowExists(_handle) ? _handle : IntPtr.Zero;

    /// <summary>Process id of the tracked CS2 window, or 0.</summary>
    public int ProcessId => Native.WindowExists(_handle) ? _processId : 0;

    /// <summary>Client area in physical screen pixels, or null when CS2 isn't running or is minimized.</summary>
    public Int32Rect? ClientBounds()
    {
        if (!Native.WindowExists(_handle) && DateTime.UtcNow >= _nextLookup)
        {
            _nextLookup = DateTime.UtcNow + LookupInterval;
            (_handle, _processId) = FindWindow();
        }

        IsMinimized = Native.WindowExists(_handle) && Native.IsMinimized(_handle);
        if (!Native.WindowExists(_handle) || IsMinimized) return null;

        return Native.ClientBoundsOnScreen(_handle) is { Width: > 0, Height: > 0 } bounds ? bounds : null;
    }

    private static (IntPtr Handle, int ProcessId) FindWindow()
    {
        foreach (var process in Process.GetProcessesByName(ProcessName))
        {
            using (process)
            {
                if (process.MainWindowHandle != IntPtr.Zero) return (process.MainWindowHandle, process.Id);
            }
        }
        return (IntPtr.Zero, 0);
    }
}
