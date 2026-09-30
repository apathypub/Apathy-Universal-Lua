using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Apathy.Services;

namespace Apathy;

/// <summary>
/// Shown before the menu. After a successful login it plays a short loading bar and then
/// closes with DialogResult = true.
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

    private readonly AuthService _auth;
    private bool _closed;

    public LoginWindow(AuthService auth)
    {
        _auth = auth;
        InitializeComponent();

        // Drag from any spot that isn't a control (text boxes and buttons handle their own clicks).
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        };
        SourceInitialized += (_, _) => Native.DisableRoundedCorners(new WindowInteropHelper(this).Handle);
        Loaded += (_, _) => FillRemembered();
        Closed += (_, _) => _closed = true;

        StartLogoShine();
    }

    /// <summary>The account that logged in (set when DialogResult is true).</summary>
    public string? Username { get; private set; }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void FillRemembered()
    {
        if (RememberedLogin.Load() is { } saved)
        {
            UsernameBox.Text = saved.Username;
            PasswordInput.Password = saved.Password;
            RememberBox.IsChecked = true;
            LoginButton.Focus();
        }
        else
        {
            UsernameBox.Focus();
        }
    }

    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        string username = UsernameBox.Text.Trim();
        string password = PasswordInput.Password;
        if (username.Length == 0 || password.Length == 0)
        {
            ShowStatus("Enter your username and password.", error: true);
            return;
        }

        LoginButton.IsEnabled = false;
        ShowStatus("Logging in...", error: false);

        bool ok;
        try
        {
            ok = await Task.Run(() => _auth.Verify(username, password));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            LoginButton.IsEnabled = true;
            ShowStatus("Can't read the account list (users.json).", error: true);
            return;
        }
        if (_closed) return;

        if (!ok)
        {
            LoginButton.IsEnabled = true;
            ShowStatus("Invalid username or password.", error: true);
            PasswordInput.Clear();
            PasswordInput.Focus();
            return;
        }

        if (RememberBox.IsChecked == true) RememberedLogin.Save(username, password);
        else RememberedLogin.Clear();

        Username = username;
        await PlayLoadingAsync(username);
        if (!_closed) DialogResult = true;
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
