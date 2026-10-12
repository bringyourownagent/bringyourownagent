using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Byoa.Wallet.Engine;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Desktop;

/// <summary>
/// The short consent window: the site, its purpose in its own words, one row per skill with the source the
/// manager suggests (and why), one limit, "decide on my own next time", Decline or Allow, and a link to the
/// detailed settings. When a site asks for more than it has, only the difference is shown.
/// It takes the focus and works from the keyboard; Esc declines.
/// </summary>
sealed class ConsentWindow : CornerWindow
{
    readonly ConsentModel _model;
    readonly Action<string?> _openSettings;
    readonly TaskCompletionSource<ConsentAnswer> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly HashSet<string> _leftOff = [];
    readonly CheckBox _auto;
    readonly Button _allow;
    decimal _budget;
    CancellationTokenRegistration _timeout;
    bool _closed;
    TextBlock? _budgetText;
    Control? _stepper;

    public ConsentWindow(ConsentModel model, Action<string?> openSettings) : base(400)
    {
        _model = model;
        _openSettings = openSettings;
        _budget = model.Budget;
        Title = "Agent wallet consent";
        ShowActivated = true;

        _auto = new CheckBox
        {
            Content = Ui.Wrap("Next time, decide on my own within these limits"),
            IsChecked = model.AutoDecide,
            FontSize = 14,
        };
        AutomationProperties.SetAutomationId(_auto, "ConsentAuto");
        AutomationProperties.SetName(_auto, "Next time, decide on my own within these limits");

        _allow = Ui.Button(model.PausedNote is null ? "Allow" : "Resume", "allow", "ConsentAllow", () => Answer(true));
        _allow.IsEnabled = !model.MissingRequired;
        var decline = Ui.Button("Decline", "deny", "ConsentDecline", () => Answer(false));

        var card = new Border { Margin = new Thickness(ShadowMargin), ClipToBounds = true };
        card.Classes.Add("popup");
        var layout = new DockPanel();
        var titleBar = TitleBar();
        DockPanel.SetDock(titleBar, Dock.Top);
        layout.Children.Add(titleBar);
        var footer = Footer(decline);
        DockPanel.SetDock(footer, Dock.Bottom);
        layout.Children.Add(footer);
        var scroll = new ScrollViewer
        {
            Content = Body(),
            MaxHeight = Math.Max(240, WorkingHeight - 170),
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Top,
        };
        layout.Children.Add(scroll);
        card.Child = layout;
        Content = card;

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Answer(false);
            }
        };
        Opened += (_, _) =>
        {
            Activate();
            (_allow.IsEnabled ? _allow : decline).Focus(NavigationMethod.Tab);
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _timeout.Dispose();
            _answer.TrySetResult(ConsentAnswer.Declined);
        };
    }

    public Task<ConsentAnswer> Result => _answer.Task;

    public void CloseOn(CancellationToken ct) => _timeout = ct.Register(() => Dispatcher.UIThread.Post(() =>
    {
        _answer.TrySetResult(ConsentAnswer.Declined);
        if (!_closed) Close();
    }));

    void Answer(bool allow)
    {
        _answer.TrySetResult(allow
            ? new ConsentAnswer(true, _budget, _auto.IsChecked == true, _leftOff.ToList())
            : ConsentAnswer.Declined);
        if (!_closed) Close();
    }

    Control TitleBar()
    {
        var icon = new Image { Source = new Avalonia.Media.Imaging.Bitmap(Avalonia.Platform.AssetLoader.Open(new Uri("avares://Byoa.Wallet/Assets/icon-192.png"))), Width = 18, Height = 18 };
        var close = new Button { Content = Ui.Glyph(Ui.Cross, Ui.Brush("Ink2"), 12, 2.4) };
        close.Classes.Add("close");
        AutomationProperties.SetAutomationId(close, "ConsentClose");
        AutomationProperties.SetName(close, "Close and decline");
        close.Click += (_, _) => Answer(false);
        close.IsTabStop = false;
        var bar = new Border
        {
            Padding = new Thickness(14, 8, 8, 8),
            BorderBrush = Ui.Brush("WinRule"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Background = Brushes.White,
            Child = Ui.Columns("Auto,*,Auto", icon, Ui.Text("Agent wallet", "label"), close),
        };
        icon.Margin = new Thickness(0, 0, 8, 0);
        bar.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(bar).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
        };
        return bar;
    }

    Control Body()
    {
        var body = new StackPanel { Spacing = 10, Margin = new Thickness(16, 14, 16, 14), Width = 368 };

        // The browser vouches for an extension origin. A direct link has no such proof until the visitor allows it.
        var host = Ui.Text(_model.Host, "host");
        var linkProof = Ui.Wrap("came by a link:\nconfirmed after you allow", "muted", "small");
        linkProof.Width = 220;
        linkProof.TextTrimming = TextTrimming.CharacterEllipsis;
        var verified = _model.FromLink
            ? Ui.Row(4, linkProof)
            : _model.Secure
            ? Ui.Row(4, Ui.InkCheck(14), Ui.Text("verified", "small", "strong"))
            : Ui.Row(4, Ui.Text("local test page (http)", "muted", "small"));
        if (_model.Secure && !_model.FromLink) ((TextBlock)verified.Children[1]).Foreground = Ui.Brush("Sign");
        var hostInfo = Ui.Column(3, host, verified);
        hostInfo.Width = 290;
        var hostRow = Ui.Row(9, Ui.Tile(Ui.Globe, 28), hostInfo);
        if (_model.IsDifference) hostRow.Children.Add(Ui.Chip("updated", "outline"));
        if (_model.PausedNote is not null) hostRow.Children.Add(Ui.Chip("paused", "outline"));
        AutomationProperties.SetName(hostRow, _model.Host);
        body.Children.Add(hostRow);

        // The declared purpose, quoted as the site's own words.
        var purpose = Ui.Runs(("“" + _model.Summary + "”", false));
        purpose.Width = 300;
        purpose.TextTrimming = TextTrimming.CharacterEllipsis;
        purpose.MaxLines = 2;
        purpose.Inlines!.Clear();
        purpose.Text = "\"" + _model.Summary + "\"  from the site";
        purpose.FontWeight = FontWeight.Medium;
        body.Children.Add(Ui.Box("field", purpose));

        if (_model.PausedNote is { } paused)
        {
            var note = Ui.Wrap("You allowed this site before. " + paused + ". Resume it?", "strong");
            AutomationProperties.SetAutomationId(note, "ConsentPaused");
            body.Children.Add(note);
        }

        if (_model.IsDifference)
        {
            body.Children.Add(Ui.Wrap("The site was updated and asks for more than you allowed. Only the difference is shown.", "muted"));
        }

        body.Children.Add(Ui.Text(_model.IsDifference ? "Only what is new" : "Asks for", "label"));
        foreach (var row in _model.Rows) body.Children.Add(SkillRow(row));
        if (_model.IsDifference && _model.Rows.Count == 0)
        {
            body.Children.Add(Ui.Wrap("No new skill. The site now expects to spend more per session:", "muted"));
        }

        body.Children.Add(BudgetLine());
        body.Children.Add(_auto);
        return body;
    }

    Control SkillRow(ConsentRow row)
    {
        var panel = new StackPanel { Spacing = 4 };
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        if (!row.Required && row.CanServe)
        {
            var include = new CheckBox { IsChecked = true, MinWidth = 0, Padding = new Thickness(0) };
            AutomationProperties.SetName(include, "Include " + row.SkillName);
            include.IsCheckedChanged += (_, _) =>
            {
                if (include.IsChecked == true) _leftOff.Remove(row.Skill);
                else _leftOff.Add(row.Skill);
                panel.Opacity = include.IsChecked == true ? 1 : 0.6;
            };
            title.Children.Add(include);
        }

        if (row.IsNew) title.Children.Add(Ui.Text("New:", "strong"));
        title.Children.Add(Ui.Text(row.SkillName, "strong"));
        if (!row.Required) title.Children.Add(Ui.Chip("Optional"));

        var why = Ui.Link("why?", "ConsentWhy-" + row.Skill, () => { });
        why.HorizontalAlignment = HorizontalAlignment.Right;
        panel.Children.Add(Ui.Columns("*,Auto", title, why));

        if (!row.CanServe)
        {
            var explanation = row.Why.StartsWith("No ", StringComparison.Ordinal)
                ? row.Why
                : row.Required
                ? "None of your sources can do this, and the site needs it."
                : "None of your sources can do this. It is optional: it will be left off.";
            panel.Children.Add(Ui.Wrap(explanation, "muted"));
            panel.Children.Add(Ui.Link("Add a model or a key…", "ConsentFix", () => _openSettings("add-model")));
        }
        else
        {
            var multi = row.Routes.Count > 1;
            foreach (var route in row.Routes)
            {
                var line = Ui.Row(6,
                    Ui.Glyph(Ui.Arrow, Ui.Brush("Muted"), 14, 2),
                    Ui.Glyph(route.Where == "API key" ? Ui.Key : Ui.Screen, Ui.Brush("Ink2"), 14, 2),
                    Ui.Text(route.SourceName, "strong"),
                    Ui.Text("· " + route.Where + " · " + (route.Paid ? "paid" : "free")
                        + (multi && route.Language is not null ? " · " + Vocabulary.LanguageName(route.Language) : ""), "muted"));
                if (route.Fits) line.Children.Add(Ui.InkCheck(15));
                ((TextBlock)line.Children[2]).FontSize = 14;
                panel.Children.Add(line);
            }
        }

        // "why?" opens the needs as tags and the manager's reason, under a dashed rule.
        var details = new StackPanel { Spacing = 6, IsVisible = false, Margin = new Thickness(0, 4, 0, 0) };
        details.Children.Add(new Avalonia.Controls.Shapes.Line { StartPoint = new Point(0, 0), EndPoint = new Point(330, 0), Stroke = Ui.Brush("Rule"), StrokeThickness = 1, StrokeDashArray = [3, 3] });
        var tags = new WrapPanel { Orientation = Orientation.Horizontal };
        tags.Children.Add(TagBox("skill", row.Skill));
        if (row.Languages.Count > 0) tags.Children.Add(TagBox("languages", string.Join(", ", row.Languages)));
        tags.Children.Add(TagBox("runs", string.Join(", ", row.Routes.Select(r => r.Where == "API key" ? "api-key" : "this-device").Distinct())));
        details.Children.Add(tags);
        details.Children.Add(Ui.Wrap(row.Why, "muted"));
        panel.Children.Add(details);
        why.Click += (_, _) =>
        {
            details.IsVisible = !details.IsVisible;
            ((TextBlock)why.Content!).Text = details.IsVisible ? "hide" : "why?";
        };

        var box = Ui.Box("row", panel);
        if (!row.CanServe)
        {
            box.BorderBrush = Ui.Brush("Signal");
        }

        return box;
    }

    static Control TagBox(string key, string value)
    {
        var t = new TextBlock();
        t.Inlines!.Add(new Avalonia.Controls.Documents.Run(key + " ") { FontFamily = (FontFamily)Application.Current!.FindResource("Mono")!, Foreground = Ui.Brush("Sign"), FontSize = 11.5 });
        t.Inlines.Add(new Avalonia.Controls.Documents.Run(value) { FontSize = 12, Foreground = Ui.Brush("Ink2") });
        var b = Ui.Box("tag", t);
        b.Margin = new Thickness(0, 0, 5, 4);
        return b;
    }

    Control BudgetLine()
    {
        _budgetText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        UpdateBudget();
        var change = Ui.Link("change", "ConsentBudgetChange", () =>
        {
            if (_stepper is not null) _stepper.IsVisible = !_stepper.IsVisible;
        });
        var down = Ui.Button("", "deny", "ConsentBudgetDown", () => Step(-1), "square");
        down.Content = Ui.Glyph(Ui.Minus, Ui.Brush("Ink"), 14);
        AutomationProperties.SetName(down, "Lower the budget");
        var up = Ui.Button("", "deny", "ConsentBudgetUp", () => Step(1), "square");
        up.Content = Ui.Glyph(Ui.Plus, Ui.Brush("Ink"), 14);
        AutomationProperties.SetName(up, "Raise the budget");
        _stepper = Ui.Row(8, down, up);
        _stepper.IsVisible = false;
        return Ui.Column(6, Ui.Row(6, _budgetText, Ui.Text("·", "muted"), change), _stepper);
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
        if (_model.BudgetBefore is { } before && before != _budget)
        {
            _budgetText.Inlines.Add(new Avalonia.Controls.Documents.Run("Budget " + Money.Show(before) + " → "));
        }
        else
        {
            _budgetText.Inlines.Add(new Avalonia.Controls.Documents.Run("Up to "));
        }

        _budgetText.Inlines.Add(new Avalonia.Controls.Documents.Run(Money.Show(_budget)) { FontFamily = Ui.SignFace, FontWeight = FontWeight.Bold, FontSize = 17 });
        _budgetText.Inlines.Add(new Avalonia.Controls.Documents.Run(" per session"));
        AutomationProperties.SetName(_budgetText, "Up to " + Money.Show(_budget) + " per session");
    }

    Control Footer(Button decline)
    {
        var details = Ui.Link("Detailed settings…", "ConsentDetails", () => _openSettings("sites"), underline: true);
        var buttons = Ui.Row(8, decline, _allow);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        return new Border
        {
            Background = Ui.Brush("WinBar"),
            BorderBrush = Ui.Brush("WinRule"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(14, 10, 14, 10),
            Child = Ui.Columns("Auto,*", details, buttons),
        };
    }
}
