using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;

namespace Byoa.Wallet.Desktop;

/// <summary>Shared layout rules for wallet prompts: content may scroll, but the decision row never does.</summary>
static class WindowLayout
{
    internal static double MaxHeight(Window window, double minimum = 360)
    {
        var screen = window.Screens.Primary ?? window.Screens.ScreenFromWindow(window);
        var workingHeight = screen is null || screen.Scaling <= 0
            ? 800
            : screen.WorkingArea.Height / screen.Scaling;
        return CalculateMaxHeight(workingHeight, minimum);
    }

    internal static double CalculateMaxHeight(double workingHeight, double minimum = 360) =>
        Math.Max(minimum, workingHeight * 0.85);

    internal static ScrollViewer Body(Window window, Control content, double footerHeight, double minimum = 360)
    {
        return new ScrollViewer
        {
            Content = content,
            MaxHeight = Math.Max(180, MaxHeight(window, minimum) - footerHeight),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Top,
        };
    }

    internal static void Prepare(Window window, double minimum = 360)
    {
        window.SizeToContent = SizeToContent.Height;
        window.MinHeight = minimum;
        window.MaxHeight = MaxHeight(window, minimum);
        window.VerticalContentAlignment = VerticalAlignment.Top;
    }
}
