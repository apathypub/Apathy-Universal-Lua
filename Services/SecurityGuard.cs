using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace Apathy.Services;

/// <summary>
/// Anti-tamper check run before the loader (the login window) opens. If a debugger is attached, or a
/// known reverse-engineering / debugging tool is running, the loader refuses to start and shows the
/// error "SRE". This protects apathy's licensing from being picked apart; it reads nothing but the
/// names of running processes and the app's own debugger state.
/// </summary>
public static class SecurityGuard
{
    /// <summary>The code shown to the user when the loader won't start.</summary>
    public const string ErrorCode = "SRE";

    // Known reverse-engineering, debugging and memory tools, matched against the process name (no .exe).
    private static readonly string[] BlockedProcesses =
    [
        "ida", "ida64", "ida32", "idaq", "idaq64", "idaw", "idaw64", "idau", "idau64",
        "x64dbg", "x32dbg", "ollydbg", "immunitydebugger", "windbg", "windbgx", "dbgview", "dnspy",
        "ghidra", "radare2", "r2", "cutter", "binaryninja", "binaryninja64", "hopper", "reclass",
        "reclass.net", "reclassex64", "cheatengine", "cheatengine-x86_64", "cheatengine-i386",
        "ilspy", "dotpeek", "dotpeek64", "justdecompile", "reflector", "megadumper", "extremedumper",
        "scylla", "scylla_x64", "scylla_x86", "pe-bear", "pestudio", "lordpe", "importrec",
        "fiddler", "wireshark", "httpdebuggerui", "charles", "processhacker", "procmon", "procmon64",
        "apimonitor", "apimonitor-x64", "apimonitor-x86", "ollyice", "hyperdbg", "titanhide", "kdstinker",
    ];

    /// <summary>True (with a short reason) when the loader must not start.</summary>
    public static bool IsBlocked(out string reason)
    {
        if (Debugger.IsAttached)
        {
            reason = "debugger attached";
            return true;
        }

        if (HasRemoteDebugger())
        {
            reason = "remote debugger";
            return true;
        }

        if (FindBlockedProcess() is { } name)
        {
            reason = name;
            return true;
        }

        reason = "";
        return false;
    }

    private static bool HasRemoteDebugger()
    {
        try
        {
            if (IsDebuggerPresent()) return true;
            using var self = Process.GetCurrentProcess();
            return CheckRemoteDebuggerPresent(self.Handle, out bool present) && present;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException
                                       or InvalidOperationException or Win32Exception)
        {
            // Can't tell (non-Windows or the API is missing): don't block on this check alone.
            return false;
        }
    }

    private static string? FindBlockedProcess()
    {
        var blocked = new HashSet<string>(BlockedProcesses, StringComparer.OrdinalIgnoreCase);
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return null;
        }

        try
        {
            foreach (var process in processes)
            {
                string name;
                try
                {
                    name = process.ProcessName;
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                {
                    continue; // the process ended between listing and reading it
                }
                if (blocked.Contains(name)) return name;
            }
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
        return null;
    }

    private static DispatcherTimer? _monitor;

    /// <summary>
    /// After login, keeps watching: if a blocked tool shows up while apathy runs, it closes with the
    /// same SRE error. Checked every 10 seconds.
    /// </summary>
    public static void StartMonitoring()
    {
        if (_monitor != null) return;
        _monitor = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(10) };
        _monitor.Tick += (_, _) =>
        {
            if (!IsBlocked(out string reason)) return;
            _monitor!.Stop();
            LuaLog.Write($"[security] closing: {reason}");
            MessageBox.Show($"Error: {ErrorCode}\n\napathy closed because debugging or reverse-engineering software was detected.",
                "apathy", MessageBoxButton.OK, MessageBoxImage.Error);
            Editor.EditorWindow.CloseForExit();
            Application.Current.Shutdown();
        };
        _monitor.Start();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool IsDebuggerPresent();

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool CheckRemoteDebuggerPresent(IntPtr process, out bool present);
}
