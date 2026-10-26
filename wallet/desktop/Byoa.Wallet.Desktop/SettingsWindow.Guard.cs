using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Byoa.Wallet.Engine;
using Byoa.Wallet.Guard;
using Byoa.Wallet.Providers;
using Byoa.Wallet.Registry;
using Byoa.Wallet.Settings;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Desktop;

/// <summary>The Agent guard page, and what the Sources page shows for the visitor's own agent.</summary>
sealed partial class SettingsWindow
{
    void GuardPage(StackPanel page)
    {
        Head(page, "Agent guard",
            "When a site is given your own agent, a small fast model reads every request before your agent does. It answers only yes or no to a few questions " +
            "(could this harm your computer, does it try to give the agent orders, is it what the site said it is for), and what looks wrong waits for you.");
        var guard = _w.Store.Read(s => (s.Guard.Judge, s.Guard.DailyLimit, s.Guard.StopWhenUnavailable, s.Guard.Strictness));
        var limit = Money.Parse(guard.DailyLimit, 2.00m);
        var spent = _w.GuardLedger.SpentOn(DateOnly.FromDateTime(DateTime.UtcNow));

        // who checks the requests: Jev, or one of the visitor's own models (SettingsWindow.Guard.Judge.cs)
        page.Children.Add(Ui.Box("card", JudgeCard(guard.Judge)));

        // its key (only Jev needs one) and what checking costs today
        var hasKey = _w.Secrets.Has(guard.Judge == GuardSettings.TypeSafeJudge ? JevDecisionClient.TypeSafeSecretName : OpenRouterSource.SecretName);
        page.Children.Add(Ui.Box("card", JudgeKeyCard(guard.Judge, hasKey, spent, limit)));

        page.Children.Add(Ui.Box("card", Ui.Column(8, Ui.Text("What it may spend a day", "h2"), DailyLimitRow(limit),
            Ui.Wrap("When this is spent the guard stops for the day, and what happens then is your choice below.", "muted", "small"))));

        // how soon a request is held
        var strictness = Ui.Column(6, Ui.Text("How soon a request is held for you", "h2"));
        foreach (var (value, name, note) in new[]
                 {
                     (GuardSettings.Strict, "Strict", "holds anything that looks slightly off"),
                     (GuardSettings.Normal, "Normal", "holds what looks wrong"),
                     (GuardSettings.Relaxed, "Relaxed", "holds only what is clearly wrong"),
                 })
        {
            var one = value;
            var radio = new RadioButton { Content = name + " · " + note, GroupName = "GuardStrictness", IsChecked = guard.Strictness == value || (guard.Strictness is not (GuardSettings.Strict or GuardSettings.Relaxed) && value == GuardSettings.Normal) };
            AutomationProperties.SetAutomationId(radio, "GuardStrictness-" + value);
            radio.IsCheckedChanged += (_, _) =>
            {
                if (radio.IsChecked == true && _w.Store.Read(s => s.Guard.Strictness) != one) _w.Store.Update(s => s.Guard.Strictness = one);
            };
            strictness.Children.Add(radio);
        }

        page.Children.Add(Ui.Box("card", strictness));

        // when the guard cannot be asked
        var down = Ui.Column(6, Ui.Text("If the guard cannot be asked", "h2"));
        foreach (var (stop, name, id) in new[] { (true, "Stop the agent: no request reaches it (recommended)", "GuardWhenDown-stop"), (false, "Let requests through without a look", "GuardWhenDown-let") })
        {
            var one = stop;
            var radio = new RadioButton { Content = name, GroupName = "GuardWhenDown", IsChecked = guard.StopWhenUnavailable == stop };
            AutomationProperties.SetAutomationId(radio, id);
            radio.IsCheckedChanged += (_, _) =>
            {
                if (radio.IsChecked == true && _w.Store.Read(s => s.Guard.StopWhenUnavailable) != one) _w.Store.Update(s => s.Guard.StopWhenUnavailable = one);
            };
            down.Children.Add(radio);
        }

        down.Children.Add(Ui.Wrap("No key, no network, or the daily limit spent. A site's goal that could not be checked is not remembered as accepted either way.", "muted", "small"));
        page.Children.Add(Ui.Box("card", down));

        // the questions, in words the visitor can change
        page.Children.Add(Section("What the guard asks"));
        page.Children.Add(Ui.Wrap("Each is a statement the model says is true or not, with a chance between 0 and 100. Reword one if you want the guard to look for something else; " +
                                  "`request.text` is what the site sent, `site.declared_purpose` is what the site said it is for.", "muted", "small"));
        foreach (var name in GuardQuestions.All) page.Children.Add(GuardWording(name));
    }

    Control DailyLimitRow(decimal limit)
    {
        var box = new TextBox { Text = limit.ToString("0.00##", CultureInfo.InvariantCulture), Width = 110, MinWidth = 110 };
        AutomationProperties.SetAutomationId(box, "GuardDailyLimit");
        AutomationProperties.SetName(box, "Daily limit of the guard, in dollars");
        box.TextChanged += (_, _) =>
        {
            // Only a number counts; anything else is left as typed and not saved until it is one.
            if (!decimal.TryParse(box.Text?.Trim().TrimStart('$'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var dollars) || dollars < 0 || dollars > 1000) return;
            var spec = Money.ToSpec(dollars);
            // Avalonia raises TextChanged a moment after a box is made, with the text it was made with: that is no edit.
            if (Money.Parse(_w.Store.Read(s => s.Guard.DailyLimit), 2.00m) == Money.Parse(spec, 0)) return;
            _w.Store.Update(s => s.Guard.DailyLimit = spec);
        };
        box.LostFocus += (_, _) => box.Text = Money.Parse(_w.Store.Read(s => s.Guard.DailyLimit), 2.00m).ToString("0.00##", CultureInfo.InvariantCulture);
        return Ui.Row(8, Ui.Text("Dollars a day", "small"), box);
    }

    Control GuardWording(string name)
    {
        var current = GuardQuestions.Text(_w.Store.Read(s => s.Guard), name);
        var box = new TextBox { Text = current, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 76 };
        box.Classes.Add("small");
        AutomationProperties.SetAutomationId(box, "GuardText-" + name);
        AutomationProperties.SetName(box, "Wording of the statement " + name);
        box.TextChanged += (_, _) =>
        {
            var text = box.Text?.Trim() ?? "";
            if (text.Length == 0 || text.Length > 2000) return;
            // The wallet's own wording is kept as "none", so that an update of it reaches the visitor who never changed it.
            var own = text == GuardQuestions.Default(name) ? null : text;
            if (GuardQuestions.Text(_w.Store.Read(s => s.Guard), name) == text) return;
            _w.Store.Update(s => SetWording(s.Guard, name, own));
        };
        var reset = Ui.Button("Use the wallet's wording", "deny", "GuardTextReset-" + name, () =>
        {
            _w.Store.Update(s => SetWording(s.Guard, name, null));
            box.Text = GuardQuestions.Default(name);
        }, "small");
        return Ui.Box("card", Ui.Column(6, Ui.Text(GuardQuestions.Label(name)[..1].ToUpperInvariant() + GuardQuestions.Label(name)[1..], "strong"), box, reset));
    }

    static void SetWording(GuardSettings guard, string name, string? text)
    {
        switch (name)
        {
            case GuardQuestions.OnPurpose: guard.OnPurposeText = text; break;
            case GuardQuestions.Harm: guard.HarmText = text; break;
            case GuardQuestions.Redirect: guard.RedirectText = text; break;
            case GuardQuestions.PurposeRisk: guard.PurposeRiskText = text; break;
        }
    }

    /// <summary>My agents: the visitor's own AI agents that can answer a site's questions, one line each; the settings open with the arrow.</summary>
    void MyAgentsPage(StackPanel page)
    {
        Head(page, "My agents", "Your own AI agent can answer a site's questions and look things up on the web for it: the news of a country, say. " +
            "A site gets it only when you tick it for that site, it must say what it will ask, and every request passes the Agent guard first. " +
            "Letting an agent manage this wallet is another thing: see My MCP.");
        page.Children.Add(Ui.Row(8, Ui.Button("Add an agent", "allow", "AddAgent", () => _w.Ui.OpenAddAgent(), "small"),
            Ui.Button("Agent guard", "deny", "OpenAgentGuard", () => Show("guard"), "small")));
        var agents = _w.Store.Snapshot().Sources.Where(s => string.Equals(s.Kind, SourceKinds.Agent, StringComparison.OrdinalIgnoreCase)).ToList();
        if (agents.Count == 0)
            page.Children.Add(Ui.Wrap("No agent added yet. Use Add an agent: Claude Code, or a launcher built on it, works so far.", "muted"));
        foreach (var agent in agents) page.Children.Add(AddedSourceRow(agent));
    }

    /// <summary>The settings of the visitor's own agent, behind the arrow of its line in My agents.</summary>
    Control AgentSourceDetails(SourceSetting source, RegistrySourceInfo? info)
    {
        var test = Ui.Button("Test it", "deny", "AgentSourceTest-" + SafeId(source.Id), () => _ = TestAgentAsync(source), "small");
        var remove = Ui.Button("Remove", "deny", "AddedSourceRemove-" + source.Id, () =>
        {
            _w.Registry.RemoveSource(source.Id);
            Render();
        }, "small");
        var sites = _w.Store.Read(s => s.Sites.Where(x => x.Models.Any(m => m.SourceIds.Contains(source.Id))).Select(x => x.Origin).ToList());
        var given = sites.Count == 0 ? "No site has it yet." : "Given to " + string.Join(", ", sites.Select(o => new Uri(o).Host)) + ".";
        return Ui.Column(7,
            Ui.Wrap(info?.Name ?? source.Label ?? source.Id, "strong"),
            Ui.Wrap(source.Id, "mono", "small"),
            Ui.Wrap(info?.StateText ?? "configured", "muted"),
            Ui.Wrap("Runs " + (source.AgentCommand ?? "?") + " · " + AgentFlavors.Name(source.AgentFlavor)
                + (string.IsNullOrWhiteSpace(source.AgentModel) ? "" : " · model " + source.AgentModel), "mono", "small"),
            Ui.Wrap("One question at a time, with web search and page reading only. A site gets it only when you tick it in that site's window (or in Sites), and every request goes through the guard. " + given, "muted", "small"),
            Ui.Row(8, test, remove));
    }

    async Task TestAgentAsync(SourceSetting source)
    {
        _w.Ui.ShowInfo(new InfoModel("Your agent", "Asking " + (source.Label ?? source.Id) + " a test question. This can take up to a minute."));
        var result = await _w.Registry.TestSourceAsync(source.Id, visitorInitiated: true);
        _w.Ui.ShowInfo(new InfoModel("Your agent", result.Ok
            ? "It answered: " + (result.Detail is { Length: > 160 } answer ? answer[..160] + "…" : result.Detail)
            : result.Message));
    }
}
