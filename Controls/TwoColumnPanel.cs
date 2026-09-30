using System.Windows;
using System.Windows.Controls;

namespace Apathy.Controls;

/// <summary>
/// Lays children out in two equal columns, each child going under whichever column is shorter,
/// so group boxes of different heights stack without gaps (same 18 px spacing as the menu pages).
/// </summary>
public sealed class TwoColumnPanel : Panel
{
    private const double Gap = 18;

    protected override Size MeasureOverride(Size availableSize)
    {
        double columnWidth = Math.Max(0, (availableSize.Width - Gap) / 2);
        double[] heights = [0, 0];
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(columnWidth, double.PositiveInfinity));
            Place(heights, child.DesiredSize.Height);
        }
        return new Size(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width, Math.Max(heights[0], heights[1]));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double columnWidth = Math.Max(0, (finalSize.Width - Gap) / 2);
        double[] heights = [0, 0];
        foreach (UIElement child in InternalChildren)
        {
            var (column, top) = Place(heights, child.DesiredSize.Height);
            child.Arrange(new Rect(column * (columnWidth + Gap), top, columnWidth, child.DesiredSize.Height));
        }
        return finalSize;
    }

    /// <summary>Puts a child of <paramref name="height"/> under the shorter column; returns where it goes.</summary>
    private static (int Column, double Top) Place(double[] heights, double height)
    {
        int column = heights[0] <= heights[1] ? 0 : 1;
        double top = heights[column] > 0 ? heights[column] + Gap : 0;
        heights[column] = top + height;
        return (column, top);
    }
}
