using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

namespace Byoa.Wallet.Desktop;

/// <summary>
/// A borderless window that sits above the tray, in the bottom-right corner of the working area of the screen the
/// visitor is on (the one with the window in front), like a Windows notification. Several of them stack upwards
/// (<see cref="CornerStack"/>). The visible card is drawn inside a transparent margin that leaves room for its shadow.
/// It can be dragged by any part of it that is not a button or a field; where it was dropped, it stays, and the next
/// windows open there.
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
        // Only a press that nothing inside handled (a button, a field, a link handle their own) starts the drag.
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Bubble);
        PositionChanged += (_, _) => OnPositionChanged();
    }

    bool _dragging;             // the visitor pressed on the window to drag it
    PixelPoint? _placed;        // where the stack last put it: a move to anywhere else is the visitor's

    /// <summary>The visitor dragged this window somewhere: it stays there, and the stack leaves it alone.</summary>
    internal bool Moved { get; private set; }

    /// <summary>The cursor of the parts that drag the window.</summary>
    protected static Cursor DragCursor { get; } = new(StandardCursorType.SizeAll);

    void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Handled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _dragging = true;
        try
        {
            BeginMoveDrag(e);          // returns at once; the window then follows the pointer and reports each new place
        }
        catch (InvalidOperationException)
        {
            _dragging = false;         // the press was not a real one (no button down any more)
            return;
        }

        e.Handled = true;
    }

    /// <summary>A move the stack did not make, after a press on the window: the visitor is dragging it. Its last place is the one remembered.</summary>
    void OnPositionChanged()
    {
        if (!_dragging || Frame() is not { } f) return;
        if (_placed is { } p && Math.Abs(f.X - p.X) <= 3 && Math.Abs(f.Y - p.Y) <= 3) return;
        var first = !Moved;
        Moved = true;
        CornerStack.Remember(this);
        if (first) Dispatcher.UIThread.Post(CornerStack.Relayout, DispatcherPriority.Background);   // the others close up over its place
    }

    /// <summary>Where the window is now, in pixels, as the system has it (null before it has a handle). Away from Windows: from the window's own position and size.</summary>
    internal PixelRect? Frame() => TryGetPlatformHandle() is { } handle
        ? ScreenArea.WindowRect(handle.Handle) ?? (OperatingSystem.IsWindows() ? null : OwnFrame())
        : null;

    PixelRect OwnFrame() => new(Position, new PixelSize(PixelWidth(RenderScaling), PixelHeight(RenderScaling)));

    /// <summary>The window's right and bottom edges (shadow margin included) in pixels.</summary>
    internal PixelPoint RightBottom() =>
        Frame() is { } f ? new PixelPoint(f.Right, f.Bottom) : new PixelPoint(Position.X + PixelWidth(RenderScaling), Position.Y + PixelHeight(RenderScaling));

    /// <summary>The window's right and top edges (shadow margin included) in pixels.</summary>
    internal PixelPoint RightTop() => Frame() is { } f ? new PixelPoint(f.Right, f.Y) : new PixelPoint(Position.X + PixelWidth(RenderScaling), Position.Y);

    int PixelWidth(double scale) => (int)Math.Ceiling(Width * scale);

    int PixelHeight(double scale) => (int)Math.Ceiling(Dips() * scale);

    double Dips() => ClientSize.Height > 0 ? ClientSize.Height : Bounds.Height;

    /// <summary>
    /// Places the window (shadow margin included) with its right edge at <paramref name="right"/> and its bottom
    /// <paramref name="above"/> DIPs above <paramref name="bottom"/> (pixels), kept inside the working area; returns its height.
    /// </summary>
    internal double Place(double above, int right, int bottom, PixelRect area, double scale)
    {
        var width = PixelWidth(scale);
        var height = PixelHeight(scale);
        _placed = Inside(new PixelPoint(right - width, bottom - height - (int)Math.Round(above * scale)), width, height, area, scale);
        Position = _placed.Value;
        return Dips();
    }

    /// <summary>
    /// Like <see cref="Place"/>, from the top (macOS: the notices hang below the menu bar, at the right): the window (shadow margin included) has its right
    /// edge at <paramref name="right"/> and its top <paramref name="below"/> DIPs below <paramref name="top"/> (pixels); returns its height.
    /// </summary>
    internal double PlaceFromTop(double below, int right, int top, PixelRect area, double scale)
    {
        var width = PixelWidth(scale);
        var height = PixelHeight(scale);
        _placed = Inside(new PixelPoint(right - width, top + (int)Math.Round(below * scale)), width, height, area, scale);
        Position = _placed.Value;
        return Dips();
    }

    /// <summary>A window the visitor moved keeps its place; only a change of the screen or of its size can push it back inside.</summary>
    internal void KeepInside(PixelRect area, double scale)
    {
        if (Frame() is not { } f) return;
        var p = Inside(f.Position, f.Width, f.Height, area, scale);
        if (p == f.Position) return;
        _placed = p;
        Position = p;
    }

    /// <summary>The card (not its shadow margin) stays inside the working area.</summary>
    static PixelPoint Inside(PixelPoint p, int width, int height, PixelRect area, double scale)
    {
        var margin = (int)Math.Round(ShadowMargin * scale);
        var x = Math.Max(area.X - margin, Math.Min(p.X, area.Right - width + margin));
        var y = Math.Max(area.Y - margin, Math.Min(p.Y, area.Bottom - height + margin));
        return new PixelPoint(x, y);
    }

    /// <summary>The height of the working area in DIPs (for limiting tall windows).</summary>
    protected double WorkingHeight
    {
        get
        {
            var (area, scale) = ScreenArea.Current(this);
            return area.Height / scale;
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
    static PixelPoint? _dropped;        // the bottom-right of the card the visitor last moved: the next ones open there

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

    /// <summary>On macOS the stack hangs from the top right (below the menu bar), as the system's own notices do; on Windows it rises from the bottom right, above the taskbar.</summary>
    static bool FromTop => OperatingSystem.IsMacOS();

    /// <summary>The visitor dropped <paramref name="w"/> somewhere: later windows start from that corner.</summary>
    public static void Remember(CornerWindow w) => _dropped = FromTop ? w.RightTop() : w.RightBottom();

    public static void Relayout()
    {
        var shown = Open.Where(w => w.IsVisible).ToList();
        if (shown.Count == 0) return;
        // The screen is asked every time: a wallet that runs for days sees the screen as it is now, not as it was at start.
        var (area, scale) = ScreenArea.Current(shown[0]);
        foreach (var w in shown.Where(w => w.Moved)) w.KeepInside(area, scale);

        // The first card's bottom sits 12 DIPs above the taskbar (or where the visitor last dropped a card); each next card
        // 8 DIPs above the one below it. A card the visitor moved is left where it is and does not count.
        var fromDropped = _dropped is not null && shown.All(w => !w.Moved);
        if (FromTop)
        {
            // The first card's top sits 12 DIPs below the menu bar (or where the visitor last dropped a card); each next card 8 DIPs below the one above it.
            var topRight = fromDropped ? _dropped!.Value.X : area.Right + (int)Math.Round((CornerWindow.ShadowMargin - 12) * scale);
            var top = fromDropped ? _dropped!.Value.Y : area.Y;
            var below = fromDropped ? 0 : 12 - CornerWindow.ShadowMargin;
            foreach (var w in shown.Where(w => !w.Moved))
                below += w.PlaceFromTop(below, topRight, top, area, scale) - 2 * CornerWindow.ShadowMargin + 8;
            return;
        }

        var right = fromDropped ? _dropped!.Value.X : area.Right + (int)Math.Round((CornerWindow.ShadowMargin - 12) * scale);
        var bottom = fromDropped ? _dropped!.Value.Y : area.Bottom;
        var above = fromDropped ? 0 : 12 - CornerWindow.ShadowMargin;
        foreach (var w in shown.Where(w => !w.Moved))
            above += w.Place(above, right, bottom, area, scale) - 2 * CornerWindow.ShadowMargin + 8;
    }
}

/// <summary>
/// The working area (screen minus taskbar) and scale of the screen the visitor is on, asked from Windows each time: the
/// monitor of the window in front, or the primary one. Avalonia keeps its own list of screens and refreshes it only when it
/// is told the displays changed, which a tray app that sits hidden for hours may miss; its list is the fallback.
/// </summary>
static class ScreenArea
{
    public static (PixelRect Area, double Scale) Current(Window w)
    {
        try
        {
            var monitor = MonitorFromWindow(GetForegroundWindow(), 1);   // 1: the primary monitor when there is no window in front
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
            {
                var work = info.Work;
                var dpi = GetDpiForMonitor(monitor, 0, out var x, out _) == 0 && x > 0 ? x : 96u;
                if (work.Right > work.Left && work.Bottom > work.Top)
                    return (new PixelRect(work.Left, work.Top, work.Right - work.Left, work.Bottom - work.Top), dpi / 96.0);
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException) { /* the fallback below */ }

        var screen = w.Screens.Primary ?? w.Screens.ScreenFromWindow(w);
        return screen is null ? (new PixelRect(0, 0, 1920, 1040), 1.0) : (screen.WorkingArea, screen.Scaling);
    }

    /// <summary>A window's rectangle in pixels, from Windows; null when it cannot be read.</summary>
    public static PixelRect? WindowRect(IntPtr window)
    {
        try
        {
            return GetWindowRect(window, out var r) && r.Right > r.Left && r.Bottom > r.Top
                ? new PixelRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top)
                : null;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct MonitorInfo { public int Size; public Rect Monitor; public Rect Work; public uint Flags; }

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
}
