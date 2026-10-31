using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Byoa.Wallet.Engine;
using Byoa.Wallet.Sources;

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

    /// <summary>
    /// A text in a field: its first two lines, and "More" under it when there is more to read. Pressed, the field opens to the whole text, up to
    /// seventeen lines tall (with a scroll when it is longer than that), and "Less" folds it back.
    /// </summary>
    /// <param name="shown">What shows while it is folded.</param>
    /// <param name="whole">What shows when it is open; null when it is the same text.</param>
    static Control Expandable(string shown, string? whole, string id)
    {
        const int closedLines = 2;
        const int openLines = 17;
        var text = Ui.Wrap(shown);
        text.TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis;
        text.MaxLines = closedLines;
        AutomationProperties.SetAutomationId(text, id);
        var scroll = new ScrollViewer { Content = text, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var more = Ui.Link("More", id + "More", () => { });
        more.HorizontalAlignment = HorizontalAlignment.Right;
        var longer = whole is not null && !string.Equals(whole, shown, StringComparison.Ordinal);
        more.IsVisible = longer;
        var open = false;
        more.Click += (_, _) =>
        {
            open = !open;
            if (open)
            {
                text.Text = whole ?? shown;
                text.MaxLines = 0;
                text.TextTrimming = Avalonia.Media.TextTrimming.None;
                scroll.MaxHeight = Math.Round(text.FontSize * 1.45 * openLines);
                scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            }
            else
            {
                text.Text = shown;
                text.MaxLines = closedLines;
                text.TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis;
                scroll.MaxHeight = double.PositiveInfinity;
                scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
                scroll.Offset = default;
            }

            ((TextBlock)more.Content!).Text = open ? "Less" : "More";
        };
        // a text that is the same open and folded gets its button only when two lines are not enough at the width the window has now
        text.SizeChanged += (_, _) =>
        {
            if (open || longer || text.Bounds.Width <= 0) return;
            var probe = new TextBlock { Text = shown, TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontWeight = text.FontWeight, FontSize = text.FontSize, FontFamily = text.FontFamily };
            probe.Measure(new Size(text.Bounds.Width, double.PositiveInfinity));
            var full = probe.DesiredSize.Height;
            probe.MaxLines = closedLines;
            probe.Measure(new Size(text.Bounds.Width, double.PositiveInfinity));
            more.IsVisible = full > probe.DesiredSize.Height + 1;
        };
        return Ui.Column(4, scroll, more);
    }

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

        // The site's reason in a field: its first two lines, and all of it (what the site wrote, what it asks, what the visitor keeps) behind "More".
        var how = spec.Min == spec.Max ? (spec.Min == 1 ? "one model" : Number(spec.Min) + " different models") : Number(spec.Min) + " to " + Number(spec.Max) + " different models";
        var reason = spec.Why ?? "The site uses " + how + ".";
        var detail = "It uses " + how + (spec.ByVendor ? ", from different companies where possible" : "") + "."
            + (spec.Require.Count > 0 ? " It cannot work without " + string.Join(", ", spec.Require.Select(w => string.Join(" or ", w.AnyOf))) + "." : "")
            + (spec.Recommend.Count > 0 ? " It recommends " + string.Join(", ", spec.Recommend.Take(6)) + (spec.Recommend.Count > 6 ? " and " + Number(spec.Recommend.Count - 6) + " more" : "") + "." : "")
            + " You choose which of your models it gets; it never sees the others.";
        var whole = reason + "\n\n" + (spec.WhyLong ?? "The site did not say more. It asks for " + how + (spec.ByVendor ? ", from different companies where possible" : "") + ".") + "\n\n" + detail;
        panel.Children.Add(Ui.Box("field", Expandable(reason, whole, "ConsentModelsWhy")));
        if (NeedsChips(spec) is { } wanted) panel.Children.Add(wanted);

        if (section.MissingRequired.Count > 0 || section.Shortfall is not null)
        {
            var problem = section.MissingRequired.Count > 0
                ? "The site cannot work without " + string.Join(", ", section.MissingRequired) + "."
                : "The site " + section.Shortfall + ".";
            // what would fix it: a model that is what the site asked for
            var fix = ModelFit.NeedsWords(spec) is { Count: > 0 } words
                ? " Add a model that fits (" + string.Join(" · ", words) + "), or an OpenRouter key, and try again."
                : " Add a model, or an OpenRouter key, and try again.";
            var explanation = Ui.Wrap(problem + fix, "muted");
            AutomationProperties.SetAutomationId(explanation, "ConsentModelsProblem");
            panel.Children.Add(explanation);
            panel.Children.Add(Ui.Link("Add a model or a key…", "ConsentFix", () => _openSettings("add-model")));
        }

        pick.Count.Classes.Add("muted");
        AutomationProperties.SetAutomationId(pick.Count, "ConsentModelsCount");
        var change = Ui.Button("Change models", "tint", "ConsentModelsChange", () => { }, "small");
        change.HorizontalAlignment = HorizontalAlignment.Right;
        panel.Children.Add(Ui.Columns("*,Auto", pick.Count, change));
        panel.Children.Add(pick.List);
        if (MissesBlock(section) is { } misses) panel.Children.Add(misses);
        if (ExamplesBlock(section) is { } examples) panel.Children.Add(examples);
        panel.Children.Add(ExtraModels(pick));
        change.Click += (_, _) =>
        {
            pick.Expanded = !pick.Expanded;
            pick.Extra.IsVisible = pick.Expanded;
            change.Content = pick.Expanded ? "Fewer models" : "Change models";
            FillModels(pick);
        };

        FillModels(pick);
        var box = Ui.Box("row", panel);
        return box;
    }

    /// <summary>What the site asked of the models, as short chips under its reason ("reads pictures", "context 64k+", "your own agent"); null when it asked nothing.</summary>
    static Control? NeedsChips(RequestedModels spec)
    {
        var words = ModelFit.NeedsWords(spec);
        if (words.Count == 0) return null;
        var chips = new WrapPanel { Orientation = Orientation.Horizontal };
        var lead = Ui.Text("it wants", "muted", "small");
        lead.Margin = new Thickness(0, 3, 6, 4);
        lead.VerticalAlignment = VerticalAlignment.Top;
        chips.Children.Add(lead);
        foreach (var word in words)
        {
            var chip = Ui.Chip(word, "outline");
            chip.Margin = new Thickness(0, 0, 4, 4);
            chips.Children.Add(chip);
        }

        AutomationProperties.SetAutomationId(chips, "ConsentModelsNeeds");
        AutomationProperties.SetName(chips, "The site wants models that: " + string.Join(", ", words));
        return chips;
    }

    /// <summary>"3 of your models do not fit": a line that opens to the reason for each one the wallet left out of the list. Null when none was left out.</summary>
    static Control? MissesBlock(ModelSection section)
    {
        if (section.Misses.Count == 0) return null;
        var reasons = Ui.Column(3);
        reasons.Margin = new Thickness(18, 0, 0, 0);
        var count = section.Misses.Count;
        var title = Number(count) + (count == 1 ? " of your models does not fit" : " of your models do not fit");
        var toggle = Ui.Link(title, "ConsentModelsMisses", () => { });
        toggle.Click += (_, _) =>
        {
            var open = reasons.Children.Count == 0;
            reasons.Children.Clear();
            if (open)
            {
                // (the lines are made when it opens, not hidden and shown: a window that is read by other programs sees only what is there)
                for (var i = 0; i < count; i++)
                {
                    var line = Ui.Wrap(section.Misses[i].Name + ": " + section.Misses[i].Why, "muted", "small");
                    AutomationProperties.SetAutomationId(line, "ConsentModelsMiss-" + i.ToString(CultureInfo.InvariantCulture));
                    reasons.Children.Add(line);
                }
            }

            ((TextBlock)toggle.Content!).Text = title + (open ? " (hide)" : "");
        };
        AutomationProperties.SetHelpText(toggle, "Opens the reason for each");
        return Ui.Column(4, toggle, reasons);
    }

    /// <summary>The names the site likes that none of the visitor's models answers to, as examples with a way to add one. Null when there are none.</summary>
    Control? ExamplesBlock(ModelSection section)
    {
        if (section.ExampleNames.Count == 0) return null;
        var text = Ui.Wrap("The site suggests " + string.Join(", ", section.ExampleNames) + ". None of your models is one of these.", "muted", "small");
        AutomationProperties.SetAutomationId(text, "ConsentModelsExamples");
        return Ui.Column(3, text, Ui.Link("Add a model…", "ConsentModelsExampleAdd", () => _openSettings("add-model")));
    }

    /// <summary>"can: search the web, read web pages · may lack: look at pictures": what his agent can do, and what the site hoped for that it cannot.</summary>
    static string AgentAbilitiesLine(RequestedModels spec)
    {
        var line = "can: " + string.Join(", ", AgentProfile.Abilities.Select(AgentProfile.Words));
        return AgentProfile.Lacks(spec.Abilities) is { Count: > 0 } lacks ? line + " · may lack: " + string.Join(", ", lacks.Select(AgentProfile.Words)) : line;
    }

    /// <summary>What "Change models…" opens: a search of the public list, for the models behind the visitor's own OpenRouter key.</summary>
    Control ExtraModels(ModelPick pick)
    {
        pick.Extra.Children.Add(new Border { Height = 1, Background = Ui.Brush("Rule"), Margin = new Thickness(0, 2) });
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
            var found = _search(query.Text, pick.Section.Spec);
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
        // The ticked ones first, then the ones that fit, then the ones whose properties are not known, then the ones that fall short (shown, never ticked by the wallet).
        var ordered = pick.Known.Values
            .Where(c => !(c.IsNew && !pick.Order.Contains(c.Key) && !pick.Section.Choices.Any(x => x.Key == c.Key)))      // search results live in their own list
            .OrderBy(c => pick.Order.Contains(c.Key) ? pick.Order.IndexOf(c.Key) : int.MaxValue)
            .ThenBy(c => c.Fit)
            .ThenBy(c => c.Role == ModelRole.Required ? 0 : c.Role == ModelRole.Recommended ? 1 : 2)
            .ToList();
        var shown = pick.Expanded ? ordered : ordered.Where(c => pick.Order.Contains(c.Key) || c.Role != ModelRole.Yours || c.Fit != FitState.Fits).Take(CollapsedRows).ToList();
        if (MasterTick(pick, shown) is { } master) pick.List.Children.Add(master);
        foreach (var choice in shown) pick.List.Children.Add(ModelLine(pick, choice));
        if (!pick.Expanded && ordered.Count > shown.Count)
            pick.List.Children.Add(Ui.Wrap("and " + Number(ordered.Count - shown.Count) + " more of yours under “Change models”", "muted", "small"));
        if (shown.Count == 0) pick.List.Children.Add(Ui.Wrap("None of your models can answer this site yet.", "muted"));

        var limit = spec.Min == spec.Max ? "the site uses " + Number(spec.Max) : "the site uses " + Number(spec.Min) + " to " + Number(spec.Max);
        pick.Count.Text = Number(pick.Order.Count) + " ticked · " + limit;
        UpdateAllow();
    }

    /// <summary>One model, and the site cannot do without it: a choice among several, so round buttons (picking one lets go of the other).</summary>
    static bool IsChoiceOfOne(ModelPick pick) => pick.Section.Spec is { Max: 1, Min: >= 1 };

    /// <summary>
    /// The master box over a list of boxes. One press lets go of every tick (the visitor was handed a crowd of models and wants one); with nothing
    /// ticked, one press ticks the models the site takes first (its required ones, then its recommended ones, up to the most it uses).
    /// Its state shows all, some or none of them ticked. Null over round buttons (a choice of one) and over a list that has nothing to clear.
    /// </summary>
    Control? MasterTick(ModelPick pick, List<ModelChoice> shown)
    {
        if (IsChoiceOfOne(pick)) return null;
        var ready = shown.Where(c => c.Ready).ToList();
        if (ready.Count < 2 && pick.Order.Count < 2) return null;
        bool? state = pick.Order.Count == 0 ? false : ready.All(c => pick.Order.Contains(c.Key)) ? true : null;
        var master = new CheckBox
        {
            IsThreeState = true,
            IsChecked = state,
            MinWidth = 0,
            Padding = new Thickness(8, 0, 0, 0),
        };
        var hint = Ui.Text(pick.Order.Count > 0 ? "press to untick all" : "press to tick the first ones", "muted", "small");
        hint.Margin = new Thickness(8, 0, 0, 0);
        master.Content = Ui.Columns("Auto,*", Ui.Text("All of them", "strong"), hint);
        AutomationProperties.SetAutomationId(master, "ConsentModelsAll");
        AutomationProperties.SetName(master, "All models");
        ToolTip.SetTip(master, pick.Order.Count > 0 ? "Untick all the models" : "Tick the models the site takes first");
        master.IsCheckedChanged += (_, _) =>             // the state it was drawn with is set before this is hooked: only a press gets here
        {
            if (pick.Order.Count > 0) pick.Order.Clear();
            else foreach (var choice in ready.Take(pick.Section.Spec.Max)) pick.Order.Add(choice.Key);
            FillModels(pick);
            if (pick.Extra.IsVisible && pick.Results.Children.Count > 0) RefreshResultTicks(pick);
        };
        return master;
    }

    Control ModelLine(ModelPick pick, ModelChoice choice)
    {
        var ticked = pick.Order.Contains(choice.Key);
        // Otherwise boxes; a box is never greyed because the list is full: ticking one more lets go of the oldest tick that is not required.
        var radio = IsChoiceOfOne(pick);
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
        var agent = choice.Where == "your agent";
        if (agent)
        {
            var abilities = Ui.Wrap(AgentAbilitiesLine(pick.Section.Spec), "muted", "small");
            AutomationProperties.SetAutomationId(abilities, "ConsentAgentAbilities-" + choice.Key);
            text.Children.Add(abilities);
        }

        // What falls short, or what the wallet does not know: said in plain words under the name, in the warm colour when it is a known mismatch.
        if (choice.Marks is { Count: > 0 } marks)
        {
            var mark = Ui.Wrap(string.Join("; ", marks), "small");
            mark.Foreground = choice.Fit == FitState.Mismatch ? Ui.Brush("SignalDark") : Ui.Brush("Muted");
            AutomationProperties.SetAutomationId(mark, "ConsentModelMark-" + choice.Key);
            text.Children.Add(mark);
        }

        var chips = Ui.Row(4);
        if (agent) chips.Children.Add(Ui.Chip("agent", "outline"));
        chips.Children.Add(choice.Role switch
        {
            ModelRole.Required => Ui.Chip("required", "ink"),
            ModelRole.Recommended => Ui.Chip("recommended", "outline"),
            _ => new Panel(),
        });
        var line = Ui.Columns("Auto,*,Auto", box, text, chips);
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
        _allow.IsEnabled = !plain && !models && (_checkSame is null || _checkSame.IsChecked == true);
        // a button that does not work says why: the code has to be confirmed first (a hint shows on a disabled button too)
        var needsCode = _checkSame is { IsChecked: not true };
        ToolTip.SetShowOnDisabled(_allow, true);
        ToolTip.SetTip(_allow, needsCode ? "First confirm the code: tick “The code is the same on the site's page”. Allow works after that." : null);
        AutomationProperties.SetHelpText(_allow, needsCode ? "Confirm the code first: tick “The code is the same on the site's page”." : "");
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
