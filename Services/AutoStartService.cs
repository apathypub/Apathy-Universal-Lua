using Microsoft.Win32;

namespace Apathy.Services;

/// <summary>Starts apathy with Windows via the current user's Run key (no admin rights needed).</summary>
public static class AutoStartService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "apathy";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static void Enable()
    {
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("Can't find apathy's executable path.");
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        key.SetValue(ValueName, $"\"{exe}\"");
    }

    public static void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
