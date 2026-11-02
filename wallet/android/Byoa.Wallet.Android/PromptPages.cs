using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Byoa.Wallet.Engine;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Android;

/// <summary>Makes the full-screen page of a question of the engine.</summary>
static class PromptPages
{
    public static Control Create(Prompt prompt, Shell shell) => prompt.Model switch
    {
        ConsentModel => new ConsentPage((Prompt<ConsentAnswer>)prompt, shell.W),
        PaidModel => new PaidPage((Prompt<PaidAnswer>)prompt),
        RaiseModel => new RaisePage((Prompt<decimal?>)prompt),
        _ => throw new InvalidOperationException("a question the phone has no page for"),
    };

    /// <summary>The frame of the small questions: a sign-blue tile, the host and what it wants, the visitor's choices under it.</summary>
    internal static Control Frame(string host, string headline, string text, params Control[] choices)
    {
        var body = Kit.Column(14,
            Kit.Columns("Auto,*", Kit.Tile(Kit.Globe, 40), Kit.Column(2, Kit.Text(host, "hostmono"), Kit.Text(headline, "strong"))),
            Kit.Text(text, "muted"));
        ((Control)((Grid)body.Children[0]).Children[1]).Margin = new Thickness(12, 0, 0, 0);
        foreach (var choice in choices)
        {
            choice.HorizontalAlignment = HorizontalAlignment.Stretch;
            body.Children.Add(choice);
        }

        body.Margin = new Thickness(16, 24, 16, 24);
        return new ScrollViewer { Content = body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    }
}

/// <summary>"Your models could not answer: use a paid source?" Allow once, Always for this site, or No. Back is No.</summary>
sealed class PaidPage : UserControl
{
    public PaidPage(Prompt<PaidAnswer> prompt)
    {
        var model = (PaidModel)prompt.Model;
        Content = PromptPages.Frame(model.Host, "needs a paid source",
            "Use " + model.SourceName + " for this? About " + Money.Show(model.Estimate) + ", from your API key. " + model.Why,
            Kit.Button("Allow once", "allow", "PaidOnce", () => prompt.Give(PaidAnswer.Once)),
            Kit.Button("Always for this site", "deny", "PaidAlways", () => prompt.Give(PaidAnswer.Always)),
            Kit.Button("No", "deny", "PaidNo", () => prompt.Give(PaidAnswer.No)));
        AutomationProperties.SetAutomationId(this, "PaidPage");
    }
}

/// <summary>"host reached a limit: raise it?" A button for each raise the engine offers; No (or Back) leaves the call unanswered.</summary>
sealed class RaisePage : UserControl
{
    public RaisePage(Prompt<decimal?> prompt)
    {
        var model = (RaiseModel)prompt.Model;
        var whose = model.Whose == "wallet"
            ? "The whole wallet's " + model.Name + " (" + model.Now + ", for all sites) is used up."
            : "The " + model.Name + " of this site (" + model.Now + ") is used up.";
        var choices = new List<Control>();
        for (var i = 0; i < model.Raises.Count; i++)
        {
            var choice = model.Raises[i];
            var button = Kit.Button(choice.Label + ", to " + choice.NewText, i == 0 ? "allow" : "deny", "RaiseLimit" + i, () => prompt.Give(choice.Value));
            AutomationProperties.SetName(button, "Raise to " + choice.NewText);
            choices.Add(button);
        }

        // as on the desktop: the amount may be set by hand in Limits (at the site's own card); the waiting call is not raised, as with No
        choices.Add(Kit.Button("Set it in Limits…", "deny", "RaiseByHand", () =>
        {
            prompt.Give(null);
            WalletHost.Services.Ui.OpenSettings(model.SettingsPage);
        }));
        choices.Add(Kit.Button("No", "deny", "RaiseNo", () => prompt.Give(null)));
        Content = PromptPages.Frame(model.Host, "reached a limit", whose + " Raise it? Its calls then go on at once.", [.. choices]);
        AutomationProperties.SetAutomationId(this, "RaisePage");
    }
}
