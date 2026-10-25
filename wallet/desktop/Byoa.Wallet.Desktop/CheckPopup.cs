using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Byoa.Wallet.Everywhere;

namespace Byoa.Wallet.Desktop;

/// <summary>
/// The window of the check of selected text, near the cursor: a row of action buttons while the visitor chooses, "Working…" while the model answers,
/// then the answer with Copy, Replace selection and Close (and the other actions, to try another one on the same text), or a plain message. It is the
/// <see cref="ICheckPopup"/> the controller draws on, and it calls the controller back when a button is pressed. Called from any thread; everything
/// is drawn on the UI thread. Esc closes it.
/// </summary>
[SupportedOSPlatform("windows")]
sealed class CheckPopup : ICheckPopup
{
    CheckPopupWindow? _window;

    /// <summary>The controller whose popup this is; set once, right after the controller is made (they need each other).</summary>
    public SelectionCheckController? Controller { get; set; }

    public void Show(CheckView view) => DesktopUi.PostSafe(() =>
    {
        if (_window is null)
        {
            _window = new CheckPopupWindow(this);
            _window.Closed += (_, _) =>
            {
                _window = null;
                // closed by the visitor (Alt+F4, Esc): the controller forgets the check; when it closed the window itself, this finds nothing to do
                _ = Controller?.CloseAsync();
            };
            _window.Draw(view);
            _window.PlaceNearCursor();
            _window.Show();
            _window.Activate();
        }
        else
        {
            _window.Draw(view);
        }
    });

    public void Close() => DesktopUi.PostSafe(() =>
    {
        var window = _window;
        _window = null;
        window?.Close();
    });

    sealed class CheckPopupWindow : Window
    {
        const double CardWidth = 440, ShadowRoom = 18;
        readonly CheckPopup _owner;
        readonly Border _card = new() { Padding = new Thickness(16, 14, 16, 14), Margin = new Thickness(ShadowRoom), Background = Brushes.White };
        bool _placed;

        public CheckPopupWindow(CheckPopup owner)
        {
            _owner = owner;
            Title = "Agent wallet: check";
            SystemDecorations = SystemDecorations.None;
            Background = Brushes.Transparent;
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
            Topmost = true;
            ShowInTaskbar = false;
            CanResize = false;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Width = CardWidth + 2 * ShadowRoom;
            _card.Classes.Add("popup");
            Content = _card;
            AutomationProperties.SetAutomationId(this, "CheckPopup");
            KeyDown += (_, e) =>
            {
                if (e.Key != Key.Escape) return;
                e.Handled = true;
                Close();
            };
            // Only a press that nothing inside handled (a button, the answer's text) starts a drag.
            PointerPressed += (_, e) =>
            {
                if (e.Handled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
                try { BeginMoveDrag(e); }
                catch (InvalidOperationException) { return; }
                e.Handled = true;
            };
            this.GetObservable(ClientSizeProperty).Subscribe(new SizeObserver(KeepOnScreen));
        }

        public void Draw(CheckView view)
        {
            var body = Ui.Column(10);
            var controller = _owner.Controller;
            switch (view.Phase)
            {
                case CheckPhase.Choosing:
                    body.Children.Add(Ui.Text("Check the selected text", "strong"));
                    body.Children.Add(Ui.Wrap("Choose what to do with it. Press the hotkey again to run the first one.", "muted", "small"));
                    body.Children.Add(Actions(view, controller, "allow"));
                    body.Children.Add(Ui.Row(8, Close_()));
                    break;
                case CheckPhase.Working:
                    var bar = new ProgressBar { IsIndeterminate = true };
                    bar.Classes.Add("meter");
                    body.Children.Add(Ui.Text("“" + view.Running + "”…", "strong"));
                    body.Children.Add(Ui.Wrap("Your own model is working on it.", "muted", "small"));
                    body.Children.Add(bar);
                    body.Children.Add(Ui.Row(8, Close_("Cancel")));
                    break;
                case CheckPhase.Answer:
                    body.Children.Add(Ui.Text("“" + view.Running + "”", "muted", "small"));
                    var answer = new TextBox
                    {
                        Text = view.Text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, MaxHeight = 260, MinHeight = 60,
                    };
                    AutomationProperties.SetAutomationId(answer, "CheckAnswer");
                    AutomationProperties.SetName(answer, "The answer");
                    body.Children.Add(answer);
                    var copy = Ui.Button("Copy", "allow", "CheckCopy", () => _ = controller?.CopyAsync(), "small");
                    var replace = Ui.Button("Replace selection", "deny", "CheckReplace", () => _ = controller?.ReplaceAsync(), "small");
                    replace.IsEnabled = view.CanReplace;
                    body.Children.Add(Ui.Row(8, copy, replace, Close_()));
                    if (view.Actions.Count > 1)
                    {
                        body.Children.Add(Ui.Text("Try another action on the same text", "muted", "small"));
                        body.Children.Add(Actions(view, controller, "deny"));
                    }

                    break;
                default:
                    var message = Ui.Wrap(view.Text ?? "", "wrap");
                    AutomationProperties.SetAutomationId(message, "CheckMessage");
                    AutomationProperties.SetName(message, view.Text ?? "");
                    body.Children.Add(message);
                    body.Children.Add(Ui.Row(8, Close_()));
                    break;
            }

            if (!string.IsNullOrEmpty(view.Status))
            {
                var status = Ui.Wrap(view.Status, "muted", "small");
                AutomationProperties.SetAutomationId(status, "CheckStatus");
                AutomationProperties.SetName(status, view.Status);
                body.Children.Add(status);
            }

            _card.Child = body;
            if (_placed) Dispatcher.UIThread.Post(KeepOnScreen, DispatcherPriority.Background);
        }

        Control Actions(CheckView view, SelectionCheckController? controller, string kind)
        {
            var panel = new WrapPanel { Orientation = Orientation.Horizontal };
            for (var i = 0; i < view.Actions.Count; i++)
            {
                var index = i;
                var name = view.Actions[i];
                var button = Ui.Button(name, kind, "CheckAction-" + i, () => _ = controller?.ChooseAsync(index), "small");
                button.Margin = new Thickness(0, 0, 8, 6);
                panel.Children.Add(button);
            }

            return panel;
        }

        Button Close_(string text = "Close") => Ui.Button(text, "deny", "CheckClose", Close, "small");

        /// <summary>Opens a little below and to the right of the cursor, on the screen the cursor is on.</summary>
        public void PlaceNearCursor()
        {
            if (!GetCursorPos(out var cursor)) return;
            Position = new PixelPoint(cursor.X + 10 - (int)ShadowRoom, cursor.Y + 18 - (int)ShadowRoom);
            _placed = true;
            Dispatcher.UIThread.Post(KeepOnScreen, DispatcherPriority.Background);
        }

        /// <summary>The card stays inside the working area of the screen it is on, whatever its height has become.</summary>
        void KeepOnScreen()
        {
            if (!_placed || TryGetPlatformHandle() is not { } handle || !GetWindowRect(handle.Handle, out var rect)) return;
            var monitor = MonitorFromWindow(handle.Handle, 2);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return;
            var margin = (int)Math.Round(ShadowRoom * RenderScaling);
            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            var x = Math.Max(info.Work.Left - margin, Math.Min(rect.Left, info.Work.Right - width + margin));
            var y = Math.Max(info.Work.Top - margin, Math.Min(rect.Top, info.Work.Bottom - height + margin));
            if (x != rect.Left || y != rect.Top) Position = new PixelPoint(x, y);
        }

        sealed class SizeObserver(Action onNext) : IObserver<Size>
        {
            public void OnCompleted() { }
            public void OnError(Exception error) { }
            public void OnNext(Size value) => onNext();
        }

        [StructLayout(LayoutKind.Sequential)] struct Point { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] struct MonitorInfo { public int Size; public Rect Monitor; public Rect Work; public uint Flags; }

        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GetCursorPos(out Point point);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GetWindowRect(IntPtr window, out Rect rect);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    }
}
