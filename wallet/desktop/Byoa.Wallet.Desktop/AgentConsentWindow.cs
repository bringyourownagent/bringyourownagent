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

    public AgentConsentWindow(AgentConsentModel model, Func<int>? waitingCount = null)
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
        var path = Ui.Box("field", Ui.Wrap(model.Identity.ClientExecutablePath, "mono", "small"));
        var observedParent = model.Identity.ObservedParentPath ?? "unknown parent: the wallet could not see which program started the bridge";
        var startedBy = Ui.Runs(("Started by: ", true), (observedParent, true));
        var interpreterNote = model.Identity.ParentIsInterpreter
            ? Ui.Wrap("This program runs other programs; anything started through it will share this approval. You can turn on Full access later in Settings → Agents.", "muted", "small")
            : Ui.Text("", "small");
        _full.IsVisible = model.FullAccessAllowed;
        var accessNote = model.FullAccessAllowed
            ? "It cannot open a site's access, change your limits, approve another agent, or approve paid use unless you turn on Full access below."
            : "Full access is not offered on this first connection because this program runs other programs. You can turn it on later in Settings â†’ Agents.";
        var body = Ui.Column(12,
            Ui.Row(12, Ui.Tile(Ui.Screen, 38), Ui.Column(3, startedBy, name, Ui.Text("First connection", "muted", "small"))),
            Ui.Wrap("It can manage sources and routes (you confirm any source on another computer).", "muted"),
            Ui.Wrap("It can download models and, after your confirmation, delete them.", "muted"),
            Ui.Wrap("It can write API keys (it cannot read them).", "muted"),
            Ui.Wrap("It can read the activity log (site names only with Full access).", "muted"),
            Ui.Wrap(accessNote, "muted"),
            Ui.Column(5, Ui.Text("Parent program (observed by wallet)", "label"), Ui.Box("field", Ui.Wrap(observedParent, "mono", "small"))),
            Ui.Column(5, Ui.Text("Bridge process (observed by wallet)", "label"), path),
            _full,
            interpreterNote,
            Ui.Wrap((waitingCount?.Invoke() is { } count && count > 1 ? (count - 1).ToString() + " more waiting. " : "") + "The wallet identifies this agent by the parent program it observed. You can change Full access or Revoke it later in Settings.", "muted", "small"));
        body.Margin = new Thickness(26, 24, 26, 18);
        var allow = Ui.Button("Allow", "allow", "AgentAllow", () => Answer(AgentConsentDecision.Allow), "small");
        var notNow = Ui.Button("Not now", "deny", "AgentNotNow", () => Answer(AgentConsentDecision.NotNow), "small");
        var block = Ui.Link("Block", "AgentBlock", () => Answer(AgentConsentDecision.Block));
        block.HorizontalAlignment = HorizontalAlignment.Right;
        var buttons = Ui.Columns("Auto,*,Auto", Ui.Row(8, allow, notNow), block);
        buttons.Margin = new Thickness(26, 0, 26, 22);
        Content = Ui.Column(0, body, buttons);
        KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Escape) Answer(AgentConsentDecision.NotNow);
            else if (e.Key is Avalonia.Input.Key.Enter or Avalonia.Input.Key.Space) e.Handled = true;
        };
        Opened += (_, _) => { Activate(); Focus(); };
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
