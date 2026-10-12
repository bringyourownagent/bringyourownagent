using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Byoa.Wallet.Desktop;

/// <summary>A wallet-owned key field. Its result is consumed immediately by DesktopUi and never sent over the pipe.</summary>
sealed class ApiKeyEntryWindow : Window
{
    readonly TextBox _key = new() { PasswordChar = '•', Watermark = "Paste the key here; it will not be shown again", MinWidth = 380 };
    readonly TaskCompletionSource<string?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    bool _closed;

    public ApiKeyEntryWindow(string sourceName)
    {
        Title = "Enter an API key";
        Width = 560;
        Height = 300;
        MinWidth = 500;
        MinHeight = 260;
        Background = Brushes.White;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        CanResize = false;

        var save = Ui.Button("Save key", "allow", "ApiKeySave", Save, "small");
        var cancel = Ui.Button("Cancel", "deny", "ApiKeyCancel", Cancel, "small");
        AutomationProperties.SetName(_key, "API key for " + sourceName);
        AutomationProperties.SetAutomationId(_key, "ApiKeyEntry");
        Content = Ui.Column(13,
            Ui.Text("Enter your API key", "h1"),
            Ui.Wrap("The key is saved in this wallet for " + sourceName + ". Your AI agent will only see that it was saved; it cannot read the key.", "muted"),
            Ui.Text("API key", "label"),
            _key,
            Ui.Wrap("Prefer this field over pasting a key into your AI agent.", "muted", "small"),
            Ui.Row(8, save, cancel));
        ((Control)Content).Margin = new Thickness(30, 26, 30, 24);

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Cancel();
            }
        };
        Opened += (_, _) =>
        {
            Activate();
            _key.Focus(NavigationMethod.Tab);
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _key.Text = "";
            _result.TrySetResult(null);
        };
    }

    public Task<string?> Result => _result.Task;

    void Save()
    {
        if (string.IsNullOrWhiteSpace(_key.Text)) return;
        _result.TrySetResult(_key.Text);
        CloseIfOpen();
    }

    void Cancel()
    {
        _result.TrySetResult(null);
        CloseIfOpen();
    }

    void CloseIfOpen()
    {
        if (!_closed) DesktopUi.PostSafe(Close);
    }
}
