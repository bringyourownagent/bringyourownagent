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

    public EndpointConfirmationWindow(EndpointConfirmationRequest request)
    {
        Title = "Confirm another computer";
        Width = 560;
        Height = 340;
        MinWidth = 500;
        MinHeight = 300;
        Background = Brushes.White;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        CanResize = false;
        ShowActivated = true;

        var agentName = SourceLabels.Clean(request.AgentName) ?? "your AI agent";
        var warning = request.Endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            ? "This connection is not encrypted."
            : "This is another computer.";
        var allow = Ui.Button("Allow this source", "allow", "EndpointAllow", () => Answer(true), "small");
        var deny = Ui.Button("Do not add", "deny", "EndpointDeny", () => Answer(false), "small");
        var content = Ui.Column(13,
            Ui.Text("Confirm another computer", "h1"),
            Ui.Wrap("Your agent " + agentName + " wants to send your text to " + request.Endpoint + ". " + warning, "muted"),
            Ui.Text("Model", "label"),
            Ui.Box("field", Ui.Wrap(request.Model, "mono")),
            Ui.Wrap("Nothing is saved or contacted until you allow this source. The wallet will remember this exact source and endpoint.", "muted", "small"),
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
