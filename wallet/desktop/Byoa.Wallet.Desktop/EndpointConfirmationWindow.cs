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
        Height = 420;
        MinWidth = 500;
        MinHeight = 300;
        Background = Brushes.White;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        CanResize = false;
        ShowActivated = true;

        var agentName = SourceLabels.Clean(request.AgentName) ?? "your AI agent";
        var endpoint = request.CanonicalEndpoint;
        var warning = request.IsLoopback
            ? "This runtime is on this computer."
            : request.Warning == "encrypted"
                ? "This is another computer. The connection is encrypted."
                : "This is another computer. The connection is not encrypted.";
        var addressText = request.ResolvedAddresses.Count > 0
            ? "Resolved addresses: " + string.Join(", ", request.ResolvedAddresses)
            : "The name did not resolve during confirmation; the wallet will fail closed if it cannot verify it later.";
        var nameWarning = request.ContainsNonAsciiHost
            ? Ui.Wrap("This name contains letters that may look like other letters: " + (Uri.TryCreate(endpoint, UriKind.Absolute, out var canonical) ? canonical.IdnHost : endpoint), "muted", "small")
            : Ui.Text("", "small");
        var waiting = waitingCount?.Invoke() is { } count && count > 1
            ? Ui.Wrap((count - 1).ToString() + " more waiting.", "muted", "small")
            : Ui.Text("", "small");
        var allow = Ui.Button("Allow this source", "allow", "EndpointAllow", () => Answer(true), "small");
        var deny = Ui.Button("Do not add", "deny", "EndpointDeny", () => Answer(false), "small");
        var content = Ui.Column(13,
            Ui.Text(request.IsLoopback ? "Confirm local runtime" : "Confirm another computer", "h1"),
            Ui.Wrap("Your agent " + agentName + " wants to send your text to " + endpoint + ". " + warning, "muted"),
            nameWarning,
            Ui.Text("Model", "label"),
            Ui.Box("field", Ui.Wrap(request.Model, "mono")),
            Ui.Wrap(addressText, "muted", "small"),
            Ui.Wrap("Nothing is saved or contacted until you allow this source. The wallet will remember this exact source and endpoint.", "muted", "small"),
            waiting,
            Ui.Row(8, allow, deny));
        content.Margin = new Thickness(30, 26, 30, 24);
        Content = content;
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
