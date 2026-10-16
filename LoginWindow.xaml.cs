using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Apathy.Services;

namespace Apathy;

/// <summary>
/// Shown before the menu. Logs in with an apathy.pub account that has premium (or a remembered
/// session token); after that it plays a short loading bar and closes with DialogResult = true.
/// </summary>
public partial class LoginWindow : Window
{
    // Loading bar: text shown while the bar fills up to the given fraction.
    private static readonly (string Step, double Progress, int Milliseconds)[] LoadingSteps =
    [
        ("Verifying", 0.25, 350),
        ("Loading menu", 0.6, 450),
        ("Starting overlay", 0.9, 400),
        ("Ready", 1.0, 250),
    ];

    private readonly CancellationTokenSource _cancel = new();
    private bool _closed;

    public LoginWindow()
    {
        InitializeComponent();

        // Drag from any spot that isn't a control (text boxes and buttons handle their own clicks).
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        };
        SourceInitialized += (_, _) => Native.DisableRoundedCorners(new WindowInteropHelper(this).Handle);
        Loaded += async (_, _) => await ResumeRememberedAsync();
        Closed += (_, _) =>
        {
            _closed = true;
            _cancel.Cancel();
        };

        StartLogoShine();
    }

    /// <summary>The account that logged in (set when DialogResult is true).</summary>
    public Account? Account { get; private set; }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Register_Click(object sender, RoutedEventArgs e) => OpenSite(AuthService.RegisterUrl);

    private void Premium_Click(object sender, RoutedEventArgs e) => OpenSite(AuthService.PremiumUrl);

    private static void OpenSite(Uri url) =>
        Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });

    // A remembered session logs straight in; an expired one just fills in the username.
    private async Task ResumeRememberedAsync()
    {
        if (RememberedLogin.Load() is not { } saved)
        {
            UsernameBox.Focus();
            return;
        }

        UsernameBox.Text = saved.Username;
        RememberBox.IsChecked = true;
        PasswordInput.Focus();
        if (saved.Token is not { } token) return;

        SetBusy(true, $"Logging in as {saved.Username}...");
        var result = await AuthService.ResumeAsync(token, _cancel.Token);
        if (_closed) return;
        if (result.Account is { } account)
        {
            await FinishAsync(account);
            return;
        }

        // Offline: keep the token for next time. Rejected: forget it.
        if (result.Outcome != LoginOutcome.Offline) RememberedLogin.Save(saved.Username, null);
        SetBusy(false, result.Message, error: true);
    }

    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        string username = UsernameBox.Text.Trim();
        string password = PasswordInput.Password;
        if (username.Length == 0 || password.Length == 0)
        {
            ShowStatus("Enter your apathy.pub username and password.", error: true);
            return;
        }

        SetBusy(true, "Logging in...");
        var result = await AuthService.LoginAsync(username, password, _cancel.Token);
        if (_closed) return;

        if (result.Account is not { } account)
        {
            SetBusy(false, result.Message, error: true);
            // Wrong password: retype it. No premium: the account is right, show where to get it.
            if (result.Outcome == LoginOutcome.WrongCredentials)
            {
                PasswordInput.Clear();
                PasswordInput.Focus();
            }
            else if (result.Outcome == LoginOutcome.NoPremium)
            {
                PremiumLink.Focus();
            }
            else if (result.Outcome == LoginOutcome.DeviceLocked)
            {
                // Nothing to retype: the account simply belongs to another PC.
                PasswordInput.Clear();
            }
            return;
        }
        await FinishAsync(account);
    }

    private async Task FinishAsync(Account account)
    {
        if (RememberBox.IsChecked == true) RememberedLogin.Save(account.Username, account.Token);
        else RememberedLogin.Clear();

        Account = account;
        await PlayLoadingAsync(account.Username);
        if (!_closed) DialogResult = true;
    }

    private void SetBusy(bool busy, string message, bool error = false)
    {
        LoginButton.IsEnabled = UsernameBox.IsEnabled = PasswordInput.IsEnabled = !busy;
        ShowStatus(message, error);
    }

    private async Task PlayLoadingAsync(string username)
    {
        WelcomeText.Text = $"Welcome back, {username}";
        LoginGroup.Visibility = Visibility.Hidden;
        LoadingGroup.Visibility = Visibility.Visible;
        LoadingGroup.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));

        foreach (var (step, progress, milliseconds) in LoadingSteps)
        {
            if (_closed) return;
            LoadingStep.Text = step + "...";
            LoadingPercent.Text = (progress * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
            ProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(progress, TimeSpan.FromMilliseconds(milliseconds))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                });
            await Task.Delay(milliseconds + 60);
        }
    }

    /// <summary>A lighter band sweeps across "thy" every few seconds.</summary>
    private void StartLogoShine()
    {
        var accent = ((SolidColorBrush)FindResource("Sk.Accent")).Color;
        var shine = Color.FromRgb(Lighten(accent.R), Lighten(accent.G), Lighten(accent.B));

        // The gradient pads with the accent outside 0..1, so sliding it from -1 to 1 moves the band
        // from fully off the left edge to fully off the right edge.
        var slide = new TranslateTransform(-1, 0);
        LogoAccent.Foreground = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 0),
            RelativeTransform = slide,
            GradientStops =
            {
                new GradientStop(accent, 0.25),
                new GradientStop(shine, 0.5),
                new GradientStop(accent, 0.75),
            },
        };

        var sweep = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
        sweep.KeyFrames.Add(new DiscreteDoubleKeyFrame(-1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        sweep.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.2)),
            new SineEase { EasingMode = EasingMode.EaseInOut }));
        sweep.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(3))));
        slide.BeginAnimation(TranslateTransform.XProperty, sweep);

        static byte Lighten(byte channel) => (byte)(channel + (255 - channel) * 0.8);
    }

    private void ShowStatus(string text, bool error)
    {
        Status.Text = text;
        Status.Foreground = (Brush)FindResource(error ? "Sk.TextError" : "Sk.TextMuted");
        Status.Visibility = Visibility.Visible;
    }
}
