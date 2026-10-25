using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Byoa.Wallet.Settings;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Desktop;

/// <summary>
/// "Add an agent": the visitor's own AI agent (Claude Code, or a launcher built on it) as a source of answers for the sites he gives it to.
/// Only the visitor does this, here: the program he names is one the wallet will start, so no agent can add one through the MCP bridge.
/// </summary>
sealed class AddAgentWindow : Window
{
    readonly WalletServices _w;
    readonly TextBox _name = new() { Watermark = "A name you will know it by, for example My Claude", MinWidth = 380 };
    readonly TextBox _program = new() { Watermark = @"ccodeglm.cmd, claude, or C:\path\to\claude.exe", MinWidth = 300 };
    readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    readonly Button _test;
    readonly Button _save;
    bool _busy;

    public AddAgentWindow(WalletServices services)
    {
        _w = services;
        Title = "Add your agent";
        Width = 620;
        MinWidth = 560;
        Background = Brushes.White;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        CanResize = false;
        WindowLayout.Prepare(this, 360);

        AutomationProperties.SetAutomationId(_name, "AddAgentName");
        AutomationProperties.SetName(_name, "Name of the agent");
        AutomationProperties.SetAutomationId(_program, "AddAgentProgram");
        AutomationProperties.SetName(_program, "Program that runs the agent");
        AutomationProperties.SetAutomationId(_status, "AddAgentStatus");
        _status.Classes.Add("small");
        _name.TextChanged += (_, _) => Refresh();
        _program.TextChanged += (_, _) => Refresh();

        var browse = Ui.Button("Find…", "deny", "AddAgentBrowse", async () =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "The program that runs your agent",
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("Programs and launchers") { Patterns = ["*.exe", "*.cmd", "*.bat"] }],
            });
            if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) _program.Text = path;
        }, "small");
        _test = Ui.Button("Test it", "deny", "AddAgentTest", () => _ = TestAsync(), "small");
        _save = Ui.Button("Add the agent", "allow", "AddAgentSave", Save, "small");
        var cancel = Ui.Button("Cancel", "deny", "AddAgentCancel", Close, "small");

        var body = Ui.Column(12,
            Ui.Text("Add your agent", "h1"),
            Ui.Wrap("Your own AI agent can answer a site's questions and look things up on the web for them: the news of a country, say. " +
                    "A site gets it only if you give it that site, and only if the site says what it will ask. Claude Code and the launchers built on it work so far.", "muted"),
            Ui.Column(4, Ui.Text("Name", "label"), _name),
            Ui.Column(4, Ui.Text("Program that runs it", "label"), Ui.Row(8, _program, browse)),
            Ui.Box("field", Ui.Column(4,
                Ui.Text("What the wallet lets it do", "strong"),
                Ui.Wrap("One question at a time. It may search the web and read web pages, and nothing else: no files, no commands, none of your other tools, and not this wallet. " +
                        "It starts in an empty folder of the wallet's own, and the site's words are given to it as a question, never as an order.", "muted", "small"),
                Ui.Wrap("Every request goes through the guard first; what looks wrong waits for you. The agent uses your own subscription: the wallet pays nothing for it.", "muted", "small"))),
            _status);
        body.Margin = new Thickness(28, 24, 28, 16);
        var buttons = Ui.Row(8, _save, _test, cancel);
        buttons.Margin = new Thickness(28, 0, 28, 22);
        Content = Ui.Column(0, WindowLayout.Body(this, body, 90, 360), buttons);
        Refresh();
    }

    SourceSetting? Draft()
    {
        var program = _program.Text?.Trim() ?? "";
        if (program.Length == 0) return null;
        var name = SourceLabels.Clean(_name.Text) ?? AgentFlavors.Name(AgentFlavors.ClaudeCode);
        return new SourceSetting
        {
            Id = AgentPrograms.IdOf(name), Kind = SourceKinds.Agent, Model = AgentPrograms.Slug(name), Label = name, Skills = [Vocabulary.Generate],
            AgentCommand = program, AgentFlavor = AgentFlavors.ClaudeCode, Offer = false, EndpointConfirmed = true,
        };
    }

    void Refresh()
    {
        var ready = !_busy && Draft() is not null && !string.IsNullOrWhiteSpace(_name.Text);
        _save.IsEnabled = ready;
        _test.IsEnabled = !_busy && Draft() is not null;
    }

    void Say(string text, bool bad = false)
    {
        _status.Text = text;
        _status.Foreground = bad ? Ui.Brush("SignalDeep") : Ui.Brush("Ink2");
    }

    void Save()
    {
        var result = _w.Registry.AddAgentSource(_name.Text, AgentFlavors.ClaudeCode, _program.Text ?? "");
        if (!result.Ok)
        {
            Say(result.Message, bad: true);
            return;
        }

        _w.Ui.OpenSettings("myagents");
        Close();
    }

    /// <summary>One real question to the agent, as the wallet would put it: the answer comes back here, in words. It can take a minute.</summary>
    async Task TestAsync()
    {
        if (Draft() is not { } draft) return;
        _busy = true;
        Refresh();
        Say("Asking your agent a test question… this can take up to a minute.");
        try
        {
            ITextSource? source;
            try
            {
                source = _w.Sources.CreateTextSource(draft);
            }
            catch (ArgumentException e)
            {
                Say(e.Message, bad: true);
                return;
            }

            if (source is null)
            {
                Say("The wallet could not set this agent up.", bad: true);
                return;
            }

            var result = await _w.Engine.TestSourceAsync(source, Vocabulary.Generate, visitorInitiated: true);
            if (result?["ok"]?.GetValue<bool>() == true)
            {
                var text = result["output"]?["text"]?.GetValue<string>() ?? "";
                Say("Your agent answered: " + (text.Length > 160 ? text[..160] + "…" : text));
            }
            else
            {
                Say("Your agent did not answer: " + (result?["error"]?.GetValue<string>() ?? "no reason given") + ".", bad: true);
            }
        }
        finally
        {
            _busy = false;
            Refresh();
        }
    }
}
