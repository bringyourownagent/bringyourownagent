using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Media;

namespace Byoa.Wallet.Android;

/// <summary>
/// "Connect by link": a link that did not reach the app through a tap in the browser (a site open on a computer without a wallet shows it, or it
/// came in a message) is pasted here. The wallet checks it, says plainly when it cannot use it, and otherwise goes on exactly as for a link from the
/// browser, with a consent page that says where the link came from (CONTRACT-PAIR.md section 1).
/// </summary>
sealed class ConnectPage : Page
{
    readonly TextBox _text = new()
    {
        Watermark = "bringyourownagent://connect?…",
        AcceptsReturn = true,                       // a link that a messenger wrapped arrives with line breaks; the wallet takes them out
        TextWrapping = TextWrapping.Wrap,
        MinHeight = 110,
        MaxHeight = 220,
    };
    readonly TextBlock _error = Kit.Text("", "small");
    readonly Button _go;

    public ConnectPage(Shell shell) : base(shell)
    {
        Kit.Named(_text, "ConnectByLinkText", "Paste the link a site gave you");
        AutomationProperties.SetAutomationId(_error, "ConnectByLinkError");
        _go = Kit.Button("Connect", "allow", "ConnectByLinkGo", Go);
        _go.IsEnabled = false;
        _text.TextChanged += (_, _) =>
        {
            Say("");
            _go.IsEnabled = !string.IsNullOrWhiteSpace(_text.Text);
        };
    }

    public override string? Title => "Connect by link";

    protected override Control Build()
    {
        var paste = Kit.Button("Paste from the clipboard", "deny", "ConnectByLinkPaste", Paste, "small");
        var scan = Kit.Button("Scan a QR code", "deny", "ConnectByLinkScan", () => W.ScanCode(), "small");
        // the buttons sit right under the box: with the keyboard up (and a long link in the box) they are still on the screen
        var body = Kit.Column(14,
            Kit.Column(6, Kit.Text("Paste the link a site gave you", "label"), _text),
            Kit.Row(10, _go, paste),
            scan,
            _error,
            Kit.Text("Use this when a site is open on a computer that has no wallet, or someone sent you its link. The site shows a link and a short code. " +
                     "Your wallet asks you before it connects, and shows the same code: allow only if the two match.", "muted"));
        body.Margin = new Thickness(16, 16, 16, 24);
        return new ScrollViewer { Content = body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    }

    async void Paste()
    {
        try
        {
            if (TopLevel.GetTopLevel(_text)?.Clipboard is { } clipboard && await clipboard.TryGetTextAsync() is { Length: > 0 } text)
                _text.Text = text;
            else Say("The clipboard has no text.", bad: true);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Say("The wallet could not read the clipboard. Paste into the box by hand.", bad: true);
        }
    }

    void Go()
    {
        if (W.Direct.CheckAndRecordHandedLink(_text.Text) is { } problem)
        {
            Say(problem, bad: true);
            return;
        }

        W.OpenHandedLink(_text.Text);
        _text.Text = "";
        Shell.Back();                               // the consent page comes over the home screen
    }

    void Say(string text, bool bad = false)
    {
        _error.Text = text;
        _error.Foreground = bad ? Kit.Brush("SignalDark") : Kit.Brush("Muted");
    }
}
