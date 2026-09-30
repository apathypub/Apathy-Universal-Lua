using System.Diagnostics;
using System.IO;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace Apathy.Services;

/// <summary>
/// Measures the game's frame rate the way MSI Afterburner / PresentMon do: it listens to the
/// system's ETW "present" events for the CS2 process and counts frames per second. This never
/// injects into or reads the memory of the game — it only observes present notifications Windows
/// already emits. The ETW session needs the app to run as administrator; without it, FPS is
/// unavailable and <see cref="CurrentFps"/> stays null.
/// </summary>
public sealed class FpsMonitor : IDisposable
{
    // Microsoft-Windows-DXGI provider; event 42 is a Present call.
    private static readonly Guid DxgiProvider = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    private const int PresentEventId = 42;
    private const string SessionName = "ApathyFpsSession";

    private readonly Func<int> _processId;
    private readonly Lock _lock = new();
    private TraceEventSession? _session;
    private Thread? _thread;
    private long _frames;
    private Stopwatch? _window;

    public FpsMonitor(Func<int> processId) => _processId = processId;

    /// <summary>False when the ETW session couldn't start (e.g. not running as administrator).</summary>
    public bool Available { get; private set; }

    /// <summary>Frames per second of the tracked game, or null when unavailable / no frames yet.</summary>
    public int? CurrentFps
    {
        get
        {
            lock (_lock)
            {
                if (!Available || _window is null) return null;
                double seconds = _window.Elapsed.TotalSeconds;
                if (seconds < 0.5) return null; // not enough sampled yet

                int fps = (int)Math.Round(_frames / seconds);

                // Roll the window each ~second so the reading tracks recent frames.
                if (seconds >= 1.0)
                {
                    _frames = 0;
                    _window.Restart();
                }
                return fps;
            }
        }
    }

    public void Start()
    {
        try
        {
            _session = new TraceEventSession(SessionName) { StopOnDispose = true };
            _session.EnableProvider(DxgiProvider, TraceEventLevel.Informational);
            _session.Source.Dynamic.All += OnEvent;

            _window = Stopwatch.StartNew();
            _thread = new Thread(() => _session.Source.Process())
            {
                IsBackground = true,
                Name = "ApathyFpsTrace",
            };
            _thread.Start();
            Available = true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or TypeInitializationException
                                       or DllNotFoundException or FileNotFoundException or InvalidOperationException)
        {
            // Not elevated, or ETW/TraceEvent unavailable: FPS just stays hidden.
            Available = false;
            Dispose();
        }
    }

    private void OnEvent(TraceEvent data)
    {
        if ((int)data.ID != PresentEventId || data.ProcessID != _processId()) return;
        lock (_lock) _frames++;
    }

    public void Dispose()
    {
        try
        {
            _session?.Dispose(); // stops Source.Process() on the background thread
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // already stopped
        }
        _session = null;
    }
}
