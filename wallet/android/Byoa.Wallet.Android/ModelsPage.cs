using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Byoa.Wallet.Providers;
using Byoa.Wallet.Registry;
using Byoa.Wallet.Secrets;
using Byoa.Wallet.Settings;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Android;

/// <summary>
/// My models, as on the desktop wallet: the models the wallet answers sites with (each one a line with "Use it", and under "Settings" what it is, its
/// place in the order of its skill, Test, Edit and Remove), the visitor's own OpenRouter key (paste, replace, remove after a question), the two ways to add
/// a model (on a server he names, or from the models behind his OpenRouter key), and the two switches about how models are started.
/// The wallet holds no model of its own.
/// </summary>
sealed class ModelsPage : ListPage
{
    readonly TextBox _key = new() { PasswordChar = '•', Watermark = "sk-or-…" };
    readonly TextBlock _keyResult = Kit.Text("", "small");
    readonly Dictionary<string, string> _results = new(StringComparer.Ordinal);
    readonly HashSet<string> _testing = new(StringComparer.Ordinal);
    readonly HashSet<string> _open = new(StringComparer.Ordinal);
    string? _confirmRemove;
    bool _confirmKey;

    public ModelsPage(Shell shell) : base(shell)
    {
        Kit.Named(_key, "KeyInput", "OpenRouter API key");
        AutomationProperties.SetAutomationId(_keyResult, "KeyResult");
    }

    public override string? Title => "My models";

    protected override void Fill()
    {
        Body.Children.Add(Kit.Text("The programs that run your models, and your keys. The site never learns which one answered, and a key goes only to its own provider. "
            + SecretProtectors.StorageSentence, "muted"));

        // the buttons come first: a long list must not push them off the screen
        Body.Children.Add(Kit.Button("Add a model on a server", "allow", "ModelsAdd", () => Shell.Push(new AddServerPage(Shell))));
        Body.Children.Add(Kit.Button("Add a model from OpenRouter", "deny", "ModelsAddOpenRouter", () => Shell.Push(new AddOpenRouterPage(Shell))));

        var settings = W.Store.Snapshot();
        var added = settings.Sources.Where(s => s.Kind is not null && !string.Equals(s.Kind, SourceKinds.Agent, StringComparison.OrdinalIgnoreCase)).ToList();
        var list = Kit.Column(10, Kit.Text("Your models", "label"));
        if (added.Count == 0) list.Children.Add(Kit.Text("No added models yet. Add a model that a server runs, or one behind your own OpenRouter key.", "muted"));
        foreach (var source in added) list.Children.Add(Row(source));
        Body.Children.Add(list);

        Body.Children.Add(KeyCard());

        // below the models: what applies to all of them
        Body.Children.Add(Kit.Rule(6, 2));
        // On by default: when a site connects, each model does its simplest job once for real and the connection waits for it.
        Body.Children.Add(Kit.Box("card", Kit.Column(4,
            Kit.Tick("Check the models when a site connects", "WarmUpOnConnect", settings.WarmUpOnConnect, on => W.Store.Update(s => s.WarmUpOnConnect = on)),
            Kit.Text("When a site connects, each model it will use does its simplest job once: text writes one short answer. The connection waits for that, so the site's first real request does not meet a model that is still starting. Turn it off to skip this: the first request may then be slow.", "muted", "small"))));
        // Off by default. On: the wallet never starts a model; one that is not in memory is left out of the route and gets nothing.
        Body.Children.Add(Kit.Box("card", Kit.Column(4,
            Kit.Tick("Never start a model for me", "NeverStartModels", settings.NeverStartModels, on => W.Store.Update(s => s.NeverStartModels = on)),
            Kit.Text("You start your models yourself. The wallet then makes no model do a warm-up job, and sends nothing to a model that is not in memory, so no program starts one because of a site: a site's question goes to the next model that is running, or the site hears that AI is unavailable. It works with programs that say what is in memory; for another server the wallet cannot tell, and it does not hold the model back.", "muted", "small"))));
    }

    // ------------------------------------------------------------------------------------------------
    // a model

    Control Row(SourceSetting source)
    {
        var info = W.Registry.ListSources().FirstOrDefault(s => s.Id == source.Id);
        var invalid = W.Sources.InvalidTextSources.FirstOrDefault(x => x.Id == source.Id);
        var state = invalid is not null ? "invalid: " + invalid.Message : info?.StateText ?? "configured";
        var fullName = info?.Name ?? source.Label ?? source.Model ?? source.Id;
        var name = fullName.Split(" · ")[0];                       // "model · this phone" -> "model": where it runs is in the opened settings
        var skill = Vocabulary.SkillName(source.Skills.FirstOrDefault() ?? Vocabulary.Generate);
        var use = Kit.Tick(name, "AddedSourceEnabled-" + source.Id, source.Enabled, on => W.Registry.EnableSource(source.Id, on));
        var header = Kit.Column(4, use, Kit.Chip(skill, "outline"), Kit.Text(source.Enabled ? state : "turned off", "muted", "small"));
        return Kit.Fold(_open, source.Id, "AddedSourceExpand-" + source.Id, name, header, () => Details(source, info, fullName, state), Redraw);
    }

    /// <summary>All the settings of a model the visitor added.</summary>
    Control Details(SourceSetting source, RegistrySourceInfo? info, string fullName, string state)
    {
        var id = SafeId(source.Id);
        var detail = state + " · " + string.Join(", ", source.Skills.Count == 0 ? ["text.generate"] : source.Skills)
            + (source.Languages.Count == 0 ? " · any language" : " · " + string.Join(", ", source.Languages));
        var card = Kit.Column(8,
            Kit.Text(fullName, "strong"),
            Kit.Text(source.Id, "mono", "small"),
            Kit.Text(detail, "muted"),
            Kit.Text((source.Endpoint is null ? "No endpoint" : source.Endpoint) + (source.Model is null ? "" : " · " + source.Model), "mono", "small"));
        if (source.Skills.Count == 0 || source.Skills.Contains(Vocabulary.Generate))
        {
            var known = KnownAs(source);
            card.Children.Add(Kit.Column(2,
                Kit.Tick("In my default set", "AddedSourceOffer-" + id, source.Offer, on => W.Store.Update(s => s.Source(source.Id).Offer = on)),
                Kit.Text("A site that asks for several models gets the ones in your default set first. Known as: " + (known.Length == 0 ? "unknown" : known) + ".", "muted", "small")));
            if (source.Kind is SourceKinds.OpenAiCompatible or SourceKinds.LmStudio or SourceKinds.Ollama)
            {
                card.Children.Add(Kit.Column(2,
                    Kit.Tick("Let the model think before it answers", "AddedSourceThinking-" + id, source.Thinking, on => W.Store.Update(s => s.Source(source.Id).Thinking = on)),
                    Kit.Text("A model that thinks first (Qwen3.5, DeepSeek-R1 and the like) can spend minutes before its first word. Untick this for quick answers. A site may also ask for no thinking. A model that does not think is not changed.", "muted", "small")));
                if (source.Kind == SourceKinds.OpenAiCompatible)
                {
                    var budget = source.ThinkingBudget;
                    card.Children.Add(Kit.Column(2,
                        Kit.Stepper("Thinking limit, tokens", budget == 0 ? "no limit" : budget.ToString("N0", CultureInfo.InvariantCulture), "AddedSourceThinkingBudget-" + id,
                            () => W.Store.Update(s => s.Source(source.Id).ThinkingBudget = Math.Max(0, budget - 500)),
                            () => W.Store.Update(s => s.Source(source.Id).ThinkingBudget = Math.Min(SourceSetting.MaxThinkingBudget, budget + 500)),
                            budget > 0, budget < SourceSetting.MaxThinkingBudget),
                        Kit.Text("The most the model may spend on thinking before it must answer. About 2000 gives it room and keeps the wait short. 0 means no limit.", "muted", "small")));
                }
            }
        }

        card.Children.Add(Order(source));
        card.Children.Add(Buttons(source));
        if (_results.TryGetValue(source.Id, out var result))
        {
            var text = Kit.Text(result, "small");
            AutomationProperties.SetAutomationId(text, "ModelTestResult-" + source.Id);
            card.Children.Add(text);
        }

        return card;
    }

    Control Buttons(SourceSetting source)
    {
        var buttons = new WrapPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };
        void Add(Control b)
        {
            b.Margin = new Thickness(0, 0, 8, 6);
            buttons.Children.Add(b);
        }

        var test = Kit.Button(_testing.Contains(source.Id) ? "Testing…" : "Test", "deny", "ModelTest-" + source.Id, () => Test(source.Id), "small");
        test.IsEnabled = !_testing.Contains(source.Id);
        Add(test);
        Add(Kit.Button("Edit", "deny", "AddedSourceEdit-" + source.Id, () => Shell.Push(source.Kind == SourceKinds.OpenRouter ? new AddOpenRouterPage(Shell, source) : new AddServerPage(Shell, source)), "small"));
        if (_confirmRemove == source.Id)
        {
            Add(Kit.Button("Remove it", "allow", "ModelRemoveYes-" + source.Id, () => Remove(source.Id), "small"));
            Add(Kit.Button("Keep it", "deny", "ModelRemoveNo-" + source.Id, () => { _confirmRemove = null; Redraw(); }, "small"));
        }
        else
        {
            Add(Kit.Button("Remove", "deny", "ModelRemove-" + source.Id, () => { _confirmRemove = source.Id; Redraw(); }, "small"));
        }

        return buttons;
    }

    /// <summary>The place of the model in the order of each of its skills (the wallet tries the first one first), with Up and Down.</summary>
    Control Order(SourceSetting source)
    {
        var panel = Kit.Column(6);
        var skills = source.Skills.Count == 0 ? [Vocabulary.Generate] : source.Skills;
        foreach (var skill in skills.Distinct(StringComparer.Ordinal))
        {
            var chain = W.Registry.ListChains(skill).FirstOrDefault(c => c.Language is null);
            var ids = chain?.OrderedSourceIds.ToList() ?? [];
            var index = ids.FindIndex(x => x == source.Id);
            var id = SafeId(source.Id) + "-" + SafeId(skill);
            if (index < 0)
            {
                panel.Children.Add(Kit.Columns("*,Auto", Kit.Text(Vocabulary.SkillName(skill) + " order", "label"),
                    Kit.Button("Add to order", "deny", "AddedSourceOrderAdd-" + id, () =>
                    {
                        ids.Add(source.Id);
                        W.Registry.SetOrder(skill, null, ids);
                    }, "small")));
                continue;
            }

            var up = Kit.Move(true, Vocabulary.SkillName(skill) + " order", "AddedSourceOrderUp-" + id, () => Move(skill, source.Id, -1));
            var down = Kit.Move(false, Vocabulary.SkillName(skill) + " order", "AddedSourceOrderDown-" + id, () => Move(skill, source.Id, 1));
            up.IsEnabled = index > 0;
            down.IsEnabled = index < ids.Count - 1;
            var place = Kit.Text(Vocabulary.SkillName(skill) + " order: " + (index + 1).ToString(CultureInfo.InvariantCulture) + " of " + ids.Count.ToString(CultureInfo.InvariantCulture), "label");
            AutomationProperties.SetAutomationId(place, "AddedSourceOrder-" + id);
            panel.Children.Add(Kit.Column(6, place, Kit.Row(6, up, down)));
        }

        return Kit.Box("field", panel);
    }

    void Move(string skill, string sourceId, int by)
    {
        var ids = W.Registry.ListChains(skill).FirstOrDefault(c => c.Language is null)?.OrderedSourceIds.ToList();
        if (ids is null) return;
        var i = ids.FindIndex(x => x == sourceId);
        var j = i + by;
        if (i < 0 || j < 0 || j >= ids.Count) return;
        (ids[i], ids[j]) = (ids[j], ids[i]);
        W.Registry.SetOrder(skill, null, ids);
    }

    static string SafeId(string value) => new(value.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());

    static string KnownAs(SourceSetting source)
    {
        if (!string.IsNullOrWhiteSpace(source.ModelId)) return ModelNames.Canon(source.ModelId);
        if (source.Kind == SourceKinds.OpenRouter) return ModelNames.Canon(source.Model);
        return ModelNames.FromLocal(source.Model);
    }

    async void Test(string id)
    {
        _testing.Add(id);
        _results.Remove(id);
        Redraw();
        try
        {
            var result = await W.Registry.TestSourceAsync(id, visitorInitiated: true).ConfigureAwait(true);
            _results[id] = result.Message;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _results[id] = "Test failed: " + e.GetType().Name;
        }
        finally
        {
            _testing.Remove(id);
            Redraw();
        }
    }

    void Remove(string id)
    {
        _confirmRemove = null;
        var result = W.Registry.RemoveSource(id);
        if (!result.Ok) _results[id] = result.Message;
        Redraw();
    }

    // ------------------------------------------------------------------------------------------------
    // the OpenRouter key

    Control KeyCard()
    {
        var has = W.Registry.ListSources().FirstOrDefault(s => s.Id == OpenRouterSource.SourceId)?.HasApiKey == true;
        var key = Kit.Column(8, Kit.Text("OpenRouter key", "label"),
            Kit.Text(has ? "A key is saved. It is never shown again." : "Optional. One key gives your sites hundreds of models, paid from your OpenRouter account.", has ? "strong" : "muted"));
        if (_confirmKey)
        {
            // the question of the desktop wallet's "Delete the saved key?": the key is not removed until he says so
            key.Children.Add(Kit.Box("field", Kit.Column(8,
                Kit.Text("Delete the saved key?", "strong"),
                Kit.Text("The wallet forgets your OpenRouter key, and the models behind it stop answering until you save a key again. Keep leaves the key in the wallet so you can use it again later.", "muted", "small"),
                Kit.Row(8, Kit.Button("Delete", "allow", "SecretDeleteConfirm", RemoveKey, "small"), Kit.Button("Keep", "deny", "SecretDeleteKeep", () => { _confirmKey = false; Redraw(); }, "small")))));
        }
        else
        {
            key.Children.Add(Kit.Detach(_key));
            var save = Kit.Button(has ? "Replace the key" : "Save the key", "deny", "KeySave", SaveKey);
            var buttons = Kit.Row(8, save);
            if (has) buttons.Children.Add(Kit.Button("Remove the key", "deny", "KeyRemove", () => { _confirmKey = true; Redraw(); }));
            key.Children.Add(buttons);
        }

        key.Children.Add(Kit.Detach(_keyResult));
        key.Children.Add(Kit.Text(SecretProtectors.StorageSentence, "muted", "small"));
        return Kit.Box("card", key);
    }

    async void SaveKey()
    {
        var text = _key.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            _keyResult.Text = "Paste the key into the box first.";
            return;
        }

        var result = await W.Registry.SetApiKeyAsync(OpenRouterSource.SourceId, text);
        _key.Text = "";
        _keyResult.Text = result.Message;
        Redraw();
    }

    void RemoveKey()
    {
        _confirmKey = false;
        try
        {
            W.Secrets.Remove(OpenRouterSource.SecretName);
            _keyResult.Text = "The key was removed.";
        }
        catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or IOException)
        {
            _keyResult.Text = SecretProtectors.FailureSentence;
        }

        Redraw();
    }
}
