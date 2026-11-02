using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Byoa.Wallet.Engine;
using Byoa.Wallet.Registry;
using Byoa.Wallet.Settings;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Android;

/// <summary>
/// "Add a model from OpenRouter": the models behind the visitor's own OpenRouter key, as the desktop wallet's Add a model offers them. The wallet's list
/// of OpenRouter's models (read once a day) is searched, narrowed to all, cheap or free ones, and the visitor ticks the ones he wants, or types a
/// model id himself. One short question is sent to the first of them to show that the key works (it costs a fraction of a cent), and Add works only
/// after that test has passed. Every ticked model becomes a model of his, behind the one key. Given a model he added before, the page edits it.
/// </summary>
sealed class AddOpenRouterPage : Page
{
    const string Endpoint = "https://openrouter.ai/api/v1";

    readonly SourceSetting? _editing;
    readonly HashSet<string> _chosen = new(StringComparer.Ordinal);
    readonly TextBox _filter = new() { Watermark = "Search the models by name" };
    readonly TextBox _model = new() { Watermark = "or a model id, like google/gemma-3-12b-it" };
    readonly TextBox _label = new() { Watermark = "optional friendly name" };
    readonly StackPanel _picker = new() { Spacing = 8 };
    readonly TextBlock _picked = Kit.Text("", "muted", "small");
    readonly TextBlock _result = Kit.Text("", "small");
    readonly Button _test;
    readonly Button _add;
    CatalogFilter _kind = CatalogFilter.All;
    StackPanel? _filters;
    bool _tested;

    public AddOpenRouterPage(Shell shell, SourceSetting? editing = null) : base(shell)
    {
        _editing = editing;
        Kit.Named(_filter, "CatalogSearch", "Search the models");
        Kit.Named(_model, "OpenRouterModel", "Model id");
        Kit.Named(_label, "OpenRouterLabel", "Friendly name");
        AutomationProperties.SetAutomationId(_result, "OpenRouterResult");
        AutomationProperties.SetAutomationId(_picked, "OpenRouterPicked");
        _test = Kit.Button("Test", "deny", "OpenRouterTest", Test);
        _add = Kit.Button(editing is null ? "Add" : "Save", "allow", "OpenRouterSave", Save);
        _test.IsEnabled = _add.IsEnabled = false;
        if (editing is not null)
        {
            _model.Text = editing.Model;
            _label.Text = editing.Label;
        }

        _filter.TextChanged += (_, _) => FillPicker();
        _model.TextChanged += (_, _) => Changed();
        _label.TextChanged += (_, _) => Changed();
        UpdateButtons();
    }

    public override string? Title => _editing is null ? "Add a model from OpenRouter" : "Edit a model";

    bool HasKey => W.Engine.HasOpenRouterKey();

    protected override Control Build()
    {
        var body = Kit.Column(12);
        body.Margin = new Thickness(16, 12, 16, 24);
        if (!HasKey && _editing is null)
        {
            body.Children.Add(Kit.Text("The models of OpenRouter are paid from your own OpenRouter key. Save the key first, in My models, and come back.", "muted"));
            return new ScrollViewer { Content = body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        }

        body.Children.Add(Kit.Text("The wallet holds no model: pick the ones you want from OpenRouter. They are paid from your key, and a test costs a fraction of a cent.", "muted"));
        if (_editing is null)
        {
            body.Children.Add(Kit.Text("Models", "label"));
            body.Children.Add(CatalogPicker());
            body.Children.Add(Kit.Text("Or type a model id yourself:", "muted", "small"));
        }
        else
        {
            body.Children.Add(Kit.Text("Model", "label"));
        }

        body.Children.Add(_model);
        body.Children.Add(_picked);
        body.Children.Add(Kit.Text("Name", "label"));
        body.Children.Add(_label);
        body.Children.Add(Kit.Text("A name labels one model; with several ticked each keeps its own name.", "muted", "small"));
        body.Children.Add(Kit.Row(10, _test, _add));
        body.Children.Add(_result);
        UpdatePicked();
        return new ScrollViewer { Content = body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    }

    Control CatalogPicker()
    {
        var column = Kit.Column(8);
        if (!W.Engine.Catalog.Loaded)
        {
            column.Children.Add(Kit.Text("The list of models is not loaded yet (the wallet reads it from OpenRouter once a day). Type a model id below, or try again in a minute.", "muted", "small"));
            return column;
        }

        _filters = Kit.Row(6);
        FillFilters();
        column.Children.Add(_filters);
        column.Children.Add(_filter);
        column.Children.Add(_picker);
        FillPicker();
        return column;
    }

    void FillFilters()
    {
        _filters!.Children.Clear();
        foreach (var (kind, label) in new[] { (CatalogFilter.All, "All"), (CatalogFilter.Cheap, "Cheap"), (CatalogFilter.Free, "Free") })
        {
            _filters.Children.Add(Kit.Button(label, _kind == kind ? "allow" : "deny", "Catalog" + label, () =>
            {
                _kind = kind;
                FillFilters();
                FillPicker();
            }, "small"));
        }
    }

    /// <summary>The ticks of the model list: the catalog, narrowed by the kind and by what was typed.</summary>
    void FillPicker()
    {
        _picker.Children.Clear();
        if (!W.Engine.Catalog.Loaded) return;
        var query = _filter.Text?.Trim() ?? "";
        var have = W.Store.Read(s => s.Sources.Select(x => x.Id).ToHashSet(StringComparer.Ordinal));
        var found = W.Engine.Catalog.Search(query, _kind, 25);
        if (found.Count == 0) _picker.Children.Add(Kit.Text("No model matches. Try fewer words, or type a model id below.", "muted", "small"));
        foreach (var m in found)
        {
            var info = m.Free ? "free" : m.TypicalAnswerCost is { } cost ? "about " + Money.Show(Math.Max(0.000001m, cost)) + " an answer" : "price unknown";
            if (have.Contains(SourceKinds.OpenRouter + ":" + m.Id)) info += " · already added";
            if (m.ContextLength > 0) info += " · " + (m.ContextLength / 1000).ToString(CultureInfo.InvariantCulture) + "k context";
            var name = ModelNames.Display(m.Id, m.Name);
            var id = m.Id;
            var text = Kit.Column(1, Kit.Text(name, "strong"), Kit.Text((id != name ? id + " · " : "") + info, "muted", "small"));
            var box = new CheckBox { IsChecked = _chosen.Contains(id), Content = text };
            Kit.Named(box, "PickModel-" + id, name + ", " + info);
            box.IsCheckedChanged += (_, _) =>
            {
                if (box.IsChecked == true) _chosen.Add(id); else _chosen.Remove(id);
                Changed();
            };
            _picker.Children.Add(box);
        }
    }

    /// <summary>The models that will be added: the ticked ones and the one typed by hand.</summary>
    List<string> Chosen()
    {
        if (_editing is not null) return string.IsNullOrWhiteSpace(_model.Text) ? [] : [_model.Text.Trim()];
        var all = _chosen.ToList();
        var typed = _model.Text?.Trim();
        if (!string.IsNullOrEmpty(typed) && !all.Contains(typed, StringComparer.Ordinal)) all.Add(typed);
        return all;
    }

    void Changed()
    {
        _tested = false;
        UpdateButtons();
        UpdatePicked();
    }

    void UpdatePicked()
    {
        var chosen = Chosen();
        _picked.Text = chosen.Count == 0
            ? "Nothing chosen yet."
            : chosen.Count + (chosen.Count == 1 ? " model" : " models") + " will be " + (_editing is null ? "added" : "saved") + ": " + string.Join(", ", chosen.Take(4)) + (chosen.Count > 4 ? " and " + (chosen.Count - 4) + " more" : "") + ".";
    }

    void UpdateButtons()
    {
        var ready = Chosen().Count > 0 && (HasKey || _editing is not null);
        _test.IsEnabled = ready;
        _add.IsEnabled = ready && _tested;
    }

    SourceDraft Draft(string? model = null) =>
        new(SourceKinds.OpenRouter, model ?? Chosen().FirstOrDefault(), Endpoint, [Vocabulary.Generate], [], _label.Text?.Trim());

    async void Test()
    {
        _test.IsEnabled = false;
        _result.Text = "Testing…";
        try
        {
            // one short call, with the first model chosen: it shows that the key works
            var result = await W.Registry.TestDraftAsync(Draft(), visitorInitiated: true, apiKey: null).ConfigureAwait(true);
            _tested = result.Ok;
            _result.Text = result.Message;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _tested = false;
            _result.Text = "Test failed: " + (e.Message.Length > 160 ? e.Message[..157] + "…" : e.Message);
        }
        finally
        {
            _test.IsEnabled = true;
            UpdateButtons();
        }
    }

    void Save()
    {
        RegistryOperationResult result;
        if (_editing is { } editing)
        {
            var draft = Draft();
            result = W.Registry.UpdateSource(editing.Id, new SourceUpdate(draft.Kind, draft.Model, draft.Endpoint, draft.Skills, draft.Languages, draft.Label));
        }
        else
        {
            // every ticked model becomes a model of the visitor's, behind the one key
            var added = 0;
            var messages = new List<string>();
            result = RegistryOperationResult.Failure("Nothing was added.");
            foreach (var model in Chosen())
            {
                var one = W.Registry.AddSource(Draft(model));
                if (!one.Ok)
                {
                    messages.Add(model + ": " + one.Message);
                    continue;
                }

                added++;
                result = one;
            }

            if (added > 0 && result.Ok)
                result = RegistryOperationResult.Success((added == 1 ? "Added 1 model" : "Added " + added + " models") + (messages.Count > 0 ? "; " + messages.Count + " could not be added (" + messages[0] + ")" : "") + ".", result.Id);
            else if (messages.Count > 0) result = RegistryOperationResult.Failure(string.Join(" ", messages.Take(2)));
        }

        _result.Text = result.Message;
        if (result.Ok) Shell.Back();
        else UpdateButtons();
    }
}
