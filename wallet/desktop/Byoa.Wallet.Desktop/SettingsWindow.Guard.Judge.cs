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
        foreach (var model in Judges())
            combo.Items.Add(new ComboBoxItem { Content = model.Name + (model.Runs == Runs.ThisDevice ? " — stays on this computer" : " — behind a key, paid"), Tag = model.Id });
        AutomationProperties.SetAutomationId(combo, "GuardJudge");
        var current = Judges().Any(m => m.Id == judge) ? judge : GuardSettings.DefaultJudge;
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
        if (current != GuardSettings.DefaultJudge) column.Children.Add(MaxInputRow());
        return column;
    }

    /// <summary>The visitor's text models that may judge: no built-in speech, and never one of his agents.</summary>
    IReadOnlyList<RegistrySourceInfo> Judges() =>
        _w.Registry.ListSources(Vocabulary.Generate)
            .Where(s => s.Kind is not null && s.Enabled &&
                !string.Equals(s.Kind, SourceKinds.Agent, StringComparison.OrdinalIgnoreCase))
            .ToList();

    string JudgeNote(string judge)
    {
        if (judge == GuardSettings.DefaultJudge)
            return "Jev writes no text: it only says how likely each statement is. The text of requests goes to OpenRouter to be checked.";
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
        if (judge == GuardSettings.DefaultJudge)
        {
            card = Ui.Column(8,
                Ui.Text(hasKey ? "Ready: it uses your OpenRouter key" : "It needs your OpenRouter key", "h2"),
                Ui.Wrap("The guard is Jev, a decision model that runs at OpenRouter, behind your own key (the same key as for Scribe v2, kept only on this PC). " +
                        "A request costs a few thousandths of a cent, and its text goes to OpenRouter to be checked. Spent today: " + Money.Show(spent) + " of " + Money.Show(limit) + ".", "muted", "small"));
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
        return card;
    }
}
