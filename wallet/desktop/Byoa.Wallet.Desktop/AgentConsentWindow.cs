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
    readonly CheckBox _full = new() { Content = "Full access (sites, limits, built-in sources, agent-supplied keys)", IsChecked = false };
    bool _closed;

    public AgentConsentWindow(AgentConsentModel model, Func<int>? waitingCount = null)
    {
        Title = "Connect an AI agent";
        Width = 540;
        MinWidth = 480;
        Background = Brushes.White;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        CanResize = false;
        WindowLayout.Prepare(this);

        var name = Ui.Runs((model.Identity.Name, true), (" wants to manage your wallet", false));
        AutomationProperties.SetName(name, model.Identity.Name + " wants to manage your wallet");
        var observedParent = model.Identity.ObservedParentPath ?? "unknown parent: the wallet could not see which program started the bridge";
        var startedBy = Ui.Column(3,
            Ui.Text("Started by:", "strong"),
            Ui.Box("field", Ui.Wrap(observedParent, "mono", "small")),
            Ui.Text("Observed by the wallet", "muted", "small"),
            name,
            Ui.Text("First connection", "muted", "small"));
        var interpreterNote = model.Identity.ParentIsInterpreter
            ? Ui.Wrap("This program runs other programs; anything started through it will share this approval. Full access is not offered on this first connection; you can turn it on later in Settings → Agents.", "muted", "small")
            : Ui.Text("", "small");
        _full.IsVisible = model.FullAccessAllowed;
        var accessNote = model.FullAccessAllowed
            ? "Without Full access it cannot see site names, change site access or limits, switch built-in sources, or supply or remove API keys. Even with Full access, no tool lets it allow a site, approve a paid call or approve another agent."
            : null;
        var body = Ui.Column(12,
            Ui.Row(12, Ui.Tile(Ui.Screen, 38), startedBy),
            Ui.Wrap("It can manage sources and routes (you confirm any source on another computer).", "muted"),
            Ui.Wrap("It can download models and, after your confirmation, delete them.", "muted"),
            Ui.Wrap("It can ask you to type an API key into the wallet; the wallet never sends a key back to it.", "muted"),
            Ui.Wrap("It can read the activity log (site names only with Full access).", "muted"),
            accessNote is null ? Ui.Text("", "small") : Ui.Wrap(accessNote, "muted"),
            _full,
            interpreterNote,
            Ui.Wrap((waitingCount?.Invoke() is { } count && count > 1 ? (count - 1).ToString() + " more waiting. " : "") + "The wallet identifies this agent by the parent program it observed. Another program you run can start the same bridge, so allow only agents you trust. You can change Full access or Revoke it later in Settings.", "muted", "small"));
        body.Margin = new Thickness(26, 24, 26, 18);
        var allow = Ui.Button("Allow", "allow", "AgentAllow", () => Answer(AgentConsentDecision.Allow), "small");
        var notNow = Ui.Button("Not now", "deny", "AgentNotNow", () => Answer(AgentConsentDecision.NotNow), "small");
        var block = Ui.Link("Block", "AgentBlock", () => Answer(AgentConsentDecision.Block));
        block.HorizontalAlignment = HorizontalAlignment.Right;
        var buttons = Ui.Columns("Auto,*,Auto", Ui.Row(8, allow, notNow), block);
        buttons.Margin = new Thickness(26, 0, 26, 22);
        // Keep the answer row outside the scroll viewer. A long observed path or a 125% display must never hide
        // Allow, Not now, or Block.
        Content = Ui.Column(0, WindowLayout.Body(this, body, 105), buttons);
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
