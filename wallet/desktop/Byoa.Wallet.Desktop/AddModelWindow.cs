using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Byoa.Wallet.Protocol;
using Byoa.Wallet.Registry;
using Byoa.Wallet.Settings;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Desktop;

/// <summary>The short two-step source wizard. It keeps the visitor's key in the window until Registry writes it.</summary>
sealed class AddModelWindow : Window
{
    readonly WalletServices _w;
    readonly string? _editingId;
    readonly StackPanel _body = new() { Spacing = 13 };
    readonly TextBlock _result = Ui.Wrap("", "muted");
    readonly TextBox _model = new() { Watermark = "model id", MinWidth = 300 };
    readonly TextBox _endpoint = new() { Watermark = "http://127.0.0.1:1234/v1", MinWidth = 300 };
    readonly TextBox _label = new() { Watermark = "optional friendly name", MinWidth = 300 };
    readonly TextBox _languages = new() { Watermark = "optional, for example ru, en", MinWidth = 300 };
    readonly TextBox _key = new() { PasswordChar = '•', Watermark = "paste the key here; it is never shown again", MinWidth = 360 };
    readonly ComboBox _skill = new() { MinWidth = 230 };
    readonly ComboBox _runtime = new() { MinWidth = 300 };
    readonly ComboBox _runtimeModel = new() { MinWidth = 300 };
    readonly TextBox _search = new() { Watermark = "Search models", MinWidth = 300 };
    readonly Button _test;
    readonly Button _save;
    readonly TextBlock _suggestion = Ui.Wrap("", "muted", "small");
    List<RegistryRuntimeInfo> _runtimes =
    [
        new RegistryRuntimeInfo(SourceKinds.LmStudio, "http://127.0.0.1:1234/v1", false, []),
        new RegistryRuntimeInfo(SourceKinds.Ollama, "http://127.0.0.1:11434", false, []),
        new RegistryRuntimeInfo(SourceKinds.OpenAiCompatible, LocalRuntimes.LlamaEndpoint, false, []),
    ];
    bool _local = true;
    bool _tested;
    bool _scanning;
    bool _scanned;
    int _scanNumber;

    public AddModelWindow(WalletServices services, string? sourceId = null)
    {
        _w = services;
        _editingId = sourceId;
        _model.Classes.Add("mono");
        _endpoint.Classes.Add("mono");
        Title = sourceId is null ? "Add a model" : "Edit model source";
        Width = 690;
        Height = 650;
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
        _runtime.SelectionChanged += (_, _) => SelectRuntime();
        _runtimeModel.SelectionChanged += (_, _) => SelectRuntimeModel();
        _search.TextChanged += (_, _) => PopulateModels();
        _model.TextChanged += (_, _) => { _tested = false; UpdateButtons(); };
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

    void LoadExisting()
    {
        if (_editingId is not { } id) return;
        var source = _w.Store.Snapshot().Sources.FirstOrDefault(s => s.Id == id);
        if (source is null) return;
        _local = !string.Equals(source.Kind, SourceKinds.OpenRouter, StringComparison.Ordinal);
        _model.Text = source.Model ?? "";
        _endpoint.Text = source.Endpoint ?? "";
        _label.Text = source.Label ?? "";
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
    void OpenLlamaPage()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(LocalRuntimes.LlamaPage) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _result.Text = "Could not open the browser. Go to " + LocalRuntimes.LlamaPage + " yourself.";
        }
    }

    void ApiStep()
    {
        _body.Children.Add(Ui.Wrap("The key is written only to the wallet's protected store when you add or save this source. It is never read back or sent to this agent.", "muted", "small"));
        _body.Children.Add(Ui.Row(9, Ui.Text("Provider", "label"), Ui.Text("OpenRouter", "strong")));
        _body.Children.Add(Ui.Row(9, Ui.Text("Model", "label"), _model));
        _body.Children.Add(Ui.Row(9, Ui.Text("API key", "label"), _key));
        _body.Children.Add(Ui.Row(9, Ui.Text("Label", "label"), _label));
        _body.Children.Add(Ui.Wrap("key saved ✓ appears after Save; the field is cleared.", "muted", "small"));
    }

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

    SourceDraft Draft() => new(
        _local ? GuessKind() : SourceKinds.OpenRouter,
        _model.Text?.Trim(),
        _local ? _endpoint.Text?.Trim() : "https://openrouter.ai/api/v1",
        [SelectedSkill()],
        ParseLanguages(),
        _label.Text?.Trim());

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
        var draft = Draft();
        RegistryOperationResult result;
        if (_editingId is { } id)
        {
            result = _w.Registry.UpdateSource(id, new SourceUpdate(draft.Kind, draft.Model, draft.Endpoint, draft.Skills, draft.Languages, draft.Label));
        }
        else
        {
            result = _w.Registry.AddSource(draft);
            if (result.Ok && !_local && !string.IsNullOrWhiteSpace(_key.Text))
                result = _w.Registry.SetApiKeyAsync(result.Id!, _key.Text!).GetAwaiter().GetResult();
        }
        _key.Text = "";
        _result.Text = result.Message;
        if (result.Ok) DispatcherTimer.RunOnce(Close, TimeSpan.FromMilliseconds(500));
        else UpdateButtons();
    }

    void UpdateButtons()
    {
        var ready = !string.IsNullOrWhiteSpace(_model.Text) && (_local ? !string.IsNullOrWhiteSpace(_endpoint.Text) : true) && _tested;
        _save.IsEnabled = ready;
        _test.IsEnabled = !string.IsNullOrWhiteSpace(_model.Text) && (_local ? !string.IsNullOrWhiteSpace(_endpoint.Text) : !string.IsNullOrWhiteSpace(_key.Text) || _editingId is not null);
    }
}
