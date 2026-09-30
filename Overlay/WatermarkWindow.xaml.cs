using System.Globalization;
using Apathy.Services;

namespace Apathy.Overlay;

/// <summary>"apathy | user | time" pill pinned to the top-right corner of the screen.</summary>
public partial class WatermarkWindow : OverlayWindow
{
    private const double EdgeGap = 10;

    public WatermarkWindow()
    {
        InitializeComponent();
    }

    /// <summary>Refreshes the user name and the clock.</summary>
    public void Refresh()
    {
        if (UserText.Text != Session.Username) UserText.Text = Session.Username;
        string time = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        if (Details.Text != time) Details.Text = time;
    }

    protected override void Reposition()
    {
        if (Area.IsEmpty) return;
        Left = Area.Right - ActualWidth - EdgeGap;
        Top = Area.Top + EdgeGap;
    }
}
