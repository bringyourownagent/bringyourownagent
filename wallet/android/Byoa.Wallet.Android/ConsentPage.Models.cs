using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Byoa.Wallet.Engine;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Android;

/// <summary>
/// The part of the consent for a site that asks for several models: the site's reason (a line, and more behind a button), the models to tick
/// with what each costs and where it runs, a way to add more from the visitor's own key, and what the site may see. The rules are the desktop
/// window's: the short list and "Show more", a tick never moves a row, a choice of one is round buttons, and "All of them" ticks or unticks the first ones.
/// </summary>
sealed partial class ConsentPage
{
    /// <summary>What the visitor has ticked for one skill, and the controls that show it.</summary>
    sealed class ModelPick(ModelSection section)
    {
        public ModelSection Section { get; } = section;
        public List<string> Order { get; } = [];
        public Dictionary<string, ModelChoice> Known { get; } = new(StringComparer.Ordinal);
        public StackPanel List { get; } = new() { Spacing = 2 };
        public TextBlock Count { get; } = new() { TextWrapping = TextWrapping.Wrap };

        /// <summary>The order of the list: set when the page opens, a model added from the search goes to its end. A tick never moves a row.</summary>
        public List<string> Shelf { get; } = [];

        /// <summary>The rows the short list shows: those ticked or recommended when the page opened, and those ticked or added since.</summary>
        public HashSet<string> Featured { get; } = new(StringComparer.Ordinal);

        /// <summary>The list shows every model ("Show more"), not only the featured ones.</summary>
        public bool Expanded { get; set; }

        public bool Required { get; set; }

        /// <summary>The text section's controls keep their automation ids; the picture section's end with "Pictures" (a model in both sections has a tick in each).</summary>
        public string Tag { get; } = section.Skill == Vocabulary.ImageGenerate ? "Pictures" : "";

        public string Id(string id) => id + Tag;
    }

    // The site's recommendations and the visitor's own models shown before "Show more".
    const int CollapsedRows = 6;

    Control ModelRow(ConsentRow row, StackPanel panel, Border header)
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
        // The order the list keeps from now on (the ticked ones first, then the ones that fit, then the site's), and the rows of the short list.
        var first = pick.Known.Values
            .OrderBy(c => pick.Order.Contains(c.Key) ? pick.Order.IndexOf(c.Key) : int.MaxValue)
            .ThenBy(c => c.Fit)
            .ThenBy(c => c.Role == ModelRole.Required ? 0 : c.Role == ModelRole.Recommended ? 1 : 2)
            .ToList();
        pick.Shelf.AddRange(first.Select(c => c.Key));
        foreach (var c in first.Where(c => pick.Order.Contains(c.Key) || c.Role != ModelRole.Yours || c.Fit != FitState.Fits).Take(CollapsedRows)) pick.Featured.Add(c.Key);
        foreach (var key in pick.Order) pick.Featured.Add(key);

        // The site's reason in a field: its first two lines, and all of it (what the site wrote, what it asks, what the visitor keeps) behind "More".
        var how = spec.Min == spec.Max ? (spec.Min == 1 ? "one model" : Number(spec.Min) + " different models") : Number(spec.Min) + " to " + Number(spec.Max) + " different models";
        var reason = spec.Why ?? "The site uses " + how + ".";
        var detail = "It uses " + how + (spec.ByVendor ? ", from different companies where possible" : "") + "."
            + (spec.Require.Count > 0 ? " It cannot work without " + string.Join(", ", spec.Require.Select(w => string.Join(" or ", w.AnyOf))) + "." : "")
            + (spec.Recommend.Count > 0 ? " It recommends " + string.Join(", ", spec.Recommend.Take(6)) + (spec.Recommend.Count > 6 ? " and " + Number(spec.Recommend.Count - 6) + " more" : "") + "." : "")
            + " You choose which of your models it gets; it never sees the others.";
        var whole = reason + "\n\n" + (spec.WhyLong ?? "The site did not say more. It asks for " + how + (spec.ByVendor ? ", from different companies where possible" : "") + ".") + "\n\n" + detail;
        panel.Children.Add(Kit.Box("field", Expandable(reason, whole, pick.Id("ConsentModelsWhy"))));
        if (NeedsChips(spec, pick.Id("ConsentModelsNeeds")) is { } wanted) panel.Children.Add(wanted);

        if (section.MissingRequired.Count > 0 || section.Shortfall is not null)
        {
            var problem = section.MissingRequired.Count > 0
                ? "The site cannot work without " + string.Join(", ", section.MissingRequired) + "."
                : "The site " + section.Shortfall + ".";
            // what would fix it: a model that is what the site asked for
            var fix = ModelFit.NeedsWords(spec) is { Count: > 0 } words
                ? " Add a model that fits (" + string.Join(" · ", words) + "), or an OpenRouter key, and try again."
                : " Add a model, or an OpenRouter key, and try again.";
            var explanation = Kit.Text(problem + fix, "muted");
            AutomationProperties.SetAutomationId(explanation, pick.Id("ConsentModelsProblem"));
            panel.Children.Add(explanation);
        }

        pick.Count.Classes.Add("muted");
        AutomationProperties.SetAutomationId(pick.Count, pick.Id("ConsentModelsCount"));
        // "Add models" opens a page over this one to search the models behind the visitor's key; "Show more" under the list opens the list itself.
        var add = Kit.Button("Add models", "tint", pick.Id("ConsentModelsAdd"), () => AddModels(pick), "small");
        add.HorizontalAlignment = HorizontalAlignment.Right;
        panel.Children.Add(Kit.Columns("*,Auto", pick.Count, add));
        panel.Children.Add(pick.List);
        if (MissesBlock(section, pick) is { } misses) panel.Children.Add(misses);
        if (ExamplesBlock(section, pick) is { } examples) panel.Children.Add(examples);
        FillModels(pick);
        return Block(header, panel, row.Skill);
    }

    /// <summary>What the site asked of the models, as short chips under its reason ("reads pictures", "context 64k+"); null when it asked nothing.</summary>
    static Control? NeedsChips(RequestedModels spec, string id)
    {
        var words = ModelFit.NeedsWords(spec);
        if (words.Count == 0) return null;
        var chips = new WrapPanel { Orientation = Orientation.Horizontal };
        var lead = Kit.Text("it wants", "muted", "small");
        lead.Margin = new Thickness(0, 3, 6, 4);
        chips.Children.Add(lead);
        foreach (var word in words)
        {
            var chip = Kit.Chip(word, "outline");
            chip.Margin = new Thickness(0, 0, 4, 4);
            chips.Children.Add(chip);
        }

        AutomationProperties.SetAutomationId(chips, id);
        AutomationProperties.SetName(chips, "The site wants models that: " + string.Join(", ", words));
        return chips;
    }

    /// <summary>"3 of your models do not fit": a line that opens to the reason for each one the wallet left out of the list. Null when none was left out.</summary>
    static Control? MissesBlock(ModelSection section, ModelPick pick)
    {
        if (section.Misses.Count == 0) return null;
        var reasons = Kit.Column(3);
        reasons.Margin = new Thickness(12, 0, 0, 0);
        var count = section.Misses.Count;
        var title = Number(count) + (count == 1 ? " of your models does not fit" : " of your models do not fit");
        var toggle = Kit.Link(title, pick.Id("ConsentModelsMisses"), () => { });
        toggle.HorizontalAlignment = HorizontalAlignment.Left;
        toggle.Click += (_, _) =>
        {
            var open = reasons.Children.Count == 0;
            reasons.Children.Clear();
            if (open)
            {
                // (the lines are made when it opens, not hidden and shown: a screen that is read by other programs sees only what is there)
                for (var i = 0; i < count; i++)
                {
                    var line = Kit.Text(section.Misses[i].Name + ": " + section.Misses[i].Why, "muted", "small");
                    AutomationProperties.SetAutomationId(line, pick.Id("ConsentModelsMiss") + "-" + i.ToString(CultureInfo.InvariantCulture));
                    reasons.Children.Add(line);
                }
            }

            ((TextBlock)toggle.Content!).Text = title + (open ? " (hide)" : "");
        };
        AutomationProperties.SetHelpText(toggle, "Opens the reason for each");
        return Kit.Column(2, toggle, reasons);
    }

    /// <summary>The names the site likes that none of the visitor's models answers to, as examples. Null when there are none.</summary>
    static Control? ExamplesBlock(ModelSection section, ModelPick pick)
    {
        if (section.ExampleNames.Count == 0) return null;
        var text = Kit.Text("The site suggests " + string.Join(", ", section.ExampleNames) + ". None of your models is one of these. Add one in My models to give it to the site next time.", "muted", "small");
        AutomationProperties.SetAutomationId(text, pick.Id("ConsentModelsExamples"));
        return text;
    }

    /// <summary>
    /// "Add models": a page over this one with a search of the public list, for the models behind the visitor's own OpenRouter key. Each model
    /// found has "Add": it goes to the end of the list, not ticked (the visitor ticks it there). "Done" (or Back) closes the page, and the rows added
    /// show in the list. Without a key it says where to get one.
    /// </summary>
    void AddModels(ModelPick pick)
    {
        if (_closeOverlay is not null) return;
        var canSearch = _w.Engine.HasOpenRouterKey() && _w.Engine.Catalog.Loaded;
        var body = new StackPanel { Spacing = 10, Margin = new Thickness(16, 14, 16, 14) };
        var results = new StackPanel { Spacing = 8 };
        TextBox? query = null;
        if (!canSearch)
        {
            body.Children.Add(Kit.Text("To pick from hundreds more models, add your OpenRouter key in My models. Models you already have are in the list.", "muted"));
        }
        else
        {
            body.Children.Add(Kit.Text("Models behind your OpenRouter key. A model you add goes to the end of your list, not ticked: tick it there to give it to the site. It is paid from the key.", "muted", "small"));
            query = new TextBox { Watermark = "Search models (OpenRouter)" };
            Kit.Named(query, pick.Id("ConsentModelsSearch"), "Search models");
            body.Children.Add(query);
            void Refresh()
            {
                results.Children.Clear();
                var found = _w.Engine.SearchNewModels(query.Text, pick.Section.Spec, skill: pick.Section.Skill);
                if (found.Count == 0)
                {
                    results.Children.Add(Kit.Text("No model found. Try fewer words.", "muted", "small"));
                    return;
                }

                foreach (var choice in found.Take(20))
                {
                    pick.Known.TryAdd(choice.Key, choice);
                    results.Children.Add(ResultLine(pick, pick.Known[choice.Key]));
                }
            }

            query.TextChanged += (_, _) => Refresh();
            Refresh();
        }

        body.Children.Add(results);
        var done = Kit.Button("Done", "allow", pick.Id("ConsentModelsDone"), () => _closeOverlay?.Invoke());
        done.HorizontalAlignment = HorizontalAlignment.Stretch;
        var page = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Background = Brushes.White };
        var heading = new Border
        {
            Padding = new Thickness(16, 12), BorderBrush = Kit.Brush("WinRule"), BorderThickness = new Thickness(0, 0, 0, 1), Background = Kit.Brush("WinBar"),
            Child = Kit.Text("Add models", "h2"),
        };
        var scroll = new ScrollViewer { Content = body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var foot = new Border { Padding = new Thickness(14, 10, 14, 12), BorderBrush = Kit.Brush("WinRule"), BorderThickness = new Thickness(0, 1, 0, 0), Background = Kit.Brush("WinBar"), Child = done };
        Grid.SetRow(heading, 0);
        Grid.SetRow(scroll, 1);
        Grid.SetRow(foot, 2);
        page.Children.Add(heading);
        page.Children.Add(scroll);
        page.Children.Add(foot);
        AutomationProperties.SetAutomationId(page, pick.Id("ConsentModelsAddWindow"));
        _frame.Children.Add(page);
        _closeOverlay = () =>
        {
            _frame.Children.Remove(page);
            _closeOverlay = null;
            FillModels(pick);
        };
        if (query is not null) Dispatcher.UIThread.Post(() => query.Focus(), DispatcherPriority.Background);
    }

    /// <summary>A model found by the search: what it is, and "Add" (or "Added" once it is in the list).</summary>
    Control ResultLine(ModelPick pick, ModelChoice choice)
    {
        var text = Describe(pick, choice);
        var inList = pick.Shelf.Contains(choice.Key);
        var add = Kit.Button(inList ? "Added" : "Add", "tint", "ConsentModelAdd" + pick.Tag + "-" + choice.Key, () => { }, "small");
        add.IsEnabled = !inList && choice.Ready;
        add.VerticalAlignment = VerticalAlignment.Center;
        var line = Kit.Columns("*,Auto", text, add);
        add.Margin = new Thickness(8, 0, 0, 0);
        add.Click += (_, _) =>
        {
            if (pick.Shelf.Contains(choice.Key)) return;
            pick.Shelf.Add(choice.Key);
            pick.Featured.Add(choice.Key);
            add.IsEnabled = false;
            add.Content = "Added";
        };
        if (!choice.Ready) line.Opacity = 0.6;
        return line;
    }

    /// <summary>
    /// Draws the models again in the order the list keeps (a tick never moves a row): the short list, or every model once "Show more" is pressed.
    /// </summary>
    void FillModels(ModelPick pick)
    {
        var spec = pick.Section.Spec;
        pick.List.Children.Clear();
        var all = pick.Shelf.Where(pick.Known.ContainsKey).Select(k => pick.Known[k]).ToList();
        var shown = pick.Expanded ? all : all.Where(c => pick.Featured.Contains(c.Key)).ToList();
        if (MasterTick(pick, shown) is { } master) pick.List.Children.Add(master);
        foreach (var choice in shown) pick.List.Children.Add(ModelLine(pick, choice));

        if (shown.Count == 0) pick.List.Children.Add(Kit.Text("None of your models can answer this site yet.", "muted"));
        var more = all.Count(c => !pick.Featured.Contains(c.Key));
        if (more > 0)
        {
            var toggle = Kit.Link(pick.Expanded ? "Show less" : "Show more (" + Number(more) + ")", pick.Id("ConsentModelsMore"), () =>
            {
                pick.Expanded = !pick.Expanded;
                FillModels(pick);
            });
            toggle.HorizontalAlignment = HorizontalAlignment.Left;
            pick.List.Children.Add(toggle);
        }

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
        var master = new CheckBox { IsThreeState = true, IsChecked = state };
        var hint = Kit.Text(pick.Order.Count > 0 ? "press to untick all" : "press to tick the first ones", "muted", "small");
        master.Content = Kit.Column(0, Kit.Text("All of them", "strong"), hint);
        Kit.Named(master, pick.Id("ConsentModelsAll"), "All models");
        master.IsCheckedChanged += (_, _) =>             // the state it was drawn with is set before this is hooked: only a press gets here
        {
            if (pick.Order.Count > 0) pick.Order.Clear();
            else foreach (var choice in ready.Take(pick.Section.Spec.Max)) { pick.Order.Add(choice.Key); pick.Featured.Add(choice.Key); }
            FillModels(pick);
        };
        return master;
    }

    Control ModelLine(ModelPick pick, ModelChoice choice)
    {
        var ticked = pick.Order.Contains(choice.Key);
        // Otherwise boxes; a box is never greyed because the list is full: ticking one more lets go of the oldest tick that is not required.
        var radio = IsChoiceOfOne(pick);
        var text = Describe(pick, choice);
        ToggleButton box = radio
            ? new RadioButton { GroupName = "ConsentModels-" + pick.Tag + pick.Section.Skill, IsChecked = ticked, IsEnabled = choice.Ready, Content = text }
            : new CheckBox { IsChecked = ticked, IsEnabled = choice.Ready, Content = text };
        Kit.Named(box, "ConsentModel" + pick.Tag + "-" + choice.Key, choice.Name + ", " + choice.Where + ", " + choice.Cost);
        box.IsCheckedChanged += (_, _) =>
        {
            if (box.IsChecked == true)
            {
                if (!pick.Order.Contains(choice.Key)) { pick.Order.Add(choice.Key); LetGo(pick, choice.Key); }
                pick.Featured.Add(choice.Key);                              // still shown when the list is short again
            }
            else if (!radio) pick.Order.Remove(choice.Key);                 // a round button is let go of by picking another, never by hand
            FillModels(pick);
        };
        if (!choice.Ready) box.Opacity = 0.6;
        return box;
    }

    /// <summary>What a model is, under its name (where it runs, what it costs, what falls short), and its chips: in the list and in the search.</summary>
    static StackPanel Describe(ModelPick pick, ModelChoice choice)
    {
        var name = Kit.Text(choice.Name, "strong");
        var about = choice.Where + " · " + choice.Cost + (choice.Note is { Length: > 0 } note ? " · " + note : "");
        var text = Kit.Column(1, name, Kit.Text(about, "muted", "small"));
        // What falls short, or what the wallet does not know: said in plain words under the name, in the warm colour when it is a known mismatch.
        if (choice.Marks is { Count: > 0 } marks)
        {
            var mark = Kit.Text(string.Join("; ", marks), "small");
            mark.Foreground = choice.Fit == FitState.Mismatch ? Kit.Brush("SignalDark") : Kit.Brush("Muted");
            AutomationProperties.SetAutomationId(mark, "ConsentModelMark" + pick.Tag + "-" + choice.Key);
            text.Children.Add(mark);
        }

        if (choice.Role != ModelRole.Yours)
        {
            var chip = choice.Role == ModelRole.Required ? Kit.Chip("required", "ink") : Kit.Chip("recommended", "outline");
            chip.Margin = new Thickness(0, 2, 0, 0);
            text.Children.Add(chip);
        }

        return text;
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

    /// <summary>Whether a skill's ticks give the site what it needs: at least its minimum, and every model it cannot work without.</summary>
    static bool Satisfied(ModelPick pick)
    {
        var ids = pick.Order.Select(k => pick.Known[k].ModelId).ToList();
        return pick.Order.Count >= pick.Section.Spec.Min && ModelPlanner.Satisfies(pick.Section.Spec, ids);
    }
}
