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

        // Login first; the menu (and with it the input poller, overlay and scripts) only starts afterwards.
        // ShutdownMode is OnExplicitShutdown (App.xaml) so closing the login window doesn't end the app early.
        var auth = new AuthService(AuthService.DefaultPath);
        auth.EnsureSeeded();
        var login = new LoginWindow(auth);
        if (login.ShowDialog() != true)
        {
            Shutdown();
            return;
        }
        Session.Username = login.Username ?? Session.Username;

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
