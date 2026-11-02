using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Byoa.Wallet.Registry;
using Byoa.Wallet.Settings;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Android;

/// <summary>
/// "Add a model on a server": a model that an OpenAI-compatible server serves (a Llama app, LM Studio, Ollama or a home server), by the server's
/// address. As in the desktop wallet's Add a model, the address is held to the same rules (a complete http or https address, nothing in it that
/// could carry a password, nothing a network sends to itself), the wallet sends one tiny question first, and Add works only after that test has
/// passed with what is typed now. Given a model the visitor added before, the same page edits it (Save instead of Add).
/// </summary>
sealed class AddServerPage : Page
{
    readonly SourceSetting? _editing;
    readonly TextBox _endpoint = new() { Watermark = "http://192.168.1.20:1234/v1" };
    readonly TextBox _model = new() { Watermark = "model id, as the server lists it" };
    readonly TextBox _label = new() { Watermark = "optional friendly name" };
    readonly TextBlock _result = Kit.Text("", "small");
    readonly TextBlock _warning = Kit.Text("", "muted", "small");
    readonly Button _test;
    readonly Button _add;
    bool _tested;

    public AddServerPage(Shell shell, SourceSetting? editing = null) : base(shell)
    {
        _editing = editing;
        Kit.Named(_endpoint, "ServerAddress", "Address of the server");
        Kit.Named(_model, "ServerModel", "Model id");
        Kit.Named(_label, "ServerLabel", "Friendly name");
        AutomationProperties.SetAutomationId(_result, "ServerResult");
        _test = Kit.Button("Test", "deny", "SourceTest", Test);
        _add = Kit.Button(editing is null ? "Add" : "Save", "allow", "SourceSave", Save);
        _add.IsEnabled = false;
        _test.IsEnabled = false;
        if (editing is not null)
        {
            _endpoint.Text = editing.Endpoint;
            _model.Text = editing.Model;
            _label.Text = editing.Label;
        }

        void Changed()
        {
            _tested = false;
            Update();
        }

        _endpoint.TextChanged += (_, _) => Changed();
        _model.TextChanged += (_, _) => Changed();
        _label.TextChanged += (_, _) => Changed();
        Update();
    }

    public override string? Title => _editing is null ? "Add a model on a server" : "Edit a model";

    protected override Control Build()
    {
        var body = Kit.Column(12,
            Kit.Text("The wallet holds no model: name one that a server runs. It uses the server only to answer sites you allowed.", "muted"),
            Kit.Column(4, Kit.Text("Address", "label"), _endpoint, _warning),
            Kit.Column(4, Kit.Text("Model", "label"), _model),
            Kit.Column(4, Kit.Text("Name", "label"), _label),
            Kit.Row(10, _test, _add),
            _result);
        body.Margin = new Thickness(16, 12, 16, 24);
        return new ScrollViewer { Content = body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    }

    SourceDraft Draft() => new(_editing?.Kind ?? SourceKinds.OpenAiCompatible, _model.Text?.Trim(), _endpoint.Text?.Trim(), [Vocabulary.Generate], [], _label.Text?.Trim());

    void Update()
    {
        var filled = !string.IsNullOrWhiteSpace(_model.Text) && !string.IsNullOrWhiteSpace(_endpoint.Text);
        _test.IsEnabled = filled;
        _add.IsEnabled = filled && _tested;
        // the same address rules as the registry applies when it adds the source
        var check = string.IsNullOrWhiteSpace(_endpoint.Text) ? null : EndpointPolicy.Check(_endpoint.Text, agentSupplied: false);
        _warning.Text = check is null ? "" : !check.Allowed ? check.Message : check.IsLoopback ? "" : "This is another computer." + (check.Warning is { Length: > 0 } w ? " The connection is " + w + "." : "");
    }

    async void Test()
    {
        _test.IsEnabled = false;
        _result.Text = "Testing…";
        try
        {
            var result = await W.Registry.TestDraftAsync(Draft(), visitorInitiated: true).ConfigureAwait(true);
            _tested = result.Ok;
            _result.Text = result.Message;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _tested = false;
            _result.Text = "Test failed: " + e.GetType().Name;
        }
        finally
        {
            Update();
        }
    }

    void Save()
    {
        var draft = Draft();
        var result = _editing is { } editing
            ? W.Registry.UpdateSource(editing.Id, new SourceUpdate(draft.Kind, draft.Model, draft.Endpoint, draft.Skills, draft.Languages, draft.Label))
            : W.Registry.AddSource(draft);
        _result.Text = result.Message;
        if (!result.Ok) return;
        _endpoint.Text = "";
        _model.Text = "";
        _label.Text = "";
        Shell.Back();
    }
}
