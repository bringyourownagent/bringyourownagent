namespace Byoa.Wallet.Settings;

/// <summary>
/// "Everywhere": the wallet's own use of the visitor's models, with no website in between. Dictation types what he says into the field that has the
/// focus; the check reads the text he selected, asks his model to do one action on it and shows the answer. Both are off until he turns them on: they
/// listen for global hotkeys and send keystrokes to other programs.
/// </summary>
public sealed class EverywhereSettings
{
    public const string DefaultDictationHotkey = "Ctrl+Alt+Space";
    public const string DefaultCheckHotkey = "Ctrl+Alt+K";

    /// <summary>Recording lasts while the hotkey is held; the text is typed when it is let go.</summary>
    public const string HoldToTalk = "hold";

    /// <summary>One press starts the recording, the next one stops it.</summary>
    public const string PressToggle = "toggle";

    /// <summary>The longest text the check takes, in characters; a longer selection is refused with a plain message.</summary>
    public const int MaxSelectionChars = 20_000;

    /// <summary>Dictation: the hotkey starts a recording and the recognised text is typed into the focused field. Off by default.</summary>
    public bool DictationOn { get; set; }

    /// <summary>The visitor read what dictation does and agreed (the page explains it the first time he turns it on).</summary>
    public bool DictationExplained { get; set; }

    public string DictationHotkey { get; set; } = DefaultDictationHotkey;

    /// <summary><see cref="HoldToTalk"/> or <see cref="PressToggle"/>.</summary>
    public string DictationMode { get; set; } = HoldToTalk;

    /// <summary>A fixed language (a primary tag such as "ru"), or null: the speech model finds the language itself.</summary>
    public string? DictationLanguage { get; set; }

    /// <summary>The check: the hotkey reads the selected text and asks the visitor's model to act on it. Off by default.</summary>
    public bool CheckOn { get; set; }

    public bool CheckExplained { get; set; }

    public string CheckHotkey { get; set; } = DefaultCheckHotkey;

    /// <summary>What the check can do with a selection, in the visitor's words. Never empty: see <see cref="EffectiveActions"/>.</summary>
    public List<CheckAction> Actions { get; set; } = DefaultActions();

    /// <summary>
    /// True: when no model on this computer answers, a paid source of his route may be used (the wallet asks before each paid call, as it does for
    /// a site). False, the default: Everywhere runs only on models on this computer and home servers, so no audio and no text leaves them.
    /// </summary>
    public bool AllowPaid { get; set; }

    /// <summary>The visitor answered "Always" to the question about a paid call: it is not asked again. Only counts while <see cref="AllowPaid"/> is on.</summary>
    public bool PaidWithoutAsking { get; set; }

    public static List<CheckAction> DefaultActions() =>
    [
        new CheckAction
        {
            Name = "Check facts and wording",
            Prompt = "Check the text for factual mistakes, unclear wording, spelling and grammar. Reply with the corrected text, then, after a blank line, " +
                     "a short list of what you changed and why. If the text has no mistakes, say so in one sentence.",
        },
        new CheckAction
        {
            Name = "Translate to English",
            Prompt = "Translate the text to English. Keep its meaning, tone and formatting. Reply with the translation only.",
        },
    ];

    /// <summary>The actions to offer: the visitor's own, or the wallet's two when he has none left (a blank one is skipped).</summary>
    public IReadOnlyList<CheckAction> EffectiveActions()
    {
        var own = Actions.Where(a => !string.IsNullOrWhiteSpace(a.Name) && !string.IsNullOrWhiteSpace(a.Prompt)).ToList();
        return own.Count > 0 ? own : DefaultActions();
    }
}

/// <summary>One thing the check can do to a selected text: a name for the button and the instruction given to the model.</summary>
public sealed class CheckAction
{
    public string Name { get; set; } = "";
    public string Prompt { get; set; } = "";
}
