using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Byoa.Wallet.Everywhere;

namespace Byoa.Wallet.Desktop;

/// <summary>
/// The small floating indicator of dictation: "Listening…", "Writing…", a notice or a plain error. It is a corner window in the style of the wallet's
/// notices, and it never takes the focus (not when it appears, not when it is clicked), or the text would be typed into the indicator instead of
/// the program the visitor is in. Notices and errors go away on their own; the other two stay until <see cref="IIndicator.Hide"/>.
/// </summary>
[SupportedOSPlatform("windows")]
sealed class EverywhereIndicator : IIndicator
{
    static readonly TimeSpan NoticeFor = TimeSpan.FromSeconds(3.5), ErrorFor = TimeSpan.FromSeconds(8);

    IndicatorWindow? _window;
    int _shown;                                  // which showing a pending auto-hide belongs to

    public void Show(IndicatorKind kind, string text)
    {
        var showing = Interlocked.Increment(ref _shown);
        DesktopUi.PostSafe(() =>
        {
            if (showing != Volatile.Read(ref _shown)) return;      // a newer one has been asked for meanwhile
            if (_window is not { IsVisible: true })
            {
                _window = new IndicatorWindow();
                _window.Closed += (_, _) => _window = null;
                _window.Draw(kind, text);
                _window.Show();
            }
            else
            {
                _window.Draw(kind, text);
            }

            if (kind is IndicatorKind.Notice or IndicatorKind.Error)
                DispatcherTimer.RunOnce(() =>
                {
                    if (showing == Volatile.Read(ref _shown)) CloseWindow();
                }, kind == IndicatorKind.Error ? ErrorFor : NoticeFor);
        });
    }

    public void Hide()
    {
        Interlocked.Increment(ref _shown);
        DesktopUi.PostSafe(CloseWindow);
    }

    void CloseWindow()
    {
        var window = _window;
        _window = null;
        window?.Close();
    }

    sealed class IndicatorWindow : CornerWindow
    {
        readonly Border _card = new() { Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(ShadowMargin), Background = Brushes.White, Cursor = DragCursor };

        public IndicatorWindow() : base(260)
        {
            Title = "Agent wallet Everywhere";
            ShowActivated = false;
            _card.Classes.Add("popup");
            _card.Classes.Add("notice");
            Content = _card;
            AutomationProperties.SetAutomationId(this, "EverywhereIndicator");
            // Never take the focus, whatever is clicked: the program the visitor is writing in must keep it.
            Opened += (_, _) =>
            {
                if (TryGetPlatformHandle() is { } handle)
                {
                    var style = GetWindowLong(handle.Handle, GwlExStyle);
                    SetWindowLong(handle.Handle, GwlExStyle, style | WsExNoActivate | WsExToolWindow);
                }
            };
        }

        public void Draw(IndicatorKind kind, string text)
        {
            var (glyph, tint, hint) = kind switch
            {
                IndicatorKind.Listening => (SettingsWindow.MicrophoneGlyph, "SignalDeep", "Esc cancels"),
                IndicatorKind.Writing => (Ui.List, "Sign", "Esc cancels"),
                IndicatorKind.Error => (Ui.Cross, "SignalDeep", ""),
                _ => (Ui.Check, "Sign", ""),
            };
            var label = Ui.Wrap(text, "strong");
            AutomationProperties.SetAutomationId(label, "EverywhereIndicatorText");
            AutomationProperties.SetName(label, text);
            var lines = Ui.Column(2, label);
            if (hint.Length > 0) lines.Children.Add(Ui.Text(hint, "muted", "small"));
            var icon = Ui.Tile(glyph, 26, tint);
            icon.Margin = new Thickness(0, 0, 10, 0);
            icon.VerticalAlignment = VerticalAlignment.Top;
            _card.Child = Ui.Row(0, icon, lines);
            AutomationProperties.SetName(this, "Everywhere: " + text);
        }

        const int GwlExStyle = -20;
        const int WsExNoActivate = 0x08000000, WsExToolWindow = 0x00000080;

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] static extern int SetWindowLong(IntPtr window, int index, int value);
    }
}
