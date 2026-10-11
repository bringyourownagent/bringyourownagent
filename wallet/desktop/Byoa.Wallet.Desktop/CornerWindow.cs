using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Byoa.Wallet.Desktop;

/// <summary>
/// A borderless window that sits above the tray, in the bottom-right corner of the primary screen's working area,
/// like a Windows notification. Several of them stack upwards (<see cref="CornerStack"/>).
/// The visible card is drawn inside a transparent margin that leaves room for its shadow.
/// </summary>
abstract class CornerWindow : Window
{
    public const double ShadowMargin = 18;

    protected CornerWindow(double cardWidth)
    {
        SystemDecorations = SystemDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = cardWidth + 2 * ShadowMargin;
        Opacity = 0;
        Opened += (_, _) =>
        {
            CornerStack.Add(this);
            Opacity = 1;
        };
        Closed += (_, _) => CornerStack.Remove(this);
        this.GetObservable(ClientSizeProperty).Subscribe(new Observer(() => CornerStack.Relayout()));
    }

    /// <summary>Places the window (shadow margin included) with its bottom <paramref name="above"/> DIPs above the working area's bottom; returns its height.</summary>
    internal double Place(double above)
    {
        var screen = Screens.Primary ?? Screens.ScreenFromWindow(this);
        if (screen is null) return 0;
        var scale = screen.Scaling;
        var wa = screen.WorkingArea;
        var width = (int)Math.Ceiling(Width * scale);
        var dips = ClientSize.Height > 0 ? ClientSize.Height : Bounds.Height;
        var height = (int)Math.Ceiling(dips * scale);
        // the card's right edge 12 DIPs from the screen edge
        Position = new PixelPoint(wa.Right - width + (int)((ShadowMargin - 12) * scale), wa.Bottom - height - (int)Math.Round(above * scale));
        return dips;
    }

    /// <summary>The height of the primary screen's working area in DIPs (for limiting tall windows).</summary>
    protected double WorkingHeight
    {
        get
        {
            var screen = Screens.Primary;
            return screen is null ? 800 : screen.WorkingArea.Height / screen.Scaling;
        }
    }

    sealed class Observer(Action onNext) : IObserver<Size>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void OnNext(Size value) => onNext();
    }
}

/// <summary>Keeps the corner windows stacked above the tray in the order they opened.</summary>
static class CornerStack
{
    static readonly List<CornerWindow> Open = [];

    public static void Add(CornerWindow w)
    {
        if (!Open.Contains(w)) Open.Add(w);
        Relayout();
    }

    public static void Remove(CornerWindow w)
    {
        Open.Remove(w);
        Relayout();
    }

    public static void Relayout()
    {
        // The first card's bottom sits 12 DIPs above the taskbar; each next card 8 DIPs above the one below it.
        var above = 12 - CornerWindow.ShadowMargin;
        foreach (var w in Open.ToList())
        {
            if (!w.IsVisible) continue;
            above += w.Place(above) - 2 * CornerWindow.ShadowMargin + 8;
        }
    }
}
