using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Byoa.Wallet.Engine;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Android;

/// <summary>
/// The consent, full screen, for a narrow portrait screen: the same questions and rules as the desktop wallet's consent window, in one column that
/// scrolls, with the check code and the two buttons fixed at the bottom where a thumb is. The site and how it came, its purpose in its own words, one
/// block for each skill it asks for (with its "Allow" box, what is already allowed, the models to tick), the limits it asks for, the budget, and
/// Decline or Allow. Back, the cross and leaving the page all answer Declined. The answer is built as the desktop builds it (<see cref="ConsentAnswer"/>).
/// </summary>
sealed partial class ConsentPage : UserControl, IBackHandler
{
    readonly Prompt<ConsentAnswer> _prompt;
    readonly ConsentModel _model;
    readonly AndroidServices _w;
    readonly Dictionary<string, ModelPick> _picks = new(StringComparer.Ordinal);
    readonly HashSet<string> _leftOff = [];
    readonly Button _allow;
    readonly TextBlock _allowWhy = Kit.Text("", "small", "bad");
    readonly Grid _frame = new();
    CheckBox? _siteLimits;
    CheckBox? _checkSame;
    Border? _checkBox;
    TextBlock? _budgetText;
    Control? _stepper;
    Action? _closeOverlay;
    decimal _budget;

    public ConsentPage(Prompt<ConsentAnswer> prompt, AndroidServices services)
    {
        _prompt = prompt;
        _model = (ConsentModel)prompt.Model;
        _w = services;
        _budget = _model.Budget;
        AutomationProperties.SetAutomationId(this, "ConsentPage");

        _allow = Kit.Button(_model.PausedNote is null ? "Allow" : "Resume", "allow", "ConsentAllow", () => Answer(true));
        var decline = Kit.Button("Decline", "deny", "ConsentDecline", () => Answer(false));
        _allow.HorizontalAlignment = HorizontalAlignment.Stretch;
        decline.HorizontalAlignment = HorizontalAlignment.Stretch;

        var main = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        var title = TitleBar();
        var scroll = new ScrollViewer
        {
            Content = Body(),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Top,
        };
        AutomationProperties.SetAutomationId(scroll, "ConsentScroll");
        var footer = Footer(decline);
        Grid.SetRow(title, 0);
        Grid.SetRow(scroll, 1);
        Grid.SetRow(footer, 2);
        main.Children.Add(title);
        main.Children.Add(scroll);
        main.Children.Add(footer);
        _frame.Children.Add(main);
        Content = _frame;
        UpdateAllow();
    }

    /// <summary>Back first closes what the page opened over itself ("Add models"); only then does it decline.</summary>
    public bool HandleBack()
    {
        if (_closeOverlay is not { } close) return false;
        close();
        return true;
    }

    void Answer(bool allow)
    {
        _prompt.Give(allow
            ? new ConsentAnswer(true, _budget, _model.AutoDecide, _leftOff.ToList(),
                _picks.Count == 0 ? null : _picks.ToDictionary(p => p.Key, p => (IReadOnlyList<string>)p.Value.Order.ToList(), StringComparer.Ordinal),
                _model.ShowModelNames, _siteLimits?.IsChecked != false, _checkSame?.IsChecked == true)
            : ConsentAnswer.Declined);
        // the first Allow is the first moment the app needs to show a notification (the serving one): ask for the permission now
        if (allow) AppState.Activity?.AskForNotificationsOnce();
    }

    Control TitleBar()
    {
        var close = Kit.Button("", "close", "ConsentClose", () => Answer(false));
        close.Content = Kit.Glyph(Kit.Cross, Kit.Brush("Ink2"), 18, 2.4);
        AutomationProperties.SetName(close, "Close and decline");
        return new Border
        {
            Padding = new Thickness(16, 0, 4, 0),
            BorderBrush = Kit.Brush("WinRule"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Background = Brushes.White,
            Child = Kit.Columns("*,Auto", Kit.Text("Agent wallet", "h2"), close),
        };
    }

    Control Body()
    {
        var body = new StackPanel { Spacing = 12, Margin = new Thickness(16, 14, 16, 18) };

        // The browser vouches for an extension origin. A direct link has no such proof until the site's welcome echoes the link, after the visitor allows it.
        var host = Kit.Text(Kit.HostOf(_model.Origin), "hostmono");
        host.TextWrapping = TextWrapping.NoWrap;
        host.TextTrimming = TextTrimming.CharacterEllipsis;
        AutomationProperties.SetAutomationId(host, "ConsentHost");
        // A link has no proof of where it came from until the site answers after Allow; the check code below is what the visitor can act on,
        // so no line here says "came by a link". Only a page that is not secure is told.
        Control? verified = _model.FromLink ? null
            : _model.Secure ? Kit.Row(4, Kit.InkCheck(15), Kit.Text("verified", "small", "strong"))
            : Kit.Text("local test page (http)", "muted", "small");
        var hostInfo = verified is null ? Kit.Column(2, host) : Kit.Column(2, host, verified);
        hostInfo.Margin = new Thickness(10, 0, 6, 0);
        var chipRow = Kit.Row(4);
        if (_model.PausedNote is not null) chipRow.Children.Add(Kit.Chip("paused", "outline"));
        var hostRow = Kit.Columns("Auto,*,Auto", Kit.Tile(Kit.Globe, 38), hostInfo, chipRow);
        AutomationProperties.SetName(hostRow, Kit.HostOf(_model.Origin));
        body.Children.Add(hostRow);
        if (_model.Handed) body.Children.Add(HandedNotice());
        if (_model.CheckCode is { } code) body.Children.Add(CheckBlock(code));

        // What the site says it is for, as it wrote it: its first two lines, the rest behind "More".
        body.Children.Add(Kit.Box("field", Expandable(_model.Summary, null, "ConsentPurpose")));

        if (_model.PausedNote is { } paused)
        {
            var note = Kit.Text("You allowed this site before. " + paused + ". Resume it?", "strong");
            AutomationProperties.SetAutomationId(note, "ConsentPaused");
            body.Children.Add(note);
        }

        // A site that asks for more gets the same page as the first time; what its grant covers says "already allowed".
        // What is not allowed yet comes first.
        var rows = _model.Rows.OrderBy(r => r.AlreadyAllowed ? 1 : 0).ToList();
        var asks = new WrapPanel { Orientation = Orientation.Horizontal };
        var asksLabel = Kit.Text("Asks for " + rows.Count.ToString(CultureInfo.InvariantCulture) + ":", "label");
        asksLabel.Margin = new Thickness(0, 0, 8, 4);
        AutomationProperties.SetAutomationId(asksLabel, "ConsentAsksFor");
        asks.Children.Add(asksLabel);
        foreach (var row in rows)
        {
            var chip = Kit.Chip(row.SkillName, KindClass(row.Skill) ?? "");
            chip.Margin = new Thickness(0, 0, 5, 4);
            asks.Children.Add(chip);
        }

        body.Children.Add(asks);
        for (var i = 0; i < rows.Count; i++) body.Children.Add(SkillRow(rows[i], i + 1));

        if (_model.Limits is { } ask) body.Children.Add(LimitsBlock(ask));
        body.Children.Add(BudgetLine());
        return body;
    }

    /// <summary>The colour class of a kind of skill, the same on its chip in "Asks for" and on its block: text bluish, pictures reddish, speech greenish.</summary>
    static string? KindClass(string skill) =>
        skill == Vocabulary.Generate ? "kind-text"
        : skill == Vocabulary.ImageGenerate ? "kind-picture"
        : skill.StartsWith("audio.", StringComparison.Ordinal) ? "kind-speech"
        : null;

    static T Kind<T>(T box, string skill) where T : StyledElement
    {
        if (KindClass(skill) is { } kind) box.Classes.Add(kind);
        return box;
    }

    // ------------------------------------------------------------------------------------------------
    // the check code (CONTRACT-DIRECT.md section 3a) and the buttons

    /// <summary>
    /// The page where the visitor pressed Connect shows the same four characters. When the site says it shows the code, Allow waits for his tick that
    /// they are the same; otherwise he is told that nothing can prove he started this. It is small: the code and the tick take two lines, the
    /// reason one short line, and "?" opens the long explanation.
    /// </summary>
    Control CheckBlock(string code)
    {
        var big = new TextBlock
        {
            Text = code, FontSize = 28, FontWeight = FontWeight.Bold, LetterSpacing = 6,
            FontFamily = (FontFamily)Avalonia.Application.Current!.FindResource("Mono")!, VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetAutomationId(big, "ConsentCheckCode");
        AutomationProperties.SetName(big, code);
        var head = Kit.Columns("*,Auto", Kit.Text("Check code", "strong"), big);
        var block = Kit.Column(4, head);
        if (_model.CheckRequired)
        {
            _checkSame = new CheckBox { Content = new TextBlock { Text = "The code is the same on the site's page", TextWrapping = TextWrapping.Wrap }, FontSize = 15 };
            Kit.Named(_checkSame, "ConsentCheckSame", "The code is the same on the site's page");
            _checkSame.IsCheckedChanged += (_, _) => UpdateAllow();
            // while the tick is not made it sits in a ring of amber, so that it is not missed
            _checkBox = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 0, 8, 0), BorderThickness = new Thickness(1.5), Child = _checkSame };
            block.Children.Add(_checkBox);
            var more = Kit.Text("The page where you pressed Connect shows the same code. If you did not just press Connect on this site yourself, Decline.", "muted", "small");
            more.IsVisible = false;
            AutomationProperties.SetAutomationId(more, "ConsentCheckMore");
            var why = Kit.Link("?", "ConsentCheckWhy", () => more.IsVisible = !more.IsVisible);
            AutomationProperties.SetName(why, "Why check the code?");
            block.Children.Add(Kit.Columns("*,Auto", Kit.Text("Decline if you did not just press Connect on this site.", "muted", "small"), why));
            block.Children.Add(more);
        }
        else
        {
            var warn = Kit.Text(_model.Handed ? HandedNoCheck : "This site gives no way to check that you started this connection. Allow only if you pressed Connect on its page a moment ago.", "strong", "small");
            AutomationProperties.SetAutomationId(warn, "ConsentNoCheck");
            block.Children.Add(warn);
        }

        return Kit.Box("field", block);
    }

    Control Footer(Button decline)
    {
        var buttons = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 10 };
        Grid.SetColumn(decline, 0);
        Grid.SetColumn(_allow, 1);
        buttons.Children.Add(decline);
        buttons.Children.Add(_allow);
        AutomationProperties.SetAutomationId(_allowWhy, "ConsentAllowWhy");
        // only the reason Allow is off and the two buttons stay in place; the whole page above them scrolls
        var content = Kit.Column(6);
        content.Children.Add(_allowWhy);
        content.Children.Add(buttons);
        return new Border
        {
            Background = Kit.Brush("WinBar"),
            BorderBrush = Kit.Brush("WinRule"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(14, 8, 14, 10),
            Child = content,
        };
    }

    // ------------------------------------------------------------------------------------------------
    // one block for each skill

    Control SkillRow(ConsentRow row, int number)
    {
        var panel = new StackPanel { Spacing = 6 };
        var title = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var numbered = NumberBadge(number);
        AutomationProperties.SetAutomationId(((Border)numbered).Child!, "ConsentNumber-" + row.Skill);
        numbered.Margin = new Thickness(0, 0, 7, 0);
        title.Children.Add(numbered);
        var skillName = Kit.Text(row.SkillName, "strong");
        skillName.Classes.Add("headtitle");
        skillName.Margin = new Thickness(0, 0, 7, 0);
        title.Children.Add(skillName);
        if (!row.Required) title.Children.Add(Kit.Chip("Optional"));
        if (row.AlreadyAllowed)
        {
            var allowed = Kit.Chip("already allowed", "allowed");
            allowed.Margin = new Thickness(6, 0, 0, 0);
            AutomationProperties.SetAutomationId(allowed.Child!, "ConsentAllowed-" + row.Skill);
            title.Children.Add(allowed);
        }

        // Every block has the same box at the right end of its header, "Allow", and any of them can be unticked: an optional block is then
        // left off; without a required one Allow at the bottom does not work, and says why.
        Control? allow = null;
        if (row.CanServe)
        {
            var include = new CheckBox { IsChecked = true, Content = "Allow", VerticalAlignment = VerticalAlignment.Center };
            include.Classes.Add("headallow");
            Kit.Named(include, "ConsentInclude-" + row.Skill, "Allow " + row.SkillName);
            include.IsCheckedChanged += (_, _) =>
            {
                if (include.IsChecked == true) _leftOff.Remove(row.Skill);
                else _leftOff.Add(row.Skill);
                panel.Opacity = include.IsChecked == true ? 1 : 0.6;
                UpdateAllow();
            };
            allow = include;
        }

        Control head = allow is null ? title : Kit.Columns("*,Auto", title, allow);
        head.VerticalAlignment = VerticalAlignment.Center;
        var header = Kind(new Border { Child = head }, row.Skill);
        header.Classes.Add("blockhead");
        if (row.Models is not null) return ModelRow(row, panel, header);

        if (!row.CanServe)
        {
            var explanation = row.Why.StartsWith("No ", StringComparison.Ordinal)
                ? row.Why
                : row.Required
                ? "None of your sources can do this, and the site needs it."
                : "None of your sources can do this. It is optional: it will be left off.";
            panel.Children.Add(Kit.Text(explanation, "muted"));
            panel.Children.Add(Kit.Text("Add a model in My models, then connect again.", "muted", "small"));
        }
        else
        {
            // Languages that go to the same source are one line; only when the sources differ is each line told its languages.
            var groups = RouteGroup.Of(row.Routes);
            foreach (var group in groups)
            {
                var route = group.Route;
                var name = Kit.Text(route.SourceName, "strong");
                Control mark = route.Fits ? Kit.InkCheck(18) : new Panel();
                mark.Margin = new Thickness(6, 0, 0, 0);
                mark.VerticalAlignment = VerticalAlignment.Top;
                var line = Kit.Columns("*,Auto", name, mark);
                // where it runs, its price and (when the sources differ) its languages go under the name, and the speed note under that
                var about = route.Where + " · " + (route.Paid ? "paid" : "free")
                    + (groups.Count > 1 && group.Languages.Count > 0 ? " · " + string.Join(", ", group.Languages.Select(Vocabulary.LanguageName)) : "");
                var lines = Kit.Column(2, line, Kit.Text(about, "muted", "small"));
                if (route.Detail is { Length: > 0 } detail) lines.Children.Add(Kit.Text(detail, "muted", "small"));
                panel.Children.Add(lines);
            }
        }

        // "why?" opens the needs as tags and the manager's reason
        var why = Kit.Link("why?", "ConsentWhy-" + row.Skill, () => { });
        why.HorizontalAlignment = HorizontalAlignment.Left;
        var details = new StackPanel { Spacing = 6, IsVisible = false };
        details.Children.Add(Kit.Rule(2, 2));
        var tags = new WrapPanel { Orientation = Orientation.Horizontal };
        tags.Children.Add(TagBox("skill", row.Skill));
        if (row.Languages.Count > 0) tags.Children.Add(TagBox("languages", string.Join(", ", row.Languages)));
        tags.Children.Add(TagBox("runs", string.Join(", ", row.Routes.Select(r => r.Where == "API key" ? "api-key" : "this-device").Distinct())));
        details.Children.Add(tags);
        details.Children.Add(Kit.Text(row.Why, "muted"));
        panel.Children.Add(why);
        panel.Children.Add(details);
        why.Click += (_, _) =>
        {
            details.IsVisible = !details.IsVisible;
            ((TextBlock)why.Content!).Text = details.IsVisible ? "hide" : "why?";
        };

        var box = Block(header, panel, row.Skill);
        if (!row.CanServe) box.BorderBrush = Kit.Brush("SignalDeep");
        return box;
    }

    /// <summary>A block like a small window: a header bar in the colour of its kind (number, title, "Allow"), and its body under it.</summary>
    static Border Block(Border header, StackPanel body, string skill)
    {
        body.Margin = new Thickness(12, 8, 12, 10);
        var block = Kind(new Border { Child = Kit.Column(0, header, body) }, skill);
        block.Classes.Add("block");
        return block;
    }

    /// <summary>The block's number in a white circle, at the start of its title.</summary>
    static Control NumberBadge(int number)
    {
        var text = new TextBlock { Text = number.ToString(CultureInfo.InvariantCulture), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var badge = new Border { Child = text, VerticalAlignment = VerticalAlignment.Center };
        badge.Classes.Add("number");
        return badge;
    }

    static Control TagBox(string key, string value)
    {
        var t = new TextBlock { TextWrapping = TextWrapping.Wrap };
        t.Inlines!.Add(new Avalonia.Controls.Documents.Run(key + " ") { FontFamily = (FontFamily)Avalonia.Application.Current!.FindResource("Mono")!, Foreground = Kit.Brush("Sign"), FontSize = 12.5 });
        t.Inlines.Add(new Avalonia.Controls.Documents.Run(value) { FontSize = 13, Foreground = Kit.Brush("Ink2") });
        var b = Kit.Box("field", t);
        b.Padding = new Thickness(8, 3);
        b.Margin = new Thickness(0, 0, 6, 6);
        return b;
    }

    // ------------------------------------------------------------------------------------------------
    // the budget, the limits, and what lets Allow work

    Control BudgetLine()
    {
        _budgetText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        UpdateBudget();
        var down = Kit.Button("", "deny", "ConsentBudgetDown", () => Step(-1));
        down.Content = Kit.Glyph("M6,12 L18,12", Kit.Brush("Ink"), 18);
        AutomationProperties.SetName(down, "Lower the budget");
        var up = Kit.Button("", "deny", "ConsentBudgetUp", () => Step(1));
        up.Content = Kit.Glyph("M6,12 L18,12 M12,6 L12,18", Kit.Brush("Ink"), 18);
        AutomationProperties.SetName(up, "Raise the budget");
        _stepper = Kit.Row(10, down, up);
        _stepper.IsVisible = false;
        var change = Kit.Link("change", "ConsentBudgetChange", () =>
        {
            if (_stepper is not null) _stepper.IsVisible = !_stepper.IsVisible;
        });
        return Kit.Column(2, Kit.Text("How much may this site spend?", "strong"), Kit.Columns("*,Auto", _budgetText, change), _stepper);
    }

    void Step(int direction)
    {
        _budget = direction > 0 ? Money.StepUp(_budget) : Money.StepDown(_budget);
        UpdateBudget();
    }

    void UpdateBudget()
    {
        if (_budgetText is null) return;
        _budgetText.Inlines!.Clear();
        if (_model.BudgetBefore is { } before && before != _budget) _budgetText.Inlines.Add(new Avalonia.Controls.Documents.Run("Budget " + Money.Show(before) + ", now "));
        else _budgetText.Inlines.Add(new Avalonia.Controls.Documents.Run("Up to "));
        _budgetText.Inlines.Add(new Avalonia.Controls.Documents.Run(Money.Show(_budget)) { FontWeight = FontWeight.Bold, FontSize = 18 });
        _budgetText.Inlines.Add(new Avalonia.Controls.Documents.Run(" per session"));
        AutomationProperties.SetName(_budgetText, "Up to " + Money.Show(_budget) + " per session");
        AutomationProperties.SetAutomationId(_budgetText, "ConsentBudget");
    }

    /// <summary>What the site asked for above the visitor's limits, with one tick to accept it.</summary>
    Control LimitsBlock(LimitsAsk ask)
    {
        var wanted = ask.Wanted;
        var current = ask.Current;
        var lines = new List<string>();
        if (wanted.InputChars is { } input) lines.Add("a text of up to " + Number(input) + " characters at once (yours: " + Number(current.InputChars) + ")");
        if (wanted.AnswerTokens is { } tokens) lines.Add("answers of up to " + Number(tokens) + " tokens (yours: " + Number(current.AnswerTokens) + ")");
        if (wanted.CallSeconds is { } seconds) lines.Add("waiting up to " + Number(seconds) + " seconds for an answer (yours: " + Number(current.CallSeconds) + ")");
        if (wanted.ParallelCalls is { } parallel) lines.Add(Number(parallel) + " questions at once (yours: " + Number(current.ParallelCalls) + ")");
        if (wanted.CallsPerMinute is { } perMinute) lines.Add(Number(perMinute) + " questions a minute (yours: " + Number(current.CallsPerMinute) + ")");
        _siteLimits = new CheckBox { IsChecked = true, Content = new TextBlock { Text = "Allow this for the site. You can change it in Limits.", TextWrapping = TextWrapping.Wrap }, FontSize = 14 };
        Kit.Named(_siteLimits, "ConsentSiteLimits", "Allow the limits the site asks for");
        var text = Kit.Text("The site asks for more room than your limits give it:\n• " + string.Join("\n• ", lines), "muted");
        AutomationProperties.SetAutomationId(text, "ConsentLimitsAsk");
        return Kit.Box("row", Kit.Column(4, text, _siteLimits));
    }

    static string Number(int value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>
    /// Allow is offered while every skill the site cannot work without can be given what it needs, the visitor has not unticked a required block, and
    /// (for a site that shows a check code) he has ticked that the two codes are the same. A button that does not work says why, in plain words above it:
    /// a phone has no tooltip to show on a disabled button.
    /// </summary>
    void UpdateAllow()
    {
        var plain = _model.Rows.Any(r => r.Required && r.Models is null && !r.CanServe);
        var models = _picks.Any(p => p.Value.Required && !_leftOff.Contains(p.Key) && !Satisfied(p.Value));
        // a required block the visitor unticked: the site cannot work without it, so Allow waits for the tick or Decline
        var unticked = _model.Rows.Where(r => r.Required && _leftOff.Contains(r.Skill)).Select(r => r.SkillName).ToList();
        var needsCode = _checkSame is { IsChecked: not true };
        _allow.IsEnabled = !plain && !models && unticked.Count == 0 && !needsCode;
        _allowWhy.Text =
            plain ? "None of your models can do what the site needs. Decline, add a model in My models, and connect again."
            : needsCode ? "Tick that the code is the same: Allow works after that."
            : unticked.Count > 0 ? "The site cannot work without " + string.Join(" and ", unticked) + ". Tick Allow on it to let the wallet serve this site, or Decline."
            : models ? "Tick the models the site needs. Allow works after that."
            : "";
        _allowWhy.IsVisible = _allowWhy.Text.Length > 0;
        if (_checkBox is not null)
        {
            // the ring of amber around the tick, until it is made
            _checkBox.BorderBrush = needsCode ? new SolidColorBrush(Color.FromRgb(255, 176, 48)) : Brushes.Transparent;
            _checkBox.Background = needsCode ? new SolidColorBrush(Color.FromArgb(40, 255, 176, 48)) : Brushes.Transparent;
        }
    }

    // ------------------------------------------------------------------------------------------------
    // what the consent says of a link that did not come through the browser on this phone (CONTRACT-PAIR.md section 1)

    /// <summary>For a site that shows no check code, when the link also came from outside the browser.</summary>
    const string HandedNoCheck = "This link came from outside the browser and the site gives no way to check it. Allow only if you started it yourself, just now.";

    Control HandedNotice()
    {
        var note = Kit.Text("This link did not come through the browser on this phone. It came from another device or was pasted by hand: check the code.", "strong", "small");
        AutomationProperties.SetAutomationId(note, "ConsentHanded");
        return Kit.Box("field", note);
    }

    /// <summary>
    /// A text in a field: its first two lines, and "More" under it when there is more to read. Pressed, the field opens to the whole text, up to
    /// seventeen lines tall (with a scroll when it is longer than that), and "Less" folds it back.
    /// </summary>
    /// <param name="shown">What shows while it is folded.</param>
    /// <param name="whole">What shows when it is open; null when it is the same text.</param>
    static Control Expandable(string shown, string? whole, string id)
    {
        const int closedLines = 2;
        const int openLines = 17;
        var text = Kit.Text(shown);
        text.TextTrimming = TextTrimming.CharacterEllipsis;
        text.MaxLines = closedLines;
        AutomationProperties.SetAutomationId(text, id);
        var scroll = new ScrollViewer { Content = text, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var more = Kit.Link("More", id + "More", () => { });
        more.HorizontalAlignment = HorizontalAlignment.Right;
        var longer = whole is not null && !string.Equals(whole, shown, StringComparison.Ordinal);
        more.IsVisible = longer;
        var open = false;
        more.Click += (_, _) =>
        {
            open = !open;
            if (open)
            {
                text.Text = whole ?? shown;
                text.MaxLines = 0;
                text.TextTrimming = TextTrimming.None;
                scroll.MaxHeight = Math.Round(text.FontSize * 1.45 * openLines);
                scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            }
            else
            {
                text.Text = shown;
                text.MaxLines = closedLines;
                text.TextTrimming = TextTrimming.CharacterEllipsis;
                scroll.MaxHeight = double.PositiveInfinity;
                scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
                scroll.Offset = default;
            }

            ((TextBlock)more.Content!).Text = open ? "Less" : "More";
        };
        // a text that is the same open and folded gets its button only when two lines are not enough at the width the screen has now
        text.SizeChanged += (_, _) =>
        {
            if (open || longer || text.Bounds.Width <= 0) return;
            var probe = new TextBlock { Text = shown, TextWrapping = TextWrapping.Wrap, FontWeight = text.FontWeight, FontSize = text.FontSize, FontFamily = text.FontFamily };
            probe.Measure(new Size(text.Bounds.Width, double.PositiveInfinity));
            var full = probe.DesiredSize.Height;
            probe.MaxLines = closedLines;
            probe.Measure(new Size(text.Bounds.Width, double.PositiveInfinity));
            more.IsVisible = full > probe.DesiredSize.Height + 1;
        };
        return Kit.Column(2, scroll, more);
    }
}
