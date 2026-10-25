using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Byoa.Wallet.Engine;

namespace Byoa.Wallet.Desktop;

/// <summary>
/// The part of the consent window for a site that asks for several models: the site's reason (a line, and more behind a button), the
/// models to tick with what each costs and where it runs, a way to add more from the visitor's own key, and what the site may see.
/// A site that asks for more room than the visitor's limits gets one block that offers it.
/// </summary>
sealed partial class ConsentWindow
{
    /// <summary>What the visitor has ticked for one skill, and the controls that show it.</summary>
    sealed class ModelPick(ModelSection section)
    {
        public ModelSection Section { get; } = section;
        public List<string> Order { get; } = [];
        public Dictionary<string, ModelChoice> Known { get; } = new(StringComparer.Ordinal);
        public StackPanel List { get; } = new() { Spacing = 6 };
        public TextBlock Count { get; } = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        public StackPanel Extra { get; } = new() { Spacing = 6, IsVisible = false };
        public StackPanel Results { get; } = new() { Spacing = 6 };
        public bool Expanded { get; set; }
        public bool Required { get; set; }
    }

    // The site's recommendations and the visitor's own models shown without opening "Change models…".
    const int CollapsedRows = 6;

    static string Number(int value) => value.ToString("N0", CultureInfo.InvariantCulture);

    Control ModelRow(ConsentRow row, StackPanel panel, StackPanel title)
    {
        var section = row.Models!;
        var spec = section.Spec;
        var pick = new ModelPick(section) { Required = row.Required };
        foreach (var choice in section.Choices)
        {
            pick.Known[choice.Key] = choice;
            if (choice.Checked) pick.Order.Add(choice.Key);
        }

        _picks[row.Skill] = pick;
        panel.Children.Add(title);

        // The site's reason: one short line always, the long one behind "More".
        var how = spec.Min == spec.Max ? (spec.Min == 1 ? "one model" : Number(spec.Min) + " different models") : Number(spec.Min) + " to " + Number(spec.Max) + " different models";
        var reason = spec.Why ?? "The site uses " + how + ".";
        var reasonLine = Ui.Wrap(reason, "muted");
        AutomationProperties.SetAutomationId(reasonLine, "ConsentModelsWhy");
        var more = Ui.Link("More", "ConsentModelsMore", () => { });
        more.HorizontalAlignment = HorizontalAlignment.Right;
        panel.Children.Add(Ui.Columns("*,Auto", reasonLine, more));

        var longer = new StackPanel { Spacing = 5, IsVisible = false, Margin = new Thickness(0, 2, 0, 2) };
        longer.Children.Add(Ui.Wrap(spec.WhyLong ?? "The site did not say more. It asks for " + how + (spec.ByVendor ? ", from different companies where possible" : "") + ".", "wrap"));
        longer.Children.Add(Ui.Wrap("It uses " + how + (spec.ByVendor ? ", from different companies where possible" : "") + "."
            + (spec.Require.Count > 0 ? " It cannot work without " + string.Join(", ", spec.Require.Select(w => string.Join(" or ", w.AnyOf))) + "." : "")
            + (spec.Recommend.Count > 0 ? " It recommends " + string.Join(", ", spec.Recommend.Take(6)) + (spec.Recommend.Count > 6 ? " and " + Number(spec.Recommend.Count - 6) + " more" : "") + "." : "")
            + " You choose which of your models it gets; it never sees the others.", "muted", "small"));
        panel.Children.Add(longer);
        more.Click += (_, _) =>
        {
            longer.IsVisible = !longer.IsVisible;
            ((TextBlock)more.Content!).Text = longer.IsVisible ? "Less" : "More";
        };

        if (section.MissingRequired.Count > 0 || section.Shortfall is not null)
        {
            var problem = section.MissingRequired.Count > 0
                ? "The site cannot work without " + string.Join(", ", section.MissingRequired) + "."
                : "The site " + section.Shortfall + ".";
            panel.Children.Add(Ui.Wrap(problem + " Add a model, or an OpenRouter key, and try again.", "muted"));
            panel.Children.Add(Ui.Link("Add a model or a key…", "ConsentFix", () => _openSettings("add-model")));
        }

        pick.Count.Classes.Add("muted");
        AutomationProperties.SetAutomationId(pick.Count, "ConsentModelsCount");
        var change = Ui.Link("Change models…", "ConsentModelsChange", () => { });
        change.HorizontalAlignment = HorizontalAlignment.Right;
        panel.Children.Add(Ui.Columns("*,Auto", pick.Count, change));
        panel.Children.Add(pick.List);
        panel.Children.Add(ExtraModels(pick));
        change.Click += (_, _) =>
        {
            pick.Expanded = !pick.Expanded;
            pick.Extra.IsVisible = pick.Expanded;
            ((TextBlock)change.Content!).Text = pick.Expanded ? "Fewer models" : "Change models…";
            FillModels(pick);
        };

        if (spec.Names)
        {
            _names = new CheckBox { IsChecked = true, Content = Ui.Wrap("The site sees the names of these models", "small"), FontSize = 13 };
            AutomationProperties.SetAutomationId(_names, "ConsentModelNames");
            AutomationProperties.SetName(_names, "The site sees the names of these models");
            panel.Children.Add(_names);
        }

        FillModels(pick);
        var box = Ui.Box("row", panel);
        return box;
    }

    /// <summary>What "Change models…" opens: a search of the public list, for the models behind the visitor's own OpenRouter key.</summary>
    Control ExtraModels(ModelPick pick)
    {
        pick.Extra.Children.Add(new Avalonia.Controls.Shapes.Line { StartPoint = new Point(0, 0), EndPoint = new Point(330, 0), Stroke = Ui.Brush("Rule"), StrokeThickness = 1, StrokeDashArray = [3, 3] });
        if (_search is null || !_canSearch)
        {
            pick.Extra.Children.Add(Ui.Wrap("To pick from hundreds more models, add your OpenRouter key in My models. Models you already have are in the list above.", "muted", "small"));
            pick.Extra.Children.Add(Ui.Link("Open My models…", "ConsentModelsSources", () => _openSettings("sources")));
            return pick.Extra;
        }

        var query = new TextBox { Watermark = "Search models (OpenRouter)", MinWidth = 300 };
        AutomationProperties.SetAutomationId(query, "ConsentModelsSearch");
        AutomationProperties.SetName(query, "Search models");
        void Refresh()
        {
            pick.Results.Children.Clear();
            var found = _search(query.Text);
            if (found.Count == 0)
            {
                pick.Results.Children.Add(Ui.Wrap("No model found. Try fewer words.", "muted", "small"));
                return;
            }

            foreach (var choice in found.Take(8))
            {
                pick.Known.TryAdd(choice.Key, choice);
                pick.Results.Children.Add(ModelLine(pick, pick.Known[choice.Key]));
            }
        }

        query.TextChanged += (_, _) => Refresh();
        pick.Extra.Children.Add(Ui.Wrap("Models behind your OpenRouter key. A model you tick is added to your models and paid from the key.", "muted", "small"));
        pick.Extra.Children.Add(query);
        pick.Extra.Children.Add(pick.Results);
        Refresh();
        return pick.Extra;
    }

    /// <summary>Draws the models again: the ticked ones and the site's recommendations, or everything once the visitor has opened the list.</summary>
    void FillModels(ModelPick pick)
    {
        var spec = pick.Section.Spec;
        pick.List.Children.Clear();
        var ordered = pick.Known.Values
            .Where(c => !(c.IsNew && !pick.Order.Contains(c.Key) && !pick.Section.Choices.Any(x => x.Key == c.Key)))      // search results live in their own list
            .OrderBy(c => pick.Order.Contains(c.Key) ? pick.Order.IndexOf(c.Key) : int.MaxValue)
            .ThenBy(c => c.Role == ModelRole.Required ? 0 : c.Role == ModelRole.Recommended ? 1 : 2)
            .ToList();
        var shown = pick.Expanded ? ordered : ordered.Where(c => pick.Order.Contains(c.Key) || c.Role != ModelRole.Yours).Take(CollapsedRows).ToList();
        foreach (var choice in shown) pick.List.Children.Add(ModelLine(pick, choice));
        if (!pick.Expanded && ordered.Count > shown.Count)
            pick.List.Children.Add(Ui.Wrap("and " + Number(ordered.Count - shown.Count) + " more of yours under “Change models…”", "muted", "small"));
        if (shown.Count == 0) pick.List.Children.Add(Ui.Wrap("None of your models can answer this site yet.", "muted"));

        var limit = spec.Min == spec.Max ? "the site uses " + Number(spec.Max) : "the site uses " + Number(spec.Min) + " to " + Number(spec.Max);
        pick.Count.Text = Number(pick.Order.Count) + " ticked · " + limit;
        UpdateAllow();
    }

    Control ModelLine(ModelPick pick, ModelChoice choice)
    {
        var ticked = pick.Order.Contains(choice.Key);
        // One model, and the site cannot do without it: a choice among several, so round buttons (picking one lets go of the other).
        // Otherwise boxes; a box is never greyed because the list is full: ticking one more lets go of the oldest tick that is not required.
        var radio = pick.Section.Spec is { Max: 1, Min: >= 1 };
        ToggleButton box = radio
            ? new RadioButton { GroupName = "ConsentModels-" + RuntimeHelpers.GetHashCode(pick), IsChecked = ticked, IsEnabled = choice.Ready, MinWidth = 0, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Top }
            : new CheckBox { IsChecked = ticked, IsEnabled = choice.Ready, MinWidth = 0, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Top };
        AutomationProperties.SetAutomationId(box, "ConsentModel-" + choice.Key);
        AutomationProperties.SetName(box, choice.Name + ", " + choice.Where + ", " + choice.Cost);
        box.IsCheckedChanged += (_, _) =>
        {
            if (box.IsChecked == true)
            {
                if (!pick.Order.Contains(choice.Key)) { pick.Order.Add(choice.Key); LetGo(pick, choice.Key); }
            }
            else if (!radio) pick.Order.Remove(choice.Key);                 // a round button is let go of by picking another, never by hand
            FillModels(pick);
            // a searched model ticked or unticked also changes the search list (its tick, and who is full)
            if (pick.Extra.IsVisible && pick.Results.Children.Count > 0) RefreshResultTicks(pick);
        };

        var name = Ui.Wrap(choice.Name, "strong");
        name.FontSize = 14;
        var about = choice.Where + " · " + choice.Cost + (choice.Note is { Length: > 0 } note ? " · " + note : "");
        // His own agent is not a model: it searches the web for the site, so the window says what comes with it.
        if (choice.Where == "your agent") about += " · it searches the web for this site; the guard looks at every request";
        var text = Ui.Column(1, name, Ui.Wrap(about, "muted", "small"));
        Control chip = choice.Role switch
        {
            ModelRole.Required => Ui.Chip("required", "ink"),
            ModelRole.Recommended => Ui.Chip("recommended", "outline"),
            _ => new Panel(),
        };
        var line = Ui.Columns("Auto,*,Auto", box, text, chip);
        text.Margin = new Thickness(8, 0, 6, 0);
        if (!choice.Ready) line.Opacity = 0.6;
        return line;
    }

    /// <summary>After a new tick: while more are ticked than the site uses, the oldest tick that is not required goes (the new one itself when nothing else can).</summary>
    static void LetGo(ModelPick pick, string keep)
    {
        var max = pick.Section.Spec.Max;
        while (pick.Order.Count > max)
        {
            var drop = pick.Order.FirstOrDefault(k => k != keep && pick.Known[k].Role != ModelRole.Required);
            if (drop is null) { pick.Order.Remove(keep); return; }
            pick.Order.Remove(drop);
        }
    }

    void RefreshResultTicks(ModelPick pick)
    {
        var shown = pick.Results.Children.OfType<Grid>().ToList();
        // The search list shows the same models again: redraw it with the current ticks.
        if (shown.Count == 0) return;
        var keys = pick.Results.Children.OfType<Grid>().Select(g => g.Children.OfType<ToggleButton>().First())
            .Select(c => AutomationProperties.GetAutomationId(c)?["ConsentModel-".Length..] ?? "").ToList();
        pick.Results.Children.Clear();
        foreach (var key in keys)
        {
            if (pick.Known.TryGetValue(key, out var choice)) pick.Results.Children.Add(ModelLine(pick, choice));
        }
    }

    /// <summary>Whether a skill's ticks give the site what it needs: at least its minimum, and every model it cannot work without.</summary>
    static bool Satisfied(ModelPick pick)
    {
        var ids = pick.Order.Select(k => pick.Known[k].ModelId).ToList();
        return pick.Order.Count >= pick.Section.Spec.Min && ModelPlanner.Satisfies(pick.Section.Spec, ids);
    }

    /// <summary>Allow is offered while every skill the site cannot work without can be given what it needs.</summary>
    void UpdateAllow()
    {
        var plain = _model.Rows.Any(r => r.Required && r.Models is null && !r.CanServe);
        var models = _picks.Any(p => p.Value.Required && !_leftOff.Contains(p.Key) && !Satisfied(p.Value));
        _allow.IsEnabled = !plain && !models;
    }

    /// <summary>What the site asked for above the visitor's limits, with one tick to accept it.</summary>
    Control LimitsBlock(LimitsAsk ask)
    {
        var wanted = ask.Wanted;
        var current = ask.Current;
        var lines = new List<string>();
        if (wanted.InputChars is { } input) lines.Add("a text of up to " + Number(input) + " characters at once (yours: " + Number(current.InputChars) + ")");
        if (wanted.AnswerTokens is { } tokens) lines.Add("answers of up to " + Number(tokens) + " tokens (yours: " + Number(current.AnswerTokens) + ")");
        if (wanted.CallSeconds is { } seconds) lines.Add("waiting up to " + Number(seconds) + " seconds for an answer (yours: " + Number(current.CallSeconds) + ")");
        if (wanted.ParallelCalls is { } parallel) lines.Add(Number(parallel) + " questions at once (yours: " + Number(current.ParallelCalls) + ")");
        if (wanted.CallsPerMinute is { } perMinute) lines.Add(Number(perMinute) + " questions a minute (yours: " + Number(current.CallsPerMinute) + ")");
        _siteLimits = new CheckBox { IsChecked = true, Content = Ui.Wrap("Allow this for the site. You can change it in Limits.", "small"), FontSize = 13 };
        AutomationProperties.SetAutomationId(_siteLimits, "ConsentSiteLimits");
        AutomationProperties.SetName(_siteLimits, "Allow the limits the site asks for");
        var text = Ui.Wrap("The site asks for more room than your limits give it:\n• " + string.Join("\n• ", lines), "muted");
        AutomationProperties.SetAutomationId(text, "ConsentLimitsAsk");
        return Ui.Box("row", Ui.Column(6, text, _siteLimits));
    }
}
