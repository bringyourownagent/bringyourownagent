using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Byoa.Wallet.Engine;
using Byoa.Wallet.Settings;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Desktop;

/// <summary>
/// "What it can do" for one model (PROPOSAL-v2 3.2): three ticks for what the visitor knows (it reads pictures, it hears audio, it draws pictures).
/// A tick is his word, and it beats the public list's; no tick says nothing, and then the
/// list speaks if it has the model and the property is not known if it does not. Under the ticks, in words, what the wallet knows now.
/// </summary>
sealed class CanDoEditor
{
    readonly CheckBox _reads = new() { Content = "Reads pictures" };
    readonly CheckBox _hears = new() { Content = "Hears audio" };
    readonly CheckBox _draws = new() { Content = "Draws pictures" };
    readonly TextBlock _known = Ui.Wrap("", "muted", "small");
    readonly Func<ModelFacts?> _facts;

    /// <param name="idSuffix">Ends the automation ids of the controls (the source's id, or "new").</param>
    /// <param name="facts">What the wallet knows of the model now (the list's word and his), for the line under the ticks; null while there is no model yet.</param>
    /// <param name="changed">Runs after every tick he makes.</param>
    public CanDoEditor(string idSuffix, IReadOnlyList<string>? inputs, IReadOnlyList<string>? outputs, Func<ModelFacts?> facts, Action changed)
    {
        _facts = facts;
        _reads.IsChecked = inputs?.Contains(Modalities.Image) == true;
        _hears.IsChecked = inputs?.Contains(Modalities.Audio) == true;
        _draws.IsChecked = outputs?.Contains(Modalities.Image) == true;
        AutomationProperties.SetAutomationId(_reads, "CanReads-" + idSuffix);
        AutomationProperties.SetAutomationId(_hears, "CanHears-" + idSuffix);
        AutomationProperties.SetAutomationId(_draws, "CanDraws-" + idSuffix);
        AutomationProperties.SetAutomationId(_known, "CanKnown-" + idSuffix);
        foreach (var box in new[] { _reads, _hears, _draws })
        {
            box.IsCheckedChanged += (_, _) =>
            {
                changed();
                Refresh();                      // the line under the ticks follows what was written
            };
        }

        var ticks = new WrapPanel();
        foreach (var box in new[] { _reads, _hears, _draws })
        {
            box.Margin = new Thickness(0, 0, 16, 2);
            ticks.Children.Add(box);
        }

        View = Ui.Column(4,
            Ui.Text("What it can do", "label"),
            Ui.Wrap("Tick what you know about this model. A tick is your word and beats the public list's; leave them all unticked to say nothing.", "muted", "small"),
            ticks, _known);
        Refresh();
    }

    public Control View { get; }

    /// <summary>What goes in, as he said it: null while he ticked nothing. Text is always among them once he has said anything.</summary>
    public List<string>? Inputs
    {
        get
        {
            if (_reads.IsChecked != true && _hears.IsChecked != true) return null;
            var words = new List<string> { Modalities.Text };
            if (_reads.IsChecked == true) words.Add(Modalities.Image);
            if (_hears.IsChecked == true) words.Add(Modalities.Audio);
            return words;
        }
    }

    /// <summary>What comes out, as he said it: null while he did not tick that it draws pictures.</summary>
    public List<string>? Outputs => _draws.IsChecked == true ? [Modalities.Text, Modalities.Image] : null;

    /// <summary>The line under the ticks says what the wallet knows.</summary>
    void Refresh()
    {
        var facts = _facts();
        _known.Text = facts is null ? "" : "The wallet knows: " + Describe(facts) + ".";
    }

    /// <summary>"reads text, pictures · makes text · context 131k · runs on an API key"; what it does not know it says.</summary>
    static string Describe(ModelFacts facts)
    {
        string Words(IReadOnlyList<string>? list) => list is null ? "not known" : string.Join(", ", list.Select(ModelFit.Plain));
        return "reads " + Words(facts.Input) + " · makes " + Words(facts.Output)
            + " · context " + (facts.ContextThousands is { } context ? context + "k" : "not known")
            + (facts.Runs is { } runs ? " · runs on " + ModelFit.Place(runs) : "");
    }

    /// <summary>Writes his word to a source's setting (empty lists are not written).</summary>
    public void WriteTo(SourceSetting setting)
    {
        setting.Inputs = Inputs;
        setting.Outputs = Outputs;
    }
}
