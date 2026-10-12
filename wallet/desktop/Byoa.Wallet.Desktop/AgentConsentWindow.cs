using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Byoa.Wallet.Registry;

namespace Byoa.Wallet.Desktop;

/// <summary>The first-connection consent for one named MCP client.</summary>
sealed class AgentConsentWindow : Window
{
    readonly TaskCompletionSource<AgentConsentAnswer> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly CheckBox _full = new() { Content = "Full access (also site access, limits, paid approvals, and other agents)", IsChecked = false };
    bool _closed;

    public AgentConsentWindow(AgentConsentModel model)
    {
        Title = "Connect an AI agent";
        Width = 540;
        Height = 430;
        MinWidth = 480;
        MinHeight = 360;
        Background = Brushes.White;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        CanResize = false;

        var name = Ui.Runs((model.Identity.Name, true), (" wants to manage your wallet", false));
        AutomationProperties.SetName(name, model.Identity.Name + " wants to manage your wallet");
        var path = Ui.Box("field", Ui.Wrap(model.Identity.ExecutablePath, "mono", "small"));
        var body = Ui.Column(12,
            Ui.Row(12, Ui.Tile(Ui.Screen, 38), Ui.Column(3, name, Ui.Text("First connection", "muted", "small"))),
            Ui.Wrap("This agent can view wallet status and activity, add or remove model sources, set source order, and ask the wallet to download a model.", "muted"),
            Ui.Wrap("It cannot open a site's access, change your limits, approve another agent, or approve paid use unless you turn on Full access below.", "muted"),
            Ui.Column(5, Ui.Text("Executable", "label"), path),
            _full,
            Ui.Wrap("The wallet identifies this agent by its announced name and executable path. You can change Full access or Revoke it later in Settings → Agents.", "muted", "small"));
        body.Margin = new Thickness(26, 24, 26, 18);
        var allow = Ui.Button("Allow", "allow", "AgentAllow", () => Answer(AgentConsentDecision.Allow), "small");
        var notNow = Ui.Button("Not now", "deny", "AgentNotNow", () => Answer(AgentConsentDecision.NotNow), "small");
        var block = Ui.Link("Block", "AgentBlock", () => Answer(AgentConsentDecision.Block));
        block.HorizontalAlignment = HorizontalAlignment.Right;
        var buttons = Ui.Columns("Auto,*,Auto", Ui.Row(8, allow, notNow), block);
        buttons.Margin = new Thickness(26, 0, 26, 22);
        Content = Ui.Column(0, body, buttons);
        KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Escape) Answer(AgentConsentDecision.NotNow); };
        Closed += (_, _) =>
        {
            _closed = true;
            _answer.TrySetResult(new AgentConsentAnswer(AgentConsentDecision.NotNow, false));
        };
    }

    public Task<AgentConsentAnswer> Result => _answer.Task;

    void Answer(AgentConsentDecision decision)
    {
        _answer.TrySetResult(new AgentConsentAnswer(decision, decision == AgentConsentDecision.Allow && _full.IsChecked == true));
        if (!_closed) DesktopUi.PostSafe(Close);
    }
}
