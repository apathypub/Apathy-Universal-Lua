namespace Apathy.Overlay;

/// <summary>Script HUD lines, right-aligned under the watermark.</summary>
public partial class HudWindow : OverlayWindow
{
    private const double EdgeGap = 10;

    public HudWindow()
    {
        InitializeComponent();
    }

    /// <summary>Distance from the top of the area to the top of the HUD (set to sit under the watermark).</summary>
    public double TopOffset { get; set => field = value; } = 40;

    protected override void Reposition()
    {
        if (Area.IsEmpty) return;
        Left = Area.Right - ActualWidth - EdgeGap;
        Top = Area.Top + TopOffset;
    }
}
