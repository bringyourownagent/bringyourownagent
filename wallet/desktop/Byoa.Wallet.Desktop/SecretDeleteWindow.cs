using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Byoa.Wallet.Desktop;

sealed class SecretDeleteWindow : Window
{
    readonly TaskCompletionSource<bool> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    bool _closed;

    public SecretDeleteWindow(string sourceName, Func<int>? waitingCount = null)
    {
        Title = "Delete saved key?";
        Width = 520;
        MinWidth = 470;
        Background = Brushes.White;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        CanResize = false;
        WindowLayout.Prepare(this, 230);
        var delete = Ui.Button("Delete", "allow", "SecretDeleteConfirm", () => Answer(true), "small");
        var keep = Ui.Button("Keep", "deny", "SecretDeleteKeep", () => Answer(false), "small");
        var waiting = waitingCount?.Invoke() is { } count && count > 1
            ? Ui.Wrap((count - 1).ToString() + " more waiting.", "muted", "small")
            : Ui.Text("", "small");
        var body = Ui.Column(13,
            Ui.Text("Delete the saved key?", "h1"),
            Ui.Wrap("The agent wants to delete the source " + sourceName + " and its saved key.", "muted"),
            Ui.Wrap("Keep leaves the key in the wallet so you can use it again later.", "muted", "small"),
            waiting);
        body.Margin = new Thickness(30, 26, 30, 24);
        var buttons = Ui.Row(8, delete, keep);
        buttons.Margin = new Thickness(30, 0, 30, 24);
        Content = Ui.Column(0, WindowLayout.Body(this, body, 92, 230), buttons);
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; Answer(false); }
            else if (e.Key is Key.Enter or Key.Space) e.Handled = true;
        };
        Opened += (_, _) => { Activate(); Focus(); };
        Closed += (_, _) => { _closed = true; _result.TrySetResult(false); };
    }

    public Task<bool> Result => _result.Task;

    void Answer(bool value)
    {
        _result.TrySetResult(value);
        if (!_closed) DesktopUi.PostSafe(Close);
    }
}
