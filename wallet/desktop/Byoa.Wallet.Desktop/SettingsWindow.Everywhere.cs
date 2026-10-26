using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Byoa.Wallet.Everywhere;
using Byoa.Wallet.Settings;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Desktop;

/// <summary>The Everywhere page: dictation into any text field, and the check of selected text. Both off until the visitor turns them on.</summary>
sealed partial class SettingsWindow
{
    /// <summary>A microphone, as a stroke drawing on the wallet's 24-unit canvas: the page's glyph in the menu and the indicator's.</summary>
    internal const string MicrophoneGlyph = "M12,3 A3,3 0 0 1 15,6 L15,12 A3,3 0 0 1 9,12 L9,6 A3,3 0 0 1 12,3 Z M6,11 A6,6 0 0 0 18,11 M12,17 L12,21";

    static readonly string[] DictationLanguages = ["en", "ru", "sk", "uk", "cs", "pl", "de", "fr", "es", "it", "pt", "nl", "hu", "be", "zh", "ja", "ko", "tr", "ar", "he"];

    // What the visitor is told the first time he turns a feature on. Two plain sentences each; the feature starts only after "Turn it on".
    const string DictationExplained =
        "While dictation is on, the wallet watches for its hotkey in every program, and only when you press it does it record from your microphone. " +
        "It sends the recording to your own speech model and types the words into the field you are in (by pasting them and then putting your clipboard back), and it keeps neither the sound nor the words.";

    const string CheckExplained =
        "While the check is on, the wallet watches for its hotkey in every program, and only when you press it does it copy the text you selected. " +
        "It asks your own text model to act on that text and shows the answer next to your cursor, and it changes your selection only if you press Replace selection. " +
        "If none of your models answers, it may ask a free demo service on the internet instead; such a service may keep the text, and you can switch that off below.";

    /// <summary>The feature whose first-time explanation is open now (not yet turned on).</summary>
    readonly HashSet<EverywhereFeature> _explaining = [];

    void EverywherePage(StackPanel page)
    {
        Head(page, "Everywhere",
            "Your own models, in every program on this computer and not only on sites. Dictation types what you say into the field you are in; the check reads the text you selected " +
            "and asks your model to do something with it. Both are off until you turn them on: they listen for a hotkey in every program and send keystrokes to the program you are in.");
        var settings = _w.Store.Snapshot().Everywhere;
        var status = _w.Everywhere.Status;

        page.Children.Add(Section("Dictation into any text field"));
        page.Children.Add(Ui.Box("card", DictationCard(settings, status.Dictation)));
        page.Children.Add(Section("Check the selected text"));
        page.Children.Add(Ui.Box("card", CheckCard(settings, status.Check)));
        page.Children.Add(Section("Models and money"));
        page.Children.Add(Ui.Box("card", EverywhereMoneyCard(settings)));
        page.Children.Add(Ui.Row(8,
            Ui.Button("Help: Everywhere", "deny", "EverywhereHelp", () => _w.Ui.OpenHelp("everywhere"), "small"),
            Ui.Button("Activity", "deny", "EverywhereActivity", () => Show("activity"), "small")));
    }

    // ------------------------------------------------------------------ dictation

    Control DictationCard(EverywhereSettings settings, FeatureStatus status)
    {
        var card = Ui.Column(10);
        card.Children.Add(FeatureHead("Dictation", "Press the hotkey, speak, and the words are typed into the field that has the focus: a browser, an editor, a chat.",
            settings.DictationOn, EverywhereFeature.Dictation, "EverywhereDictationSwitch",
            on => _w.Store.Update(s =>
            {
                s.Everywhere.DictationOn = on;
                if (on) s.Everywhere.DictationExplained = true;
            }), settings.DictationExplained));
        if (_explaining.Contains(EverywhereFeature.Dictation)) card.Children.Add(Explanation(EverywhereFeature.Dictation, DictationExplained, "EverywhereDictation"));
        if (!settings.DictationOn) return card;

        card.Children.Add(StatusLine(status, settings.DictationHotkey, "listening for it in every program", "EverywhereDictationStatus"));
        card.Children.Add(HotkeyRow("Hotkey", settings.DictationHotkey, EverywhereFeature.Dictation, "EverywhereHotkey",
            text => _w.Store.Update(s => s.Everywhere.DictationHotkey = text)));

        var mode = Ui.Column(4, Ui.Text("How it records", "strong"));
        foreach (var (value, name, id) in new[]
                 {
                     (EverywhereSettings.HoldToTalk, "Hold to talk: it records while you hold the hotkey and types when you let go", "EverywhereMode-hold"),
                     (EverywhereSettings.PressToggle, "Press to start, press to stop: one press starts the recording, the next one ends it", "EverywhereMode-toggle"),
                 })
        {
            var one = value;
            var radio = new RadioButton { Content = name, GroupName = "EverywhereMode", IsChecked = settings.DictationMode == value || (settings.DictationMode != EverywhereSettings.PressToggle && value == EverywhereSettings.HoldToTalk) };
            AutomationProperties.SetAutomationId(radio, id);
            radio.IsCheckedChanged += (_, _) =>
            {
                if (radio.IsChecked == true && _w.Store.Read(s => s.Everywhere.DictationMode) != one) _w.Store.Update(s => s.Everywhere.DictationMode = one);
            };
            mode.Children.Add(radio);
        }

        mode.Children.Add(Ui.Wrap("Esc drops a recording. One recording lasts at most 60 seconds.", "muted", "small"));
        card.Children.Add(mode);
        card.Children.Add(LanguageRow(settings.DictationLanguage));
        card.Children.Add(Ui.Wrap("The sound goes to your own speech model, by the speech route of My models and Skills & fallbacks. Nothing is recorded unless dictation is on and you press its hotkey; " +
                                  "the wallet keeps neither the sound nor the words.", "muted", "small"));
        return card;
    }

    Control LanguageRow(string? current)
    {
        var box = new ComboBox { MinWidth = 230 };
        AutomationProperties.SetAutomationId(box, "EverywhereLanguage");
        AutomationProperties.SetName(box, "Language of dictation");
        box.Items.Add(new ComboBoxItem { Content = "Automatic: the speech model finds the language", Tag = "" });
        foreach (var code in DictationLanguages) box.Items.Add(new ComboBoxItem { Content = Vocabulary.LanguageName(code), Tag = code });
        var chosen = Vocabulary.PrimaryLanguage(current) ?? "";
        if (chosen.Length > 0 && !DictationLanguages.Contains(chosen)) box.Items.Add(new ComboBoxItem { Content = Vocabulary.LanguageName(chosen), Tag = chosen });
        for (var i = 0; i < box.Items.Count; i++)
        {
            if (box.Items[i] is ComboBoxItem { Tag: string tag } && tag == chosen) box.SelectedIndex = i;
        }

        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
            var language = tag.Length == 0 ? null : tag;
            if (_w.Store.Read(s => s.Everywhere.DictationLanguage) != language) _w.Store.Update(s => s.Everywhere.DictationLanguage = language);
        };
        return Ui.Column(4, Ui.Text("Language", "strong"), box, Ui.Wrap("A fixed language helps a small model; automatic suits a model that tells languages apart.", "muted", "small"));
    }

    // ------------------------------------------------------------------ the check

    Control CheckCard(EverywhereSettings settings, FeatureStatus status)
    {
        var card = Ui.Column(10);
        card.Children.Add(FeatureHead("The check", "Select text anywhere and press the hotkey. Your own model does one action on it, and the answer shows next to your cursor.",
            settings.CheckOn, EverywhereFeature.Check, "EverywhereCheckSwitch",
            on => _w.Store.Update(s =>
            {
                s.Everywhere.CheckOn = on;
                if (on) s.Everywhere.CheckExplained = true;
            }), settings.CheckExplained));
        if (_explaining.Contains(EverywhereFeature.Check)) card.Children.Add(Explanation(EverywhereFeature.Check, CheckExplained, "EverywhereCheck"));
        if (!settings.CheckOn) return card;

        card.Children.Add(StatusLine(status, settings.CheckHotkey, "listening for it in every program", "EverywhereCheckStatus"));
        card.Children.Add(HotkeyRow("Hotkey", settings.CheckHotkey, EverywhereFeature.Check, "EverywhereCheckHotkey",
            text => _w.Store.Update(s => s.Everywhere.CheckHotkey = text)));

        card.Children.Add(Ui.Text("What it can do with the text", "strong"));
        card.Children.Add(Ui.Wrap("Each action is a name for a button and an instruction for your model. With one action it runs at once; with several, a window shows a button for each, " +
                                  "and a second press of the hotkey runs the first. The first text model of your route that answers does it, never one of your agents.", "muted", "small"));
        var actions = settings.Actions;
        for (var i = 0; i < actions.Count; i++) card.Children.Add(ActionEditor(i, actions[i], actions.Count));
        card.Children.Add(Ui.Row(8,
            Ui.Button("Add an action", "allow", "EverywhereActionAdd", () =>
            {
                _w.Store.Update(s => s.Everywhere.Actions.Add(new CheckAction { Name = "New action", Prompt = "Describe what the model should do with the text." }));
                Render();
            }, "small"),
            Ui.Button("Use the wallet's two actions", "deny", "EverywhereActionsReset", () =>
            {
                _w.Store.Update(s => s.Everywhere.Actions = EverywhereSettings.DefaultActions());
                Render();
            }, "small")));
        card.Children.Add(Ui.Wrap("The text goes only to your own model. It is never logged. A selection of up to " + N(EverywhereSettings.MaxSelectionChars) +
                                  " characters is taken. A terminal window is not read: Ctrl+C there would stop the program running in it.", "muted", "small"));
        return card;
    }

    Control ActionEditor(int index, CheckAction action, int count)
    {
        var name = new TextBox { Text = action.Name, MaxLength = 60, Watermark = "Name on the button" };
        AutomationProperties.SetAutomationId(name, "EverywhereAction-" + index + "-Name");
        AutomationProperties.SetName(name, "Name of action " + (index + 1));
        var prompt = new TextBox { Text = action.Prompt, AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MinHeight = 70, MaxLength = 4000, Watermark = "What the model should do with the text" };
        prompt.Classes.Add("small");
        AutomationProperties.SetAutomationId(prompt, "EverywhereAction-" + index + "-Prompt");
        AutomationProperties.SetName(prompt, "Instruction of action " + (index + 1));
        // Only a change the visitor makes counts: Avalonia raises TextChanged a moment after a box is made, with the text it was made with.
        name.TextChanged += (_, _) => SetAction(index, a => a.Name = name.Text ?? "", action.Name, name.Text);
        prompt.TextChanged += (_, _) => SetAction(index, a => a.Prompt = prompt.Text ?? "", action.Prompt, prompt.Text);
        var remove = Ui.Button("Remove", "deny", "EverywhereActionRemove-" + index, () =>
        {
            _w.Store.Update(s =>
            {
                if (index < s.Everywhere.Actions.Count) s.Everywhere.Actions.RemoveAt(index);
            });
            Render();
        }, "small");
        remove.IsEnabled = count > 1;                // the last one stays: a check with nothing to do is no check
        return Ui.Box("row", Ui.Column(6, Ui.Columns("*,Auto", name, remove), prompt));
    }

    void SetAction(int index, Action<CheckAction> change, string before, string? after)
    {
        if ((after ?? "") == before) return;
        _w.Store.Update(s =>
        {
            if (index < s.Everywhere.Actions.Count) change(s.Everywhere.Actions[index]);
        });
    }

    // ------------------------------------------------------------------ paid

    Control EverywhereMoneyCard(EverywhereSettings settings)
    {
        var card = Ui.Column(8);
        card.Children.Add(Ui.Wrap("Everywhere uses your own routes and your limits, like a site does. Its calls are in Activity under the name Everywhere and count in the wallet's limits for the day. " +
                                  "It tries the models on this computer or on a home server first. Sound never leaves them, and nothing goes to a paid service unless you allow it below.", "muted"));
        var free = new CheckBox
        {
            Content = "Let the check use a free demo model (LLM7, OVHcloud) when no model of mine answers. These public services may keep the text they are sent: it is not for private text",
            IsChecked = settings.AllowFreeCloud,
        };
        AutomationProperties.SetAutomationId(free, "EverywhereAllowFreeCloud");
        free.IsCheckedChanged += (_, _) =>
        {
            if (_w.Store.Read(s => s.Everywhere.AllowFreeCloud) != (free.IsChecked == true)) _w.Store.Update(s => s.Everywhere.AllowFreeCloud = free.IsChecked == true);
        };
        card.Children.Add(free);
        var allow = new CheckBox { Content = "Let Everywhere use a paid source when no model of mine answers (the wallet asks before each paid call)", IsChecked = settings.AllowPaid };
        AutomationProperties.SetAutomationId(allow, "EverywhereAllowPaid");
        allow.IsCheckedChanged += (_, _) =>
        {
            if (_w.Store.Read(s => s.Everywhere.AllowPaid) == (allow.IsChecked == true)) return;
            _w.Store.Update(s =>
            {
                s.Everywhere.AllowPaid = allow.IsChecked == true;
                if (!s.Everywhere.AllowPaid) s.Everywhere.PaidWithoutAsking = false;
            });
            Render();
        };
        card.Children.Add(allow);
        if (settings.AllowPaid)
        {
            var quiet = new CheckBox { Content = "Do not ask before each paid call (still within the limits of the day)", IsChecked = settings.PaidWithoutAsking };
            AutomationProperties.SetAutomationId(quiet, "EverywherePaidWithoutAsking");
            quiet.IsCheckedChanged += (_, _) =>
            {
                if (_w.Store.Read(s => s.Everywhere.PaidWithoutAsking) != (quiet.IsChecked == true)) _w.Store.Update(s => s.Everywhere.PaidWithoutAsking = quiet.IsChecked == true);
            };
            card.Children.Add(quiet);
        }

        return card;
    }

    // ------------------------------------------------------------------ the parts both features share

    /// <summary>
    /// The title row of a feature with its on/off switch. Turning a feature on for the first time does not turn it on: it opens the explanation, and the
    /// feature starts when the visitor says "Turn it on". Turning it off is at once.
    /// </summary>
    Control FeatureHead(string title, string text, bool on, EverywhereFeature feature, string id, Action<bool> set, bool explained)
    {
        var toggle = new ToggleSwitch { IsChecked = on, OnContent = "On", OffContent = "Off", VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAutomationId(toggle, id);
        AutomationProperties.SetName(toggle, title);
        toggle.IsCheckedChanged += (_, _) =>
        {
            var wanted = toggle.IsChecked == true;
            var now = _w.Store.Read(s => feature == EverywhereFeature.Dictation ? s.Everywhere.DictationOn : s.Everywhere.CheckOn);
            if (wanted == now) return;
            if (wanted && !explained)
            {
                _explaining.Add(feature);
                Render();                           // the switch goes back to off; the explanation opens
                return;
            }

            _explaining.Remove(feature);
            set(wanted);
            Render();
        };
        return Ui.Column(4, Ui.Columns("*,Auto", Ui.Text(title, "h2"), toggle), Ui.Wrap(text, "muted"));
    }

    Control Explanation(EverywhereFeature feature, string text, string idPrefix)
    {
        var on = Ui.Button("Turn it on", "allow", idPrefix + "Confirm", () =>
        {
            _explaining.Remove(feature);
            _w.Store.Update(s =>
            {
                if (feature == EverywhereFeature.Dictation)
                {
                    s.Everywhere.DictationExplained = true;
                    s.Everywhere.DictationOn = true;
                }
                else
                {
                    s.Everywhere.CheckExplained = true;
                    s.Everywhere.CheckOn = true;
                }
            });
            Render();
        }, "small");
        var cancel = Ui.Button("Not now", "deny", idPrefix + "Cancel", () =>
        {
            _explaining.Remove(feature);
            Render();
        }, "small");
        var body = Ui.Wrap(text, "wrap");
        AutomationProperties.SetAutomationId(body, idPrefix + "Explanation");
        return Ui.Box("row", Ui.Column(8, Ui.Text("Before you turn it on", "strong"), body, Ui.Row(8, on, cancel)));
    }

    /// <summary>What the service says about the feature: listening, or why it is not (a clash, a refused hotkey), in the service's own plain words.</summary>
    Control StatusLine(FeatureStatus status, string hotkey, string listening, string id)
    {
        var text = status.State switch
        {
            FeatureState.On => hotkey + ": " + listening + ".",
            FeatureState.Problem => status.Message ?? "The hotkey cannot be used.",
            _ => "Starting…",
        };
        var line = Ui.Wrap(text, status.State == FeatureState.Problem ? "strong" : "muted", "small");
        AutomationProperties.SetAutomationId(line, id);
        AutomationProperties.SetName(line, text);
        return line;
    }

    /// <summary>
    /// A hotkey the visitor can type ("Ctrl+Alt+J") or press ("Press the keys", then the combination). It is checked all the way before it is kept
    /// (understood, allowed, not the other feature's, not held by another program), and a refusal is said in plain words under the box.
    /// </summary>
    Control HotkeyRow(string label, string current, EverywhereFeature feature, string id, Action<string> save)
    {
        var box = new TextBox { Text = current, Width = 220, MinWidth = 220 };
        AutomationProperties.SetAutomationId(box, id);
        AutomationProperties.SetName(box, label + " of " + (feature == EverywhereFeature.Dictation ? "dictation" : "the check"));
        var message = Ui.Wrap("", "muted", "small");
        AutomationProperties.SetAutomationId(message, id + "Message");
        var capturing = false;
        var other = _w.Store.Read(s => feature == EverywhereFeature.Dictation ? s.Everywhere.CheckHotkey : s.Everywhere.DictationHotkey);

        void Commit(string? text)
        {
            var (hotkey, problem) = HotkeyRules.Validate(text, feature, other, _w.HotkeyProbe);
            if (hotkey is not { } accepted)
            {
                message.Text = problem;
                message.Classes.Remove("muted");
                message.Classes.Add("strong");
                return;
            }

            message.Classes.Remove("strong");
            message.Classes.Add("muted");
            message.Text = accepted + " is free; the wallet listens for it now.";
            box.Text = accepted.ToString();
            if (accepted.ToString() != current) save(accepted.ToString());
        }

        var capture = Ui.Button("Press the keys", "deny", id + "Capture", () =>
        {
            capturing = true;
            box.Text = "Press the new keys, Esc to cancel";
            message.Text = "";
            box.Focus();
        }, "small");
        box.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (!capturing) return;
            e.Handled = true;
            if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System) return;
            capturing = false;
            if (e.Key == Key.Escape)
            {
                box.Text = current;
                return;
            }

            var name = KeyName(e.Key);
            if (name is null)
            {
                box.Text = current;
                message.Text = "The wallet does not know that key. Use a letter, a digit, F1 to F24, Space, or a named key such as Home.";
                return;
            }

            var text = string.Join('+', new[]
            {
                e.KeyModifiers.HasFlag(KeyModifiers.Control) ? "Ctrl" : null, e.KeyModifiers.HasFlag(KeyModifiers.Alt) ? "Alt" : null,
                e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? "Shift" : null, e.KeyModifiers.HasFlag(KeyModifiers.Meta) ? "Win" : null, name,
            }.Where(p => p is not null));
            Commit(text);
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        box.LostFocus += (_, _) =>
        {
            if (capturing)
            {
                capturing = false;
                box.Text = current;
                return;
            }

            if ((box.Text ?? "").Trim() != current) Commit(box.Text);
        };
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || capturing) return;
            e.Handled = true;
            Commit(box.Text);
        };
        return Ui.Column(4, Ui.Text(label, "strong"), Ui.Row(8, box, capture), message);
    }

    /// <summary>The wallet's name for a key the visitor pressed, or null for a key it has no name for.</summary>
    internal static string? KeyName(Key key)
    {
        if (key is >= Key.A and <= Key.Z) return ((char)('A' + (key - Key.A))).ToString();
        if (key is >= Key.D0 and <= Key.D9) return ((char)('0' + (key - Key.D0))).ToString();
        if (key is >= Key.F1 and <= Key.F24) return "F" + (key - Key.F1 + 1);
        return key switch
        {
            Key.Space => "Space", Key.Enter => "Enter", Key.Tab => "Tab", Key.Back => "Backspace", Key.Insert => "Insert", Key.Delete => "Delete",
            Key.Home => "Home", Key.End => "End", Key.PageUp => "PageUp", Key.PageDown => "PageDown",
            Key.Up => "Up", Key.Down => "Down", Key.Left => "Left", Key.Right => "Right",
            Key.OemTilde => "Grave", Key.OemMinus => "Minus", Key.OemPlus => "Equals", Key.OemComma => "Comma", Key.OemPeriod => "Period",
            Key.OemQuestion => "Slash", Key.OemSemicolon => "Semicolon", Key.OemQuotes => "Quote", Key.OemOpenBrackets => "OpenBracket",
            Key.OemCloseBrackets => "CloseBracket", Key.OemPipe => "Backslash", Key.Pause => "Pause", Key.PrintScreen => "PrintScreen", Key.Scroll => "ScrollLock",
            _ => null,
        };
    }
}
