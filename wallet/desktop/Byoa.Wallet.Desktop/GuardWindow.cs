using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Byoa.Wallet.Engine;
using Byoa.Wallet.Guard;

namespace Byoa.Wallet.Desktop;

/// <summary>
/// The guard held something for the visitor: one request a site made to his agent, or the goal a site states for it. The window shows what the
/// site wrote and what the guard noticed, and asks. No answer, a closed window and Esc all refuse.
/// </summary>
sealed class GuardWindow : Window
{
    readonly TaskCompletionSource<GuardAnswer> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
    bool _closed;

    public GuardWindow(GuardPrompt model, Func<int>? waitingCount = null)
    {
        Title = model.ForPurpose ? "Give your agent to this site?" : "Your agent: a request was held";
        Width = 580;
        MinWidth = 520;
        Background = Brushes.White;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        CanResize = false;
        Topmost = true;
        WindowLayout.Prepare(this, 300);

        var headline = model.ForPurpose
            ? Ui.Runs((model.Host, true), (" wants to use your agent " + model.Agent + ".", false))
            : Ui.Runs((model.Host, true), (" asked your agent something that the guard held back.", false));
        AutomationProperties.SetName(headline, model.ForPurpose ? model.Host + " wants to use your agent" : model.Host + " asked your agent something that the guard held back");
        var purpose = Ui.Column(3,
            Ui.Text(model.ForPurpose ? "What the site says it will ask your agent:" : "What the site said it is for:", "strong"),
            Ui.Box("field", Ui.Wrap(model.Purpose, "small")));
        var noticed = Ui.Column(3, Ui.Text("What the guard noticed:", "strong"));
        foreach (var finding in model.Findings)
        {
            var line = Ui.Wrap("• " + finding.Label + (finding.Question == GuardQuestions.TooLong ? "" : " (" + finding.Percent + ")"), "small");
            AutomationProperties.SetAutomationId(line, "GuardFinding-" + finding.Question);
            noticed.Children.Add(line);
        }

        var parts = new List<Control> { Ui.Row(12, Ui.Tile(Ui.Screen, 38), headline), purpose };
        if (model.Request is { } request)
        {
            var sent = Ui.Wrap(request, "mono", "small");
            AutomationProperties.SetAutomationId(sent, "GuardRequest");
            parts.Add(Ui.Column(3, Ui.Text("What the site sent:", "strong"),
                Ui.Box("field", new ScrollViewer { Content = sent, MaxHeight = 150, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled })));
        }

        parts.Add(noticed);
        var waiting = waitingCount?.Invoke() is { } count && count > 1 ? (count - 1).ToString() + " more waiting. " : "";
        parts.Add(Ui.Wrap(waiting + (model.ForPurpose
            ? "The guard only reads: it is a small model that answers yes or no, and it can be wrong. Without your answer the agent is not given to the site."
            : "The guard only reads: it is a small model that answers yes or no, and it can be wrong. Without your answer the request is refused."), "muted", "small"));
        var body = Ui.Column(12, [.. parts]);
        body.Margin = new Thickness(26, 24, 26, 16);

        var allow = Ui.Button(model.ForPurpose ? "Give it my agent" : "Let it through", "allow", "GuardAllow", () => Answer(GuardAnswer.Allow), "small");
        var refuse = Ui.Button(model.ForPurpose ? "Do not give it" : "Refuse", "deny", "GuardRefuse", () => Answer(GuardAnswer.Refuse), "small");
        Panel buttons;
        if (model.ForPurpose) buttons = Ui.Row(8, allow, refuse);
        else
        {
            var takeAway = Ui.Link("Refuse and take my agent away from this site", "GuardTakeAway", () => Answer(GuardAnswer.TakeAway));
            takeAway.HorizontalAlignment = HorizontalAlignment.Right;
            buttons = Ui.Columns("Auto,*,Auto", Ui.Row(8, allow, refuse), takeAway);
        }

        buttons.Margin = new Thickness(26, 0, 26, 22);
        // The answer row stays outside the scroll viewer: a long request must never hide the buttons.
        Content = Ui.Column(0, WindowLayout.Body(this, body, 100, 300), buttons);
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; Answer(GuardAnswer.Refuse); }
            else if (e.Key is Key.Enter or Key.Space) e.Handled = true;
        };
        Opened += (_, _) => { Activate(); Focus(); };
        Closed += (_, _) =>
        {
            _closed = true;
            _answer.TrySetResult(GuardAnswer.Refuse);
        };
    }

    public Task<GuardAnswer> Result => _answer.Task;

    void Answer(GuardAnswer value)
    {
        _answer.TrySetResult(value);
        if (!_closed) DesktopUi.PostSafe(Close);
    }
}
