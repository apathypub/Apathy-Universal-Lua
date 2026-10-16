using System.Windows;
using System.Windows.Controls;
using Apathy.Models;
using Apathy.Services;

namespace Apathy.Views;

public partial class MiscPage : UserControl
{
    public MiscPage()
    {
        InitializeComponent();
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        if (ConfirmDialog.Ask(Window.GetWindow(this), "Exit", "Close apathy? Loaded scripts will stop.", "Exit", "Cancel") != true)
            return;
        if (Editor.EditorWindow.CloseForExit()) Application.Current.Shutdown();
    }

    private async void Logout_Click(object sender, RoutedEventArgs e)
    {
        if (ConfirmDialog.Ask(Window.GetWindow(this), "Log out",
                $"Log out of {Session.Username}? apathy will close and ask for your login next time.", "Log out", "Cancel") != true)
            return;
        RememberedLogin.Clear();
        await AuthService.LogoutAsync(Session.Token);
        if (Editor.EditorWindow.CloseForExit()) Application.Current.Shutdown();
    }

    // Turning auto start on asks first; turning it off doesn't.
    private void AutoStart_Click(object sender, RoutedEventArgs e)
    {
        var settings = SettingsStore.Current;
        try
        {
            if (!settings.AutoStart)
            {
                AutoStartService.Disable();
                return;
            }

            bool? yes = ConfirmDialog.Ask(Window.GetWindow(this), "Auto start",
                "Start apathy automatically every time Windows starts?");
            if (yes != true)
            {
                settings.AutoStart = false;
                return;
            }

            AutoStartService.Enable();
            NotificationService.Show("Auto start enabled", "apathy will start together with Windows.", NotificationKind.Success);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException
                                       or System.IO.IOException or InvalidOperationException)
        {
            settings.AutoStart = AutoStartService.IsEnabled;
            NotificationService.Show("Auto start", ex.Message, NotificationKind.Error);
        }
    }

    private void ResetIsland_Click(object sender, RoutedEventArgs e) =>
        (Window.GetWindow(this) as MainWindow)?.ResetIsland();
}
