using System.IO;
using System.Text.Json;
using System.Windows;
using Apathy.Services;
using Apathy.Themes;

namespace Apathy;

public partial class App : Application
{
    // Settings (loaded scripts, their options, island position...) are kept here between runs.
    private const string AutosaveConfig = "autosave";

    private bool _menuStarted;

    protected override void OnStartup(StartupEventArgs e)
    {
        ThemeManager.InstallPattern();
        base.OnStartup(e);

        // The loader won't start while a debugger or reverse-engineering tool is running.
        if (SecurityGuard.IsBlocked(out string reason))
        {
            LuaLog.Write($"[security] loader blocked: {reason}");
            MessageBox.Show($"Error: {SecurityGuard.ErrorCode}\n\napathy can't start while debugging or reverse-engineering software is running. Close it and try again.",
                "apathy", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        // Login first; the menu (and with it the input poller, overlay and scripts) only starts afterwards.
        // ShutdownMode is OnExplicitShutdown (App.xaml) so closing the login window doesn't end the app early.
        // Only apathy.pub accounts with premium get past this; SessionGuard keeps checking afterwards.
        var login = new LoginWindow();
        if (login.ShowDialog() != true || login.Account is not { } account)
        {
            Shutdown();
            return;
        }
        Session.Start(account);
        SessionGuard.Start();
        SecurityGuard.StartMonitoring();

        try
        {
            SettingsStore.Replace(ConfigService.Load(AutosaveConfig));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // First run or unreadable file: start with defaults.
        }

        var menu = new MainWindow();
        MainWindow = menu;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        _menuStarted = true;
        menu.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_menuStarted)
        {
            try
            {
                ConfigService.Save(AutosaveConfig, SettingsStore.Current);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Read-only folder: settings just aren't kept.
            }
        }
        base.OnExit(e);
    }
}
