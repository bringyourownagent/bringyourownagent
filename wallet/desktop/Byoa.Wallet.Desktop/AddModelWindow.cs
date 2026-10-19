using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Byoa.Wallet.Protocol;
using Byoa.Wallet.Providers;
using Byoa.Wallet.Registry;
using Byoa.Wallet.Settings;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Desktop;

/// <summary>
/// The short source wizard: a model that runs in a program on this PC, or models behind a key (OpenRouter with its hundreds of models, or a
/// company's own API). It keeps the visitor's key in the window until Registry writes it. One key serves every model behind its provider, and
/// several models can be added at once.
/// </summary>
sealed class AddModelWindow : Window
{
    const string OpenRouterId = SourceKinds.OpenRouter;

    readonly WalletServices _w;
    readonly string? _editingId;
    readonly StackPanel _body = new() { Spacing = 13 };
    readonly TextBlock _result = Ui.Wrap("", "muted");
    readonly TextBox _model = new() { Watermark = "model id", MinWidth = 300 };
    readonly TextBox _endpoint = new() { Watermark = "http://127.0.0.1:1234/v1", MinWidth = 300 };
    readonly TextBox _label = new() { Watermark = "optional friendly name", MinWidth = 300 };
    readonly TextBox _knownAs = new() { Watermark = "optional, for example google/gemma-3-1b-it", MinWidth = 300 };
    readonly TextBox _languages = new() { Watermark = "optional, for example ru, en", MinWidth = 300 };
    readonly TextBox _key = new() { PasswordChar = '•', Watermark = "paste the key here; it is never shown again", MinWidth = 360 };
    readonly ComboBox _skill = new() { MinWidth = 230 };
    readonly ComboBox _runtime = new() { MinWidth = 300 };
    readonly ComboBox _runtimeModel = new() { MinWidth = 300 };
    readonly ComboBox _provider = new() { MinWidth = 300 };
    readonly TextBox _search = new() { Watermark = "Search models", MinWidth = 300 };
    readonly TextBox _filter = new() { Watermark = "Search models", MinWidth = 300 };
    readonly StackPanel _picker = new() { Spacing = 5 };
    readonly TextBlock _picked = Ui.Wrap("", "muted", "small");
    readonly HashSet<string> _chosen = new(StringComparer.Ordinal);
    readonly Button _test;
    readonly Button _save;
    readonly TextBlock _suggestion = Ui.Wrap("", "muted", "small");
    List<RegistryRuntimeInfo> _runtimes =
    [
        new RegistryRuntimeInfo(SourceKinds.LmStudio, "http://127.0.0.1:1234/v1", false, []),
        new RegistryRuntimeInfo(SourceKinds.Ollama, "http://127.0.0.1:11434", false, []),
        new RegistryRuntimeInfo(SourceKinds.OpenAiCompatible, LocalRuntimes.LlamaEndpoint, false, []),
    ];
    List<string> _serviceModels = [];
    CatalogFilter _catalogFilter = CatalogFilter.All;
    bool _local = true;
    bool _tested;
    bool _scanning;
    bool _scanned;
    bool _fetching;
    bool _replaceKey;
    int _scanNumber;

    public AddModelWindow(WalletServices services, string? sourceId = null)
    {
        _w = services;
        _editingId = sourceId;
        _model.Classes.Add("mono");
        _endpoint.Classes.Add("mono");
        _knownAs.Classes.Add("mono");
        Title = sourceId is null ? "Add a model" : "Edit model source";
        Width = 690;
        Height = 700;
        MinWidth = 620;
        MinHeight = 520;
        Background = Brushes.White;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://Byoa.Wallet/Assets/app.ico")));
        MaxHeight = WindowLayout.MaxHeight(this, 520);

        foreach (var skill in new[] { Vocabulary.Transcribe, Vocabulary.Generate })
            _skill.Items.Add(new ComboBoxItem { Content = Vocabulary.SkillName(skill), Tag = skill });
        _skill.SelectedIndex = 1;
        _skill.SelectionChanged += (_, _) => { _tested = false; Render(); };
        _provider.Items.Add(new ComboBoxItem { Content = "OpenRouter · hundreds of models, one key", Tag = OpenRouterId });
        foreach (var service in ApiServices.All) _provider.Items.Add(new ComboBoxItem { Content = service.Name, Tag = service.Id });
        _provider.Items.Add(new ComboBoxItem { Content = "Another OpenAI-style service (its own address)", Tag = ApiServices.Custom });
        _provider.SelectedIndex = 0;
        _provider.SelectionChanged += (_, _) => { _tested = false; _chosen.Clear(); _serviceModels = []; _replaceKey = false; _key.Text = ""; Render(); };
        _runtime.SelectionChanged += (_, _) => SelectRuntime();
        _runtimeModel.SelectionChanged += (_, _) => SelectRuntimeModel();
        _search.TextChanged += (_, _) => PopulateModels();
        _filter.TextChanged += (_, _) => FillPicker();
        _model.TextChanged += (_, _) => { _tested = false; UpdateButtons(); };
        _key.TextChanged += (_, _) => { _tested = false; UpdateButtons(); };
        _endpoint.TextChanged += (_, _) => { _tested = false; UpdateButtons(); };
        _languages.TextChanged += (_, _) => { _tested = false; UpdateButtons(); };

        _test = Ui.Button("Test", "deny", "SourceTest", Test, "small");
        _save = Ui.Button(sourceId is null ? "Add" : "Save changes", "allow", "SourceSave", Save, "small");
        _save.IsEnabled = false;
        AutomationProperties.SetName(_test, "Test this model");
        AutomationProperties.SetName(_save, sourceId is null ? "Add this model" : "Save model changes");

        var scroll = new ScrollViewer
        {
            Content = _body,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Top,
        };
        scroll.SizeChanged += (_, _) =>
        {
            if (scroll.Bounds.Width > 0) _body.Width = Math.Max(0, scroll.Bounds.Width - scroll.Margin.Left - scroll.Margin.Right);
        };
        scroll.Margin = new Thickness(30, 25, 30, 25);
        var buttons = Ui.Row(8, _test, _save);
        buttons.Margin = new Thickness(30, 0, 30, 25);
        var layout = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        Grid.SetRow(scroll, 0);
        Grid.SetRow(buttons, 1);
        layout.Children.Add(scroll);
        layout.Children.Add(buttons);
        Content = layout;
        Opened += (_, _) =>
        {
            if (_local) DispatcherTimer.RunOnce(StartScan, TimeSpan.FromMilliseconds(100));
        };
        LoadExisting();
        Render();
    }

    string Provider => (_provider.SelectedItem as ComboBoxItem)?.Tag as string ?? OpenRouterId;

    bool IsOpenRouter => Provider == OpenRouterId;

    void LoadExisting()
    {
        if (_editingId is not { } id) return;
        var source = _w.Store.Snapshot().Sources.FirstOrDefault(s => s.Id == id);
        if (source is null) return;
        _local = source.Kind is not (SourceKinds.OpenRouter or ApiServices.Kind);
        if (!_local)
        {
            var wanted = source.Kind == SourceKinds.OpenRouter ? OpenRouterId : source.Service ?? ApiServices.Custom;
            for (var i = 0; i < _provider.Items.Count; i++)
            {
                if (_provider.Items[i] is ComboBoxItem { Tag: string tag } && tag == wanted) _provider.SelectedIndex = i;
            }
        }

        _model.Text = source.Model ?? "";
        _endpoint.Text = source.Endpoint ?? "";
        _label.Text = source.Label ?? "";
        _knownAs.Text = source.ModelId ?? "";
        _languages.Text = string.Join(", ", source.Languages);
        var skill = source.Skills.FirstOrDefault() ?? Vocabulary.Generate;
        _skill.SelectedIndex = skill == Vocabulary.Transcribe ? 0 : 1;
    }

    void Render()
    {
        _body.Children.Clear();
        _body.Children.Add(Ui.Column(3, Ui.Text(_editingId is null ? "Add a model" : "Edit model source", "h1"),
            Ui.Wrap("Choose where it is, select what it serves, test one tiny call, then save it.", "muted")));
        _body.Children.Add(Ui.Text("1  Where is it?", "h2"));
        var localButton = Ui.Button("On this PC", _local ? "allow" : "deny", "ModelLocal", () => { _local = true; _tested = false; Render(); }, "small");
        var keyButton = Ui.Button("Behind my API key", !_local ? "allow" : "deny", "ModelApiKey", () => { _local = false; _tested = false; Render(); }, "small");
        _body.Children.Add(Ui.Row(8, localButton, keyButton));
        if (_local) LocalStep(); else ApiStep();
        _body.Children.Add(Ui.Text("2  What should it do?", "h2"));
        _body.Children.Add(Ui.Text("Skill", "label"));
        if (!_local) _skill.SelectedIndex = 1;          // a model behind a key writes text; the paid speech model is the Scribe card in Sources
        _skill.IsEnabled = _local;
        _body.Children.Add(_skill);
        _body.Children.Add(Ui.Text("Languages", "label"));
        _body.Children.Add(_languages);
        UpdateSuggestion();
        _body.Children.Add(_suggestion);
        _body.Children.Add(Ui.Rule(4, 0));
        _body.Children.Add(_result);
        UpdateButtons();
    }

    void LocalStep()
    {
        _runtime.Items.Clear();
        foreach (var runtime in _runtimes)
        {
            var state = runtime.Running ? "running" : "not running";
            _runtime.Items.Add(new ComboBoxItem { Content = LocalRuntimes.Name(runtime.Kind, runtime.Endpoint) + " · " + state, Tag = runtime });
        }
        // Start on the first runtime that answered: with only Llama running, "LM Studio · not running" is no use.
        if (_runtime.Items.Count > 0 && _runtime.SelectedIndex < 0)
            _runtime.SelectedIndex = Math.Max(0, _runtimes.FindIndex(r => r.Running));
        if (_scanned && !_scanning && !_runtimes.Any(r => r.Running)) _body.Children.Add(NoRuntimeCard());
        _body.Children.Add(Ui.Wrap("The wallet holds no model: pick one that runs in a program on this PC. It only uses that program after it answers here. A stopped program is shown so you know what to start.", "muted", "small"));
        _body.Children.Add(Ui.Text("Runtime", "label"));
        _body.Children.Add(_runtime);
        _body.Children.Add(Ui.Text("Model", "label"));
        _body.Children.Add(_runtimeModel);
        _body.Children.Add(Ui.Text("Endpoint", "label"));
        _body.Children.Add(_endpoint);
        _body.Children.Add(Ui.Text("Label", "label"));
        _body.Children.Add(_label);
        _body.Children.Add(Ui.Text("Known as", "label"));
        _body.Children.Add(_knownAs);
        _body.Children.Add(Ui.Wrap("The model's usual name. A site that asks for a model by name (google/gemma-3-1b-it) finds it by this. Leave it empty and the wallet guesses from the program's id"
            + (string.IsNullOrWhiteSpace(_model.Text) ? "." : ": " + ModelNames.FromLocal(_model.Text) + "."), "muted", "small"));
        if (_runtimes.Count > 8) _body.Children.Add(_search);
        var stopped = _runtimes.Where(r => !r.Running).ToList();
        if (_runtimes.Count == 0 && !_scanning) _body.Children.Add(Ui.Wrap("No programs answered. Start Llama, LM Studio or Ollama, then scan again.", "muted"));
        if (stopped.Count > 0) _body.Children.Add(Ui.Wrap("Not running — start it or download it. A stopped runtime cannot be tested.", "muted", "small"));
        _body.Children.Add(Ui.Button(_scanning ? "Scanning…" : "Scan again", "deny", "RuntimeScan", StartScan, "small"));
    }

    /// <summary>Shown when no local model app answers: a visitor with none yet is pointed to Llama, free and open source.</summary>
    Control NoRuntimeCard() => Ui.Box("card", Ui.Column(7,
        Ui.Text("No local model app is running", "strong"),
        Ui.Wrap("Llama is free and open source, from the llama.cpp team and Hugging Face. It suggests a model that fits this PC " +
                "and downloads it. Install it, let it download a model, then press Scan again. " +
                "Already use LM Studio or Ollama? Start it and scan again.", "muted", "small"),
        Ui.Row(9, Ui.Button("Get Llama", "allow", "GetLlama", OpenLlamaPage, "small"), Ui.Text("llama.app", "muted", "small"))));

    /// <summary>One click by the visitor opens the fixed address in the browser; nothing is downloaded by the wallet.</summary>
    void OpenLlamaPage() => OpenPage(LocalRuntimes.LlamaPage, "Go to " + LocalRuntimes.LlamaPage + " yourself.");

    void OpenPage(string address, string fallback)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(address) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _result.Text = "Could not open the browser. " + fallback;
        }
    }

    // ------------------------------------------------------------------------------------------------
    // models behind a key

    string ProviderSecretName() => IsOpenRouter
        ? OpenRouterSource.SecretName
        : ApiServices.SecretName(Provider, _endpoint.Text);

    bool KeySaved
    {
        get
        {
            try { return _w.Secrets.Has(ProviderSecretName()); }
            catch (Exception e) when (e is not OutOfMemoryException) { return false; }
        }
    }

    /// <summary>The key to use now: the one typed in the window, else the one saved for this provider (read inside the wallet, never shown).</summary>
    string? ActiveKey()
    {
        if (!string.IsNullOrWhiteSpace(_key.Text)) return _key.Text.Trim();
        try { return _w.Secrets.Get(ProviderSecretName()); }
        catch (Exception e) when (e is not OutOfMemoryException) { return null; }
    }

    void ApiStep()
    {
        _body.Children.Add(Ui.Wrap("The key is written only to the wallet's protected store when you add these models. It is never read back or sent to an agent, and one key serves every model behind the same provider.", "muted", "small"));
        _body.Children.Add(Ui.Text("Provider", "label"));
        _body.Children.Add(_provider);
        var service = ApiServices.Find(Provider);
        if (service is not null)
            _body.Children.Add(Ui.Row(8, Ui.Text("Need a key?", "muted", "small"), Ui.Link(service.KeyPage.Replace("https://", ""), "ModelKeyPage", () => OpenPage(service.KeyPage, "Go to " + service.KeyPage + " yourself."))));
        if (Provider == ApiServices.Custom)
        {
            _body.Children.Add(Ui.Text("Address", "label"));
            _endpoint.Watermark = "https://api.example.org/v1";
            _body.Children.Add(_endpoint);
        }

        _body.Children.Add(Ui.Text("API key", "label"));
        if (KeySaved && !_replaceKey)
        {
            var saved = Ui.Row(10, Ui.Text("key saved ✓", "strong"), Ui.Link("use another key", "ModelKeyReplace", () => { _replaceKey = true; Render(); }));
            AutomationProperties.SetAutomationId(saved, "ModelKeySaved");
            _body.Children.Add(saved);
        }
        else
        {
            _body.Children.Add(_key);
        }

        _body.Children.Add(Ui.Text("Models", "label"));
        if (IsOpenRouter) CatalogPicker(); else ServicePicker();
        _body.Children.Add(Ui.Wrap("Or type a model id yourself:", "muted", "small"));
        _body.Children.Add(_model);
        _body.Children.Add(_picked);
        _body.Children.Add(Ui.Text("Label", "label"));
        _body.Children.Add(_label);
        _body.Children.Add(Ui.Wrap("A label names one model; with several ticked each keeps its own name.", "muted", "small"));
        UpdatePicked();
    }

    void CatalogPicker()
    {
        var catalog = _w.Engine.Catalog;
        if (!catalog.Loaded)
        {
            _body.Children.Add(Ui.Wrap("The list of models is not loaded yet (the wallet reads it from OpenRouter once a day). Type a model id below, or try again in a minute.", "muted", "small"));
            return;
        }

        var filters = Ui.Row(6,
            Ui.Button("All", _catalogFilter == CatalogFilter.All ? "allow" : "deny", "CatalogAll", () => { _catalogFilter = CatalogFilter.All; Render(); }, "small"),
            Ui.Button("Cheap", _catalogFilter == CatalogFilter.Cheap ? "allow" : "deny", "CatalogCheap", () => { _catalogFilter = CatalogFilter.Cheap; Render(); }, "small"),
            Ui.Button("Free", _catalogFilter == CatalogFilter.Free ? "allow" : "deny", "CatalogFree", () => { _catalogFilter = CatalogFilter.Free; Render(); }, "small"));
        _body.Children.Add(filters);
        _body.Children.Add(_filter);
        _body.Children.Add(_picker);
        FillPicker();
    }

    void ServicePicker()
    {
        var button = Ui.Button(_fetching ? "Loading…" : _serviceModels.Count > 0 ? "Reload the list" : "Show this provider's models", "deny", "ModelsFetch", FetchServiceModels, "small");
        button.IsEnabled = !_fetching && ActiveKey() is not null;
        _body.Children.Add(button);
        if (_serviceModels.Count > 0)
        {
            _body.Children.Add(_filter);
            _body.Children.Add(_picker);
            FillPicker();
        }
        else if (ActiveKey() is null)
        {
            _body.Children.Add(Ui.Wrap("Paste the key to see the models the provider offers.", "muted", "small"));
        }
    }

    async void FetchServiceModels()
    {
        var key = ActiveKey();
        if (key is null) return;
        var endpoint = ApiServices.Find(Provider)?.BaseUrl ?? _endpoint.Text?.Trim() ?? "";
        _fetching = true;
        _result.Text = "";
        Render();
        try
        {
            var ids = await ServiceModels.ListAsync(_w.Sources.TextHttp, endpoint, key, CancellationToken.None);
            _serviceModels = ids.ToList();
            _result.Text = ids.Count == 0 ? "The provider listed no models. Type a model id below." : "";
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _result.Text = "Could not read the provider's list: " + (e.Message.Length > 120 ? e.Message[..117] + "…" : e.Message) + ". You can still type a model id.";
        }
        finally
        {
            _fetching = false;
            Render();
        }
    }

    /// <summary>The ticks of the model list: the catalog (OpenRouter) or what the provider listed, narrowed by what was typed.</summary>
    void FillPicker()
    {
        _picker.Children.Clear();
        var query = _filter.Text?.Trim() ?? "";
        var rows = new List<(string Id, string Name, string Info)>();
        if (IsOpenRouter)
        {
            var have = _w.Store.Read(s => s.Sources.Select(x => x.Id).ToHashSet(StringComparer.Ordinal));
            foreach (var m in _w.Engine.Catalog.Search(query, _catalogFilter, 25))
            {
                var info = m.Free ? "free" : m.TypicalAnswerCost is { } cost ? "about " + Money.Show(Math.Max(0.000001m, cost)) + " an answer" : "price unknown";
                if (have.Contains(SourceKinds.OpenRouter + ":" + m.Id)) info += " · already added";
                rows.Add((m.Id, ModelNames.Display(m.Id, m.Name), info + (m.ContextLength > 0 ? " · " + (m.ContextLength / 1000).ToString(CultureInfo.InvariantCulture) + "k context" : "")));
            }
        }
        else
        {
            foreach (var id in _serviceModels.Where(id => query.Length == 0 || id.Contains(query, StringComparison.OrdinalIgnoreCase)).Take(40))
                rows.Add((id, id, ""));
        }

        if (rows.Count == 0) _picker.Children.Add(Ui.Wrap("No model matches. Try fewer words, or type a model id below.", "muted", "small"));
        foreach (var (id, name, info) in rows)
        {
            var box = new CheckBox { IsChecked = _chosen.Contains(id), MinWidth = 0, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Top };
            AutomationProperties.SetAutomationId(box, "PickModel-" + id);
            AutomationProperties.SetName(box, name + (info.Length > 0 ? ", " + info : ""));
            box.IsCheckedChanged += (_, _) =>
            {
                if (box.IsChecked == true) _chosen.Add(id); else _chosen.Remove(id);
                UpdatePicked();
                UpdateButtons();
            };
            var text = new StackPanel { Spacing = 1, Margin = new Thickness(8, 0, 0, 0) };
            var title = Ui.Wrap(name, "strong");
            text.Children.Add(title);
            if (id != name || info.Length > 0) text.Children.Add(Ui.Wrap((id != name ? id : "") + (id != name && info.Length > 0 ? " · " : "") + info, "muted", "small"));
            _picker.Children.Add(Ui.Columns("Auto,*", box, text));
        }
    }

    /// <summary>The models that will be added: the ticked ones and the one typed by hand.</summary>
    List<string> Chosen()
    {
        var all = _chosen.ToList();
        var typed = _model.Text?.Trim();
        if (!string.IsNullOrEmpty(typed) && !all.Contains(typed, StringComparer.Ordinal)) all.Add(typed);
        return all;
    }

    void UpdatePicked()
    {
        var chosen = Chosen();
        _picked.Text = chosen.Count == 0
            ? "Nothing chosen yet."
            : chosen.Count + (chosen.Count == 1 ? " model" : " models") + " will be added: " + string.Join(", ", chosen.Take(4)) + (chosen.Count > 4 ? " and " + (chosen.Count - 4) + " more" : "") + ".";
    }

    // ------------------------------------------------------------------------------------------------
    // the runtimes on this PC

    async Task ScanAsync()
    {
        if (_scanning) return;
        _scanning = true;
        Render();
        var scanNumber = ++_scanNumber;
        DispatcherTimer.RunOnce(() =>
        {
            if (!_scanning || scanNumber != _scanNumber) return;
            _runtimes = [];
            _scanning = false;
            _scanned = true;
            Render();
        }, TimeSpan.FromSeconds(5));
        IReadOnlyList<RegistryRuntimeInfo> runtimes;
        try
        {
            var scan = Task.Run(() => _w.Registry.ListRuntimesAsync());
            var winner = await Task.WhenAny(scan, Task.Delay(TimeSpan.FromSeconds(4)));
            runtimes = winner == scan ? await scan : [];
        }
        catch { runtimes = []; }
        DesktopUi.PostSafe(() =>
        {
            if (scanNumber != _scanNumber) return;
            _runtimes = runtimes.ToList();
            _scanning = false;
            _scanned = true;
            Render();
        });
    }

    void StartScan() => _ = ScanObservedAsync();

    async Task ScanObservedAsync()
    {
        try
        {
            await ScanAsync().ConfigureAwait(true);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            ErrorLog.Write(WalletPaths.Home, e);
            DesktopUi.PostSafe(() =>
            {
                _scanning = false;
                _result.Text = "The runtime scan failed. Try again.";
                Render();
            });
        }
    }

    void PopulateModels()
    {
        var selected = (_runtime.SelectedItem as ComboBoxItem)?.Tag as RegistryRuntimeInfo;
        _runtimeModel.Items.Clear();
        if (selected is null) return;
        var query = _search.Text?.Trim() ?? "";
        // For speech to text, the models that sound like speech models come first.
        var speech = SelectedSkill() == Vocabulary.Transcribe;
        foreach (var model in selected.Models.Where(m => query.Length == 0 || (m.Name ?? m.Id).Contains(query, StringComparison.OrdinalIgnoreCase))
                     .OrderByDescending(m => speech && LooksLikeSpeechModel(m.Id)))
            _runtimeModel.Items.Add(new ComboBoxItem { Content = model.Name is null ? model.Id : model.Name + "  ·  " + model.Id, Tag = model });
        if (_runtimeModel.Items.Count > 0) _runtimeModel.SelectedIndex = 0;
    }

    void SelectRuntime()
    {
        PopulateModels();
        SelectRuntimeModel();
    }

    void SelectRuntimeModel()
    {
        if (_runtime.SelectedItem is not ComboBoxItem { Tag: RegistryRuntimeInfo runtime } || !runtime.Running) return;
        _endpoint.Text = runtime.Kind == SourceKinds.Ollama ? runtime.Endpoint.TrimEnd('/') + "/v1" : runtime.Endpoint;
        if (_runtimeModel.SelectedItem is ComboBoxItem { Tag: RuntimeModel model })
        {
            _model.Text = model.Id;
        }

        UpdateSuggestion();
        UpdateButtons();
    }

    string SelectedSkill() => (_skill.SelectedItem as ComboBoxItem)?.Tag as string ?? Vocabulary.Generate;

    static bool LooksLikeSpeechModel(string id) =>
        new[] { "asr", "whisper", "speech", "voxtral", "parakeet", "canary", "audio" }
            .Any(word => id.Contains(word, StringComparison.OrdinalIgnoreCase));

    void UpdateSuggestion()
    {
        if (SelectedSkill() == Vocabulary.Transcribe)
        {
            _suggestion.Text = _local
                ? "Pick a speech model that the program has loaded, for example Qwen3-ASR in Llama. The test sends it a short sound. Leave Languages empty to take any language."
                : "A paid speech model needs no setup here: add your OpenRouter key on the Sources page, under Paid speech.";
        }
        else if (_languages.Text?.Length == 0 && _model.Text?.Contains("qwen", StringComparison.OrdinalIgnoreCase) == true)
        {
            _suggestion.Text = "Suggested because it is local and free. For Russian, a Qwen model is a good first try.";
        }
        else
        {
            _suggestion.Text = "";
        }
    }

    // ------------------------------------------------------------------------------------------------
    // test and save

    SourceDraft Draft(string? model = null)
    {
        model ??= _local ? _model.Text?.Trim() : Chosen().FirstOrDefault();
        if (_local)
            return new(GuessKind(), model, _endpoint.Text?.Trim(), [SelectedSkill()], ParseLanguages(), _label.Text?.Trim());
        if (IsOpenRouter)
            return new(SourceKinds.OpenRouter, model, "https://openrouter.ai/api/v1", [Vocabulary.Generate], ParseLanguages(), _label.Text?.Trim());
        var service = ApiServices.Find(Provider);
        return new(ApiServices.Kind, model, service?.BaseUrl ?? _endpoint.Text?.Trim(), [Vocabulary.Generate], ParseLanguages(), _label.Text?.Trim(), Provider);
    }

    string GuessKind()
    {
        var runtime = (_runtime.SelectedItem as ComboBoxItem)?.Tag as RegistryRuntimeInfo;
        return runtime?.Kind ?? SourceKinds.OpenAiCompatible;
    }

    IReadOnlyList<string> ParseLanguages() => (_languages.Text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(Vocabulary.PrimaryLanguage).Where(x => x is not null).Select(x => x!).Distinct(StringComparer.Ordinal).ToArray();

    async void Test()
    {
        _test.IsEnabled = false;
        _result.Text = SelectedSkill() == Vocabulary.Transcribe && _local
            ? "Testing… the first test can take a minute while the program loads the model."
            : "Testing…";
        try
        {
            RegistryTestResult result;
            // One short call, with the first model chosen: it shows that the key works. It costs a fraction of a cent behind a key.
            result = _editingId is { } id
                ? await _w.Registry.TestSourceAsync(id, visitorInitiated: true)
                : await _w.Registry.TestDraftAsync(Draft(), visitorInitiated: true,
                    apiKey: _local ? null : _key.Text);
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
        var added = new List<string>();
        if (_editingId is { } id)
        {
            var draft = Draft(_local ? _model.Text?.Trim() : Chosen().FirstOrDefault() ?? _model.Text?.Trim());
            result = _w.Registry.UpdateSource(id, new SourceUpdate(draft.Kind, draft.Model, draft.Endpoint, draft.Skills, draft.Languages, draft.Label));
            if (result.Ok) added.Add(result.Id ?? id);
        }
        else if (_local)
        {
            result = _w.Registry.AddSource(Draft());
            if (result.Ok) added.Add(result.Id!);
        }
        else
        {
            // Every ticked model becomes a model of the visitor's, behind the one key of its provider.
            var messages = new List<string>();
            var failed = 0;
            result = RegistryOperationResult.Failure("Nothing was added.");
            foreach (var model in Chosen())
            {
                var one = _w.Registry.AddSource(Draft(model));
                if (!one.Ok)
                {
                    failed++;
                    messages.Add(model + ": " + one.Message);
                    continue;
                }

                added.Add(one.Id!);
                result = one;
            }

            if (added.Count > 0 && !string.IsNullOrWhiteSpace(_key.Text))
            {
                var keyed = _w.Registry.SetApiKeyAsync(added[0], _key.Text!).GetAwaiter().GetResult();
                if (!keyed.Ok) result = keyed;
            }

            if (added.Count > 0 && result.Ok)
                result = RegistryOperationResult.Success((added.Count == 1 ? "Added 1 model" : "Added " + added.Count + " models") + (failed > 0 ? "; " + failed + " could not be added (" + messages[0] + ")" : "") + ".", added[0]);
            else if (messages.Count > 0) result = RegistryOperationResult.Failure(string.Join(" ", messages.Take(2)));
        }

        // The model's usual name is the visitor's own note; a speech model needs none.
        if (result.Ok && _local && added.Count == 1 && SelectedSkill() == Vocabulary.Generate)
        {
            var known = _knownAs.Text?.Trim();
            _w.Store.Update(s => s.Source(added[0]).ModelId = string.IsNullOrWhiteSpace(known) ? null : known);
        }

        _key.Text = "";
        _result.Text = result.Message;
        if (result.Ok) DispatcherTimer.RunOnce(Close, TimeSpan.FromMilliseconds(500));
        else UpdateButtons();
    }

    void UpdateButtons()
    {
        if (_local)
        {
            var ready = !string.IsNullOrWhiteSpace(_model.Text) && !string.IsNullOrWhiteSpace(_endpoint.Text) && _tested;
            _save.IsEnabled = ready;
            _test.IsEnabled = !string.IsNullOrWhiteSpace(_model.Text) && !string.IsNullOrWhiteSpace(_endpoint.Text);
            return;
        }

        var chosen = Chosen().Count > 0;
        var haveKey = !string.IsNullOrWhiteSpace(_key.Text) || KeySaved || _editingId is not null;
        var endpointOk = Provider != ApiServices.Custom || !string.IsNullOrWhiteSpace(_endpoint.Text);
        _save.IsEnabled = chosen && haveKey && endpointOk && _tested;
        _test.IsEnabled = chosen && haveKey && endpointOk;
        if (_picked is not null) UpdatePicked();
    }
}
