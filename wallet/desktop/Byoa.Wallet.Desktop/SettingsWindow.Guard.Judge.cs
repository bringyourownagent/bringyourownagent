using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Byoa.Wallet.Providers;
using Byoa.Wallet.Registry;
using Byoa.Wallet.Settings;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Desktop;

/// <summary>
/// Who checks the requests for the Agent guard: Jev through the visitor's OpenRouter key (the default), or one of his own
/// text models. A model on this computer keeps the requests home and costs nothing; its OpenRouter key is not needed, so
/// the key hint on the page says so only while Jev is the judge. Kept apart from the rest of the guard page because the
/// list of models follows the Sources page.
/// </summary>
sealed partial class SettingsWindow
{
    Control JudgeCard(string judge)
    {
        var column = Ui.Column(6, Ui.Text("Who checks the requests", "h2"));
        var combo = new ComboBox { MinWidth = 360 };
        combo.Items.Add(new ComboBoxItem { Content = "Jev, a decision model (through your OpenRouter key)", Tag = GuardSettings.DefaultJudge });
        combo.Items.Add(new ComboBoxItem { Content = "Jev, a decision model (straight at TypeSafe, with your TypeSafe key)", Tag = GuardSettings.TypeSafeJudge });
        combo.Items.Add(new ComboBoxItem { Content = "Kev, a decision model in your Llama app — stays on this computer, free", Tag = GuardSettings.KevJudge });
        foreach (var model in Judges())
            combo.Items.Add(new ComboBoxItem { Content = model.Name + (model.Runs == Runs.ThisDevice ? " — stays on this computer" : " — behind a key, paid"), Tag = model.Id });
        AutomationProperties.SetAutomationId(combo, "GuardJudge");
        var current = IsJev(judge) || judge == GuardSettings.KevJudge || Judges().Any(m => m.Id == judge) ? judge : GuardSettings.DefaultJudge;
        combo.SelectedIndex = current == GuardSettings.DefaultJudge
            ? 0
            : combo.Items.OfType<ComboBoxItem>().Select((item, i) => (item, i)).First(x => x.item.Tag as string == current).i;
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is not ComboBoxItem { Tag: string id }) return;
            if (_w.Store.Read(s => s.Guard.Judge) == id) return;
            _w.Store.Update(s => s.Guard.Judge = id);
            Render();
        };
        column.Children.Add(combo);
        column.Children.Add(Ui.Wrap(JudgeNote(current), "muted", "small"));
        if (!IsJev(current)) column.Children.Add(MaxInputRow());
        return column;
    }

    /// <summary>Jev, through OpenRouter or straight at TypeSafe: a judge that is no model of the visitor's and needs a key of its own.</summary>
    static bool IsJev(string judge) => judge == GuardSettings.DefaultJudge || judge == GuardSettings.TypeSafeJudge;

    /// <summary>The visitor's text models that may judge: no built-in speech, never one of his agents, and no model that draws pictures (unless he said it may answer text).</summary>
    IReadOnlyList<RegistrySourceInfo> Judges() =>
        _w.Registry.ListSources(Vocabulary.Generate)
            .Where(s => s.Kind is not null && s.Enabled &&
                !string.Equals(s.Kind, SourceKinds.Agent, StringComparison.OrdinalIgnoreCase) && _w.Engine.ServesText(s.Id))
            .ToList();

    string JudgeNote(string judge)
    {
        if (judge == GuardSettings.DefaultJudge)
            return "Jev writes no text: it only says how likely each statement is. The text of requests goes to OpenRouter to be checked.";
        if (judge == GuardSettings.TypeSafeJudge)
            return "Jev writes no text: it only says how likely each statement is. The text of requests goes straight to TypeSafe, who make Jev, to be checked.";
        if (judge == GuardSettings.KevJudge)
            return "Kev is a small decision model that runs in the Llama app on this computer: the text of requests never leaves it, no key is needed and checking costs nothing. " +
                   "It does not write text either, it only says how likely each statement is, and a little less sharply than Jev: on a test of 200 requests it held 16 ordinary ones by mistake " +
                   "against Jev's 8 and held every bad one. Add ggml-org/Kev-4B-GGUF in the Llama app and keep it loaded; if it is not there the guard cannot be asked, " +
                   "and what happens is your choice below.";
        var model = Judges().FirstOrDefault(m => m.Id == judge);
        if (model is null) return "The model picked here is not in your models any more: until you pick another, requests are not checked.";
        return model.Runs == Runs.ThisDevice
            ? model.Name + " runs on this computer: the text of requests never leaves it, and checking them costs nothing — the daily limit below does not apply. " +
              "If it is off or answers in a form the wallet cannot use, what happens is your choice below."
            : model.Name + " runs behind a key: the text of requests goes to its server, and what a check costs counts in the spend below.";
    }

    /// <summary>The longest input one call to the model sees; the rest is judged as its start, middle and end, on the careful side.</summary>
    Control MaxInputRow()
    {
        var chars = _w.Store.Read(s => s.Guard.MaxInputChars);
        var box = new TextBox { Text = chars.ToString(CultureInfo.InvariantCulture), Width = 110, MinWidth = 110 };
        AutomationProperties.SetAutomationId(box, "GuardMaxInputChars");
        AutomationProperties.SetName(box, "The longest input one call to the judge sees, in characters");
        box.TextChanged += (_, _) =>
        {
            if (!int.TryParse(box.Text?.Trim(), NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var value)) return;
            value = Math.Clamp(value, 500, 100_000);
            if (_w.Store.Read(s => s.Guard.MaxInputChars) == value) return;
            _w.Store.Update(s => s.Guard.MaxInputChars = value);
        };
        box.LostFocus += (_, _) => box.Text = _w.Store.Read(s => s.Guard.MaxInputChars).ToString(CultureInfo.InvariantCulture);
        return Ui.Row(8, Ui.Text("A request longer than this many characters is checked in pieces:", "small"), box);
    }

    /// <summary>The key and the spend, as they read for the judge picked: the OpenRouter key matters only while Jev judges.</summary>
    Control JudgeKeyCard(string judge, bool hasKey, decimal spent, decimal limit)
    {
        StackPanel card;
        if (judge == GuardSettings.TypeSafeJudge)
        {
            card = Ui.Column(8,
                Ui.Text(hasKey ? "Ready: it uses your TypeSafe key" : "It needs your TypeSafe key", "h2"),
                Ui.Wrap("The guard is Jev, asked at TypeSafe's own service (api.typesafe.ai), behind your own TypeSafe key (kept only on this PC; get one in the console at typesafe.ai). " +
                        "A request costs a few thousandths of a cent, and its text goes to TypeSafe to be checked. Spent today: " + Money.Show(spent) + " of " + Money.Show(limit) + ".", "muted", "small"));
        }
        else if (judge == GuardSettings.DefaultJudge)
        {
            card = Ui.Column(8,
                Ui.Text(hasKey ? "Ready: it uses your OpenRouter key" : "It needs your OpenRouter key", "h2"),
                Ui.Wrap("The guard is Jev, a decision model that runs at OpenRouter, behind your own key (the same key as for Scribe v2, kept only on this PC). " +
                        "A request costs a few thousandths of a cent, and its text goes to OpenRouter to be checked. Spent today: " + Money.Show(spent) + " of " + Money.Show(limit) + ".", "muted", "small"));
        }
        else if (judge == GuardSettings.KevJudge)
        {
            card = Ui.Column(8,
                Ui.Text("No key needed: Kev runs in your Llama app", "h2"),
                Ui.Wrap(JudgeNote(judge) + " Spent today on checking: " + Money.Show(spent) + ".", "muted", "small"));
        }
        else
        {
            var model = Judges().FirstOrDefault(m => m.Id == judge);
            var name = model?.Name ?? "the model you picked";
            var ready = model is null ? "Pick a model in the list above" : "Ready: " + name + " checks the requests";
            card = Ui.Column(8,
                Ui.Text(ready, "h2"),
                Ui.Wrap(JudgeNote(judge) + " Spent today on checking: " + Money.Show(spent) + ".", "muted", "small"));
        }

        card.Children[0].SetValue(AutomationProperties.AutomationIdProperty, "GuardKeyState");
        if (judge == GuardSettings.DefaultJudge && !hasKey) card.Children.Add(KeyExtras());
        if (judge == GuardSettings.TypeSafeJudge) card.Children.Add(TypeSafeKeyExtras(hasKey));
        return card;
    }

    /// <summary>Where the TypeSafe key is entered, replaced or removed: only here and only on this PC (the secret store keeps it, Windows-protected).</summary>
    Control TypeSafeKeyExtras(bool has)
    {
        var box = new TextBox { PasswordChar = '•', Watermark = "apikey_…", MinWidth = 300 };
        AutomationProperties.SetAutomationId(box, "TypeSafeKeyInput");
        var save = Ui.Button(has ? "Replace the key" : "Add the key", "deny", "TypeSafeKeySave", () =>
        {
            if (string.IsNullOrWhiteSpace(box.Text)) return;
            _w.Secrets.Set(JevDecisionClient.TypeSafeSecretName, box.Text);
            box.Text = "";
            Render();
        }, "small");
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(Ui.Row(8, box, save));
        if (has)
            panel.Children.Add(Ui.Button("Remove the key", "deny", "TypeSafeKeyRemove", () =>
            {
                _w.Secrets.Remove(JevDecisionClient.TypeSafeSecretName);
                Render();
            }, "small"));
        return panel;
    }
}
