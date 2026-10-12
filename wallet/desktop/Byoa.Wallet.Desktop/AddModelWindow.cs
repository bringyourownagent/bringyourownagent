using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
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
    ];
    bool _local = true;
    bool _tested;
    bool _scanning;
    int _scanNumber;

    public AddModelWindow(WalletServices services, string? sourceId = null)
    {
        _w = services;
        _editingId = sourceId;
        Title = sourceId is null ? "Add a model" : "Edit model source";
        Width = 690;
        Height = 650;
        MinWidth = 620;
        MinHeight = 520;
        Background = Brushes.White;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://Byoa.Wallet/Assets/app.ico")));

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
        Content = scroll;
        Opened += (_, _) =>
        {
            if (_local) DispatcherTimer.RunOnce(() => _ = ScanAsync(), TimeSpan.FromMilliseconds(100));
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
        _body.Children.Add(_suggestion);
        _body.Children.Add(Ui.Rule(4, 0));
        _body.Children.Add(Ui.Row(8, _test, _save));
        _body.Children.Add(_result);
        UpdateButtons();
    }

    void LocalStep()
    {
        _runtime.Items.Clear();
        foreach (var runtime in _runtimes)
        {
            var state = runtime.Running ? "running" : "not running";
            _runtime.Items.Add(new ComboBoxItem { Content = RuntimeName(runtime.Kind) + " · " + state, Tag = runtime });
        }
        if (_runtime.Items.Count > 0 && _runtime.SelectedIndex < 0) _runtime.SelectedIndex = 0;
        _body.Children.Add(Ui.Wrap("The wallet only uses a local server after it answers here. A stopped runtime is shown so you know what to start.", "muted", "small"));
        _body.Children.Add(Ui.Text("Runtime", "label"));
        _body.Children.Add(_runtime);
        _body.Children.Add(Ui.Text("Model", "label"));
        _body.Children.Add(_runtimeModel);
        if (_runtimes.Count > 8) _body.Children.Add(_search);
        var stopped = _runtimes.Where(r => !r.Running).ToList();
        if (_runtimes.Count == 0 && !_scanning) _body.Children.Add(Ui.Wrap("No runtimes answered. Start LM Studio or Ollama, then scan again.", "muted"));
        if (stopped.Count > 0) _body.Children.Add(Ui.Wrap("Not running — start it or download it. A stopped runtime cannot be tested.", "muted", "small"));
        _body.Children.Add(Ui.Button(_scanning ? "Scanning…" : "Scan again", "deny", "RuntimeScan", () => _ = ScanAsync(), "small"));
    }

    static string RuntimeName(string kind) => kind switch
    {
        SourceKinds.LmStudio => "LM Studio",
        SourceKinds.Ollama => "Ollama",
        _ => "OpenAI-compatible server",
    };

    void ApiStep()
    {
        _body.Children.Add(Ui.Wrap("The key is written only to the wallet's protected store when you add or save this source. It is never read back or sent to this agent.", "muted", "small"));
        _body.Children.Add(Ui.Row(9, Ui.Text("Provider", "label"), Ui.Text("OpenRouter", "strong")));
        _body.Children.Add(Ui.Row(9, Ui.Text("Model", "label"), _model));
        _body.Children.Add(Ui.Row(9, Ui.Text("API key", "label"), _key));
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
        Dispatcher.UIThread.Post(() =>
        {
            if (scanNumber != _scanNumber) return;
            _runtimes = runtimes.ToList();
            _scanning = false;
            Render();
        });
    }

    void PopulateModels()
    {
        var selected = (_runtime.SelectedItem as ComboBoxItem)?.Tag as RegistryRuntimeInfo;
        _runtimeModel.Items.Clear();
        if (selected is null) return;
        var query = _search.Text?.Trim() ?? "";
        foreach (var model in selected.Models.Where(m => query.Length == 0 || (m.Name ?? m.Id).Contains(query, StringComparison.OrdinalIgnoreCase)))
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
            if (_languages.Text?.Length == 0 && model.Id.Contains("qwen", StringComparison.OrdinalIgnoreCase)) _suggestion.Text = "Suggested because it is local and free. For Russian, a Qwen model is a good first try.";
        }
        UpdateButtons();
    }

    SourceDraft Draft() => new(
        _local ? GuessKind() : SourceKinds.OpenRouter,
        _model.Text?.Trim(),
        _local ? _endpoint.Text?.Trim() : "https://openrouter.ai/api/v1",
        [(_skill.SelectedItem as ComboBoxItem)?.Tag as string ?? Vocabulary.Generate],
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
        _result.Text = "Testing…";
        RegistryTestResult result;
        if (!_local)
        {
            result = new RegistryTestResult(true,
                "Provider details are ready. The wallet did not make a paid test call; a site will ask before paid use.");
        }
        else
        {
            result = _editingId is { } id ? await _w.Registry.TestSourceAsync(id) : await _w.Registry.TestDraftAsync(Draft());
        }
        _tested = result.Ok;
        _result.Text = result.Message;
        _test.IsEnabled = true;
        UpdateButtons();
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
