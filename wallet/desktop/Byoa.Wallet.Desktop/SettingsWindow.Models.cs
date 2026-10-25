using System.Globalization;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Byoa.Wallet.Engine;
using Byoa.Wallet.Settings;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Desktop;

/// <summary>The settings for text: how much room a site has (the defaults, and a site's own), and which models a site is given.</summary>
sealed partial class SettingsWindow
{
    static string N(int value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>A number the visitor sets with the keyboard or the arrows; <paramref name="set"/> runs on every change he makes.</summary>
    Control NumberLimit(string label, int value, int min, int max, int step, Action<int> set, string id, string? hint = null)
    {
        var box = new NumericUpDown { Value = value, Minimum = min, Maximum = max, Increment = step, FormatString = "N0", Width = 150 };
        AutomationProperties.SetAutomationId(box, id);
        AutomationProperties.SetName(box, label);
        box.ValueChanged += (_, e) =>
        {
            if (e.NewValue is decimal v) set((int)v);
        };
        var column = Ui.Column(2, Ui.Columns("*,Auto", Ui.Text(label), box));
        if (hint is not null) column.Children.Add(Ui.Wrap(hint, "muted", "small"));
        return column;
    }

    /// <summary>
    /// The five text limits. For every site (<paramref name="site"/> null) they are the defaults; for one site they are its own, starting from the
    /// default, with what the site recommended beside them and a way back to the defaults.
    /// </summary>
    Control TextLimitsCard(SiteRecord? site)
    {
        var settings = _w.Store.Snapshot();
        var current = TextLimits.ForSite(settings.Limits, site is null ? null : settings.Site(site.Origin));
        var suffix = site is null ? "Text" : "SiteText-" + Host(site);
        void Changed(Action<WalletSettings> change)
        {
            _w.Store.Update(change);
            _w.Engine.LimitsChanged(site?.Origin);
        }

        var rows = new List<Control>
        {
            NumberLimit("Text a call may send (characters)", current.InputChars, 1_000, TextLimits.MaxInputChars, 10_000,
                v => Changed(s => { if (site is null) s.Limits.TextInputChars = v; else s.Site(site.Origin)!.TextInputChars = v; }), "LimitInput-" + suffix,
                "About 4 characters make a token. The wallet never takes more than " + N(TextLimits.MaxInputChars) + "."),
            NumberLimit("Longest answer (tokens)", current.AnswerTokens, 16, TextLimits.MaxAnswerTokens, 1_000,
                v => Changed(s => { if (site is null) s.Limits.TextAnswerTokens = v; else s.Site(site.Origin)!.TextAnswerTokens = v; }), "LimitAnswer-" + suffix,
                "The most a model may write for one question. Up to " + N(TextLimits.MaxAnswerTokens) + "."),
            NumberLimit("Longest wait for one answer (seconds)", current.CallSeconds, 5, TextLimits.MaxCallSeconds, 30,
                v => Changed(s => { if (site is null) s.Limits.TextCallSeconds = v; else s.Site(site.Origin)!.TextCallSeconds = v; }), "LimitWait-" + suffix,
                "A model that is still starting may need a minute or two. Up to " + N(TextLimits.MaxCallSeconds) + "."),
            NumberLimit("Questions at once", current.ParallelCalls, 1, TextLimits.MaxParallelCalls, 1,
                v => Changed(s => { if (site is null) s.Limits.ParallelCalls = v; else s.Site(site.Origin)!.ParallelCalls = v; }), "LimitParallel-" + suffix,
                "A model on this PC answers one at a time; the others wait their turn. Up to " + N(TextLimits.MaxParallelCalls) + "."),
            NumberLimit("Questions a minute", current.CallsPerMinute, 1, TextLimits.MaxCallsPerMinute, 10,
                v => Changed(s => { if (site is null) s.Limits.CallsPerMinute = v; else s.Site(site.Origin)!.CallsPerMinute = v; }), "LimitRate-" + suffix),
        };

        if (site is null)
        {
            rows.Add(Ui.Wrap("These are the limits of every site. A site may ask for more in its request, and you decide when it asks; a site can also be given limits of its own in Limits.", "muted", "small"));
            return Ui.Box("card", Ui.Column(10, rows.ToArray()));
        }

        var stored = settings.Site(site.Origin) ?? site;
        if (stored.Asked is { } asked)
        {
            var beyond = TextLimits.Beyond(current, new RequestedLimits(asked.InputChars, asked.AnswerTokens, asked.CallSeconds, asked.ParallelCalls, asked.CallsPerMinute));
            if (beyond is not null)
            {
                var parts = new List<string>();
                if (beyond.InputChars is { } input) parts.Add("text up to " + N(input) + " characters");
                if (beyond.AnswerTokens is { } tokens) parts.Add("answers up to " + N(tokens) + " tokens");
                if (beyond.CallSeconds is { } seconds) parts.Add("waiting up to " + N(seconds) + " seconds");
                if (beyond.ParallelCalls is { } parallel) parts.Add(N(parallel) + " questions at once");
                if (beyond.CallsPerMinute is { } perMinute) parts.Add(N(perMinute) + " questions a minute");
                var use = Ui.Button("Use these", "allow", "UseSiteLimits-" + Host(site), () =>
                {
                    Changed(s =>
                    {
                        var record = s.Site(site.Origin)!;
                        record.TextInputChars = beyond.InputChars ?? record.TextInputChars;
                        record.TextAnswerTokens = beyond.AnswerTokens ?? record.TextAnswerTokens;
                        record.TextCallSeconds = beyond.CallSeconds ?? record.TextCallSeconds;
                        record.ParallelCalls = beyond.ParallelCalls ?? record.ParallelCalls;
                        record.CallsPerMinute = beyond.CallsPerMinute ?? record.CallsPerMinute;
                    });
                    Render();
                }, "small");
                var recommends = Ui.Wrap("The site recommends: " + string.Join(", ", parts) + ".", "strong");
                AutomationProperties.SetAutomationId(recommends, "SiteRecommends-" + Host(site));
                rows.Add(Ui.Box("field", Ui.Column(6, recommends, use)));
            }
        }

        rows.Add(Ui.Button("Back to the defaults", "deny", "SiteTextLimitsReset-" + Host(site), () =>
        {
            Changed(s =>
            {
                var record = s.Site(site.Origin)!;
                record.TextInputChars = record.TextAnswerTokens = record.TextCallSeconds = record.ParallelCalls = record.CallsPerMinute = null;
            });
            Render();
        }, "small"));
        return Ui.Box("card", Ui.Column(10, rows.ToArray()));
    }

    static string Host(SiteRecord site)
    {
        Origins.TryParse(site.Origin, out var host, out _);
        return host.Length == 0 ? site.Origin : host;
    }

    /// <summary>The models a site that asked for several was given: in the visitor's order, with a way to take one away, add another, and hide their names.</summary>
    Control? SiteModelsBlock(SiteRecord site)
    {
        var entry = site.ModelsFor(Vocabulary.Generate);
        if (entry is null || entry.SourceIds.Count == 0) return null;
        var host = Host(site);
        var all = _w.Registry.ListSources(Vocabulary.Generate).Where(s => !s.BuiltIn || s.Kind is not null).ToList();
        string NameOf(string id) => all.FirstOrDefault(s => s.Id == id) is { } info ? info.Name : id;
        var ids = entry.SourceIds.ToList();

        var panel = new StackPanel { Spacing = 5 };
        panel.Children.Add(Ui.Text("Models this site is given", "label"));
        for (var i = 0; i < ids.Count; i++)
        {
            var index = i;
            var id = ids[i];
            var known = all.FirstOrDefault(s => s.Id == id);
            var up = Ui.Button("↑", "deny", "SiteModelUp-" + host + "-" + SafeId(id), () => Move(index, -1), "square");
            var down = Ui.Button("↓", "deny", "SiteModelDown-" + host + "-" + SafeId(id), () => Move(index, 1), "square");
            var remove = Ui.Button("Remove", "deny", "SiteModelRemove-" + host + "-" + SafeId(id), () =>
            {
                var next = ids.Where(x => x != id).ToList();
                _w.Engine.SetSiteModels(site.Origin, Vocabulary.Generate, next);
                Render();
            }, "small");
            up.IsEnabled = index > 0;
            down.IsEnabled = index < ids.Count - 1;
            var line = known is null ? "no longer in your models" : known.StateText;
            var text = Ui.Column(1, Ui.Wrap((index + 1).ToString(CultureInfo.InvariantCulture) + ".  " + NameOf(id), "strong"), Ui.Wrap(line, "muted", "small"));
            panel.Children.Add(Ui.Columns("*,Auto,Auto,Auto", text, up, down, remove));
        }

        void Move(int index, int by)
        {
            var j = index + by;
            if (j < 0 || j >= ids.Count) return;
            (ids[index], ids[j]) = (ids[j], ids[index]);
            _w.Engine.SetSiteModels(site.Origin, Vocabulary.Generate, ids);
            Render();
        }

        var others = all.Where(s => !ids.Contains(s.Id)).ToList();
        if (others.Count > 0)
        {
            var combo = new ComboBox { MinWidth = 280 };
            foreach (var other in others) combo.Items.Add(new ComboBoxItem { Content = other.Name, Tag = other.Id });
            combo.SelectedIndex = 0;
            AutomationProperties.SetAutomationId(combo, "SiteModelChoice-" + host);
            var add = Ui.Button("Add", "deny", "SiteModelAdd-" + host, () =>
            {
                if (combo.SelectedItem is not ComboBoxItem { Tag: string id }) return;
                ids.Add(id);
                _w.Engine.SetSiteModels(site.Origin, Vocabulary.Generate, ids);
                Render();
            }, "small");
            panel.Children.Add(Ui.Row(8, combo, add));
        }

        var names = new CheckBox { Content = "The site sees the names of its models", IsChecked = site.ShowModelNames };
        AutomationProperties.SetAutomationId(names, "SiteShowNames-" + host);
        names.IsCheckedChanged += (_, _) => _w.Engine.SetShowModelNames(site.Origin, names.IsChecked == true);
        panel.Children.Add(names);
        panel.Children.Add(Ui.Wrap("The site reaches only these models, by a code of its own, and never the others. A change reaches it at once.", "muted", "small"));
        return Ui.Box("field", panel);
    }

    /// <summary>"In my default set": the models offered first to a site that asks for several and names none the visitor has. Text models only.</summary>
    Control? OfferRow(SourceSetting source)
    {
        if (source.Skills.Count > 0 && !source.Skills.Contains(Vocabulary.Generate)) return null;
        var offer = new CheckBox { Content = "In my default set", IsChecked = source.Offer };
        AutomationProperties.SetAutomationId(offer, "AddedSourceOffer-" + SafeId(source.Id));
        offer.IsCheckedChanged += (_, _) => _w.Store.Update(s => s.Source(source.Id).Offer = offer.IsChecked == true);
        var known = ModelNamesOf(source);
        return Ui.Column(2, offer, Ui.Wrap("A site that asks for several models gets the ones in your default set first. Known as: " + (known.Length == 0 ? "unknown" : known) + ".", "muted", "small"));
    }

    /// <summary>"Let the model think": a model that reasons before it answers can be told to answer at once. Text models that run on this PC or a server of yours, and GLM at Z.ai.</summary>
    Control? ThinkingRow(SourceSetting source)
    {
        if (source.Skills.Count > 0 && !source.Skills.Contains(Vocabulary.Generate)) return null;
        var glm = source.Kind == ApiServices.Kind && (source.Service is "zai" or "zai-coding");
        if (source.Kind is not (SourceKinds.OpenAiCompatible or SourceKinds.LmStudio or SourceKinds.Ollama) && !glm) return null;
        var think = new CheckBox { Content = "Let the model think before it answers", IsChecked = source.Thinking };
        AutomationProperties.SetAutomationId(think, "AddedSourceThinking-" + SafeId(source.Id));
        think.IsCheckedChanged += (_, _) => _w.Store.Update(s => s.Source(source.Id).Thinking = think.IsChecked == true);
        var column = Ui.Column(2, think, Ui.Wrap(glm
            ? "GLM-5.3 always reasons a little. Unticked, the wallet asks for its lowest level of reasoning, so the answer comes quickly. A site may also ask for this."
            : "A model that thinks first (Qwen3.5, DeepSeek-R1 and the like) can spend minutes before its first word. Untick this for quick answers. A site may also ask for no thinking. A model that does not think is not changed.", "muted", "small"));
        if (source.Kind == SourceKinds.OpenAiCompatible) column.Children.Add(ThinkingBudgetRow(source));
        return column;
    }

    /// <summary>"Thinking limit": the most tokens a thinking model of the Llama app may spend on reasoning before it must answer (0 is no limit).</summary>
    Control ThinkingBudgetRow(SourceSetting source)
    {
        var box = new TextBox { Text = source.ThinkingBudget.ToString(System.Globalization.CultureInfo.InvariantCulture), Width = 90, MinWidth = 90 };
        AutomationProperties.SetAutomationId(box, "AddedSourceThinkingBudget-" + SafeId(source.Id));
        box.TextChanged += (_, _) =>
        {
            // Only a whole number counts; anything else is left as typed and not saved until it is one.
            if (!int.TryParse(box.Text?.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var tokens)) return;
            var kept = Math.Clamp(tokens, 0, SourceSetting.MaxThinkingBudget);
            // Avalonia raises TextChanged a moment after a box is made, with the text it was made with. That is no edit: saving it
            // would redraw the page, which makes a new box, which raises it again - the page would flash twice a second.
            if (_w.Store.Read(s => s.Sources.FirstOrDefault(x => x.Id == source.Id)?.ThinkingBudget) == kept) return;
            _w.Store.Update(s => s.Source(source.Id).ThinkingBudget = kept);
        };
        box.LostFocus += (_, _) => box.Text = _w.Store.Read(s => s.Sources.FirstOrDefault(x => x.Id == source.Id)?.ThinkingBudget ?? source.ThinkingBudget)
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Ui.Column(2,
            Ui.Row(8, Ui.Text("Thinking limit, tokens", "small"), box),
            Ui.Wrap("The most the model may spend on thinking before it must answer. A small model can think in circles for minutes; " +
                    "about 2000 gives it room and keeps the wait short. 0 means no limit. Works with the Llama app and other llama.cpp servers.", "muted", "small"));
    }

    static string ModelNamesOf(SourceSetting source)
    {
        if (!string.IsNullOrWhiteSpace(source.ModelId)) return ModelNames.Canon(source.ModelId);
        if (source.Kind == SourceKinds.OpenRouter) return ModelNames.Canon(source.Model);
        if (source.Kind == ApiServices.Kind)
        {
            var vendor = ApiServices.Find(source.Service)?.Vendor;
            var tail = ModelNames.Canon(source.Model);
            return vendor is null || tail.Contains('/') ? tail : vendor + "/" + tail;
        }

        return ModelNames.FromLocal(source.Model);
    }
}
