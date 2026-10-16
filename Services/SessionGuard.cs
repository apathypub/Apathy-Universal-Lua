using System.Windows;
using System.Windows.Threading;

namespace Apathy.Services;

/// <summary>
/// Re-checks the apathy.pub session every 15 minutes while apathy runs. When the server says the
/// session is gone or premium has ended, apathy tells the user and closes. When apathy.pub can't be
/// reached, it keeps running and tries again at the next check.
/// </summary>
public static class SessionGuard
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);
    private static DispatcherTimer? _timer;
    private static bool _checking;

    public static void Start()
    {
        if (_timer != null) return;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = Interval };
        _timer.Tick += async (_, _) => await CheckAsync();
        _timer.Start();
    }

    private static async Task CheckAsync()
    {
        if (_checking || Session.Token.Length == 0) return;
        _checking = true;
        try
        {
            var result = await AuthService.ResumeAsync(Session.Token);
            if (result.Account is { } account)
            {
                Session.PremiumUntil = account.PremiumUntil;
                return;
            }
            if (result.Outcome is not (LoginOutcome.WrongCredentials or LoginOutcome.NoPremium)) return;

            _timer?.Stop();
            RememberedLogin.Save(Session.Username, null);
            NotificationService.Show("Logged out", result.Message + " apathy will close.", NotificationKind.Error);
            await Task.Delay(TimeSpan.FromSeconds(5));
            Editor.EditorWindow.CloseForExit();
            Application.Current.Shutdown();
        }
        finally
        {
            _checking = false;
        }
    }
}
