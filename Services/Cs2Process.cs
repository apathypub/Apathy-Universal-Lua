using System.Diagnostics;

namespace Apathy.Services;

/// <summary>Whether the CS2 game process is currently running. Only the process list is read; nothing is opened or touched.</summary>
public static class Cs2Process
{
    public const string ProcessName = "cs2";

    public static bool IsRunning
    {
        get
        {
            var processes = Process.GetProcessesByName(ProcessName);
            foreach (var process in processes) process.Dispose();
            return processes.Length > 0;
        }
    }
}
