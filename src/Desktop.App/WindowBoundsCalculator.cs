using System.Windows;

namespace Desktop.App;

internal static class WindowBoundsCalculator
{
    public static Rect Fit(Rect workArea, Size preferredSize, Size minimumSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(workArea.Width, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(workArea.Height, 0);

        var effectiveMinimumWidth = Math.Min(minimumSize.Width, workArea.Width);
        var effectiveMinimumHeight = Math.Min(minimumSize.Height, workArea.Height);
        var width = Math.Clamp(preferredSize.Width, effectiveMinimumWidth, workArea.Width);
        var height = Math.Clamp(preferredSize.Height, effectiveMinimumHeight, workArea.Height);
        var left = workArea.Left + ((workArea.Width - width) / 2);
        var top = workArea.Top + ((workArea.Height - height) / 2);

        return new Rect(left, top, width, height);
    }
}
