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

    public SecretDeleteWindow(string sourceName)
    {
        Title = "Delete saved key?";
        Width = 520;
        Height = 270;
        MinWidth = 470;
        MinHeight = 230;
        Background = Brushes.White;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        CanResize = false;
        var delete = Ui.Button("Delete", "allow", "SecretDeleteConfirm", () => Answer(true), "small");
        var keep = Ui.Button("Keep", "deny", "SecretDeleteKeep", () => Answer(false), "small");
        var content = Ui.Column(13,
            Ui.Text("Delete the saved key?", "h1"),
            Ui.Wrap("The agent wants to delete the source " + sourceName + " and its saved key.", "muted"),
            Ui.Wrap("Keep leaves the key in the wallet so you can use it again later.", "muted", "small"),
            Ui.Row(8, delete, keep));
        content.Margin = new Thickness(30, 26, 30, 24);
        Content = content;
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
