using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Byoa.Wallet.Registry;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Desktop;

/// <summary>Confirms the first use of an agent-supplied endpoint outside this computer.</summary>
sealed class EndpointConfirmationWindow : Window
{
    readonly TaskCompletionSource<bool> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    bool _closed;

    public EndpointConfirmationWindow(EndpointConfirmationRequest request, Func<int>? waitingCount = null)
    {
        Title = request.IsLoopback ? "Confirm local runtime" : "Confirm another computer";
        Width = 560;
        MinWidth = 500;
        Background = Brushes.White;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        CanResize = false;
        ShowActivated = true;
        WindowLayout.Prepare(this, 300);

        var agentName = SourceLabels.Clean(request.AgentName) ?? "your AI agent";
        var endpoint = request.CanonicalEndpoint;
        var parsedEndpoint = Uri.TryCreate(endpoint, UriKind.Absolute, out var parsed) ? parsed : null;
        var host = parsedEndpoint?.DnsSafeHost ?? endpoint;
        if (host.Contains(':', StringComparison.Ordinal) && !host.StartsWith("[", StringComparison.Ordinal)) host = "[" + host + "]";
        var port = parsedEndpoint is null || parsedEndpoint.IsDefaultPort
            ? parsedEndpoint?.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) == true ? 443 : 80
            : parsedEndpoint.Port;
        var canonicalHost = host + ":" + port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var encrypted = parsedEndpoint?.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) == true;
        var warning = request.IsLoopback
            ? "This runtime is on this computer."
            : "This is another computer. The connection is " + (encrypted ? "encrypted." : "not encrypted.");
        Control addressText = request.ResolvedAddresses.Count > 0
            ? Ui.Column(4, Ui.Text("Resolved addresses", "label"), Ui.Box("field", Ui.Wrap(string.Join(", ", request.ResolvedAddresses), "mono", "small")))
            : Ui.Wrap("The name did not resolve during confirmation; the wallet will fail closed if it cannot verify it later.", "muted", "small");
        Control nameWarning = request.ContainsNonAsciiHost
            ? Ui.Column(3, Ui.Wrap("This name contains letters that may look like other letters:", "muted", "small"), Ui.Box("field", Ui.Wrap(host, "mono", "small")))
            : Ui.Text("", "small");
        var waiting = waitingCount?.Invoke() is { } count && count > 1
            ? Ui.Wrap((count - 1).ToString() + " more waiting.", "muted", "small")
            : Ui.Text("", "small");
        var allow = Ui.Button("Allow this source", "allow", "EndpointAllow", () => Answer(true), "small");
        var deny = Ui.Button("Do not add", "deny", "EndpointDeny", () => Answer(false), "small");
        var body = Ui.Column(13,
            Ui.Text(request.IsLoopback ? "Confirm local runtime" : "Confirm another computer", "h1"),
            Ui.Wrap("Your agent " + agentName + " wants to send your text to:", "muted"),
            Ui.Box("field", Ui.Wrap(endpoint, "mono", "small")),
            Ui.Text("Canonical host", "label"),
            Ui.Box("field", Ui.Wrap(canonicalHost, "mono", "small")),
            Ui.Wrap(warning, "muted"),
            nameWarning,
            Ui.Text("Model", "label"),
            Ui.Box("field", Ui.Wrap(request.Model, "mono")),
            addressText,
            Ui.Wrap("Nothing is saved or contacted until you allow this source. The wallet will remember this exact source and endpoint.", "muted", "small"),
            waiting);
        body.Margin = new Thickness(30, 26, 30, 24);
        var buttons = Ui.Row(8, allow, deny);
        buttons.Margin = new Thickness(30, 0, 30, 24);
        Content = Ui.Column(0, WindowLayout.Body(this, body, 92, 300), buttons);
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; Answer(false); }
            else if (e.Key is Key.Enter or Key.Space) e.Handled = true;
        };
        Opened += (_, _) => { Activate(); Focus(); };
        Closed += (_, _) =>
        {
            _closed = true;
            _result.TrySetResult(false);
        };
    }

    public Task<bool> Result => _result.Task;

    void Answer(bool value)
    {
        _result.TrySetResult(value);
        if (!_closed) DesktopUi.PostSafe(Close);
    }
}
