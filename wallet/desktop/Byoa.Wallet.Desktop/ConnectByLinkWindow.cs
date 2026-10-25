using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;

namespace Byoa.Wallet.Desktop;

/// <summary>
/// "Connect a site by link": a link that did not arrive through the browser on this computer, because a site open on a phone (or on a
/// computer with no wallet) gave it to the visitor. He pastes it; the wallet checks it, tells him plainly if it cannot use it, and otherwise
/// goes on exactly as for a link from the browser, with a consent window that says where the link came from (CONTRACT-PAIR.md section 1).
/// </summary>
sealed class ConnectByLinkWindow : Window
{
    readonly WalletServices _w;
    readonly TextBox _text = new()
    {
        Watermark = "bringyourownagent://connect?…",
        AcceptsReturn = true,                   // a link that a messenger wrapped arrives with line breaks; the wallet takes them out
        TextWrapping = TextWrapping.Wrap,
        MinWidth = 380,
        MinHeight = 84,
        MaxHeight = 160,
    };
    readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap };
    readonly Button _go;

    public ConnectByLinkWindow(WalletServices services)
    {
        _w = services;
        Title = "Connect a site by link";
        Width = 520;
        MinWidth = 480;
        Background = Brushes.White;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        CanResize = false;
        WindowLayout.Prepare(this, 240);

        AutomationProperties.SetAutomationId(_text, "ConnectByLinkText");
        AutomationProperties.SetName(_text, "Paste the link a site gave you");
        AutomationProperties.SetAutomationId(_error, "ConnectByLinkError");
        _error.Classes.Add("small");
        _go = Ui.Button("Connect", "allow", "ConnectByLinkGo", Go, "small");
        _go.IsEnabled = false;
        _text.TextChanged += (_, _) =>
        {
            Say("");
            _go.IsEnabled = !string.IsNullOrWhiteSpace(_text.Text);
        };

        var cancel = Ui.Button("Cancel", "deny", "ConnectByLinkCancel", Close, "small");

        var body = Ui.Column(12,
            Ui.Text("Connect a site by link", "h1"),
            Ui.Column(4, Ui.Text("Paste the link a site gave you", "label"), _text),
            Ui.Wrap("Use this when a site is open on your phone, or on a computer that has no wallet. The site shows a link and a short code. " +
                    "Your wallet asks you before it connects, and shows the same code: allow only if the two match.", "muted", "small"),
            _error);
        body.Margin = new Thickness(28, 24, 28, 16);
        var buttons = Ui.Row(8, _go, cancel);
        buttons.Margin = new Thickness(28, 0, 28, 22);
        Content = Ui.Column(0, WindowLayout.Body(this, body, 90, 240), buttons);
        Opened += (_, _) => _text.Focus();
    }

    void Go()
    {
        if (_w.Direct.CheckHandedLink(_text.Text) is { } problem)
        {
            Say(problem, bad: true);
            return;
        }

        _w.OpenHandedLink(_text.Text);
        Close();
    }

    void Say(string text, bool bad = false)
    {
        _error.Text = text;
        _error.Foreground = bad ? Ui.Brush("SignalDeep") : Ui.Brush("Ink2");
    }
}
