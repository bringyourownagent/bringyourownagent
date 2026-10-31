using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
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
sealed partial class ConsentWindow : CornerWindow
{
    readonly ConsentModel _model;
    readonly Action<string?> _openSettings;
    readonly Func<string?, IReadOnlyList<ModelChoice>>? _search;
    readonly bool _canSearch;
    readonly Dictionary<string, ModelPick> _picks = new(StringComparer.Ordinal);
    CheckBox? _siteLimits;
    CheckBox? _checkSame;
    readonly TaskCompletionSource<ConsentAnswer> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly HashSet<string> _leftOff = [];
    readonly Button _allow;
    decimal _budget;
    CancellationTokenRegistration _timeout;
    bool _closed;
    TextBlock? _budgetText;
    Control? _stepper;
    Border? _glow;                                 // the ring of light around the check tick, while the visitor has not ticked it
    DispatcherTimer? _pulse;
    bool _glowBright;
    static double? _preferredCard;                 // the card's width (DIPs) the visitor last chose: the next windows open as wide

    /// <param name="searchModels">Searches the public model list for the models behind the visitor's own OpenRouter key; null when there is none.</param>
    public ConsentWindow(ConsentModel model, Action<string?> openSettings, Func<string?, IReadOnlyList<ModelChoice>>? searchModels = null, bool canSearch = false) : base(400)
    {
        _model = model;
        _openSettings = openSettings;
        _search = searchModels;
        _canSearch = canSearch;
        _budget = model.Budget;
        Title = "Agent wallet consent";
        ShowActivated = true;
        MinWidth = 340 + 2 * ShadowMargin;
        MaxWidth = 960;
        if (_preferredCard is { } wide) Width = Math.Clamp(wide, 340, 960 - 2 * ShadowMargin) + 2 * ShadowMargin;


        _allow = Ui.Button(model.PausedNote is null ? "Allow" : "Resume", "allow", "ConsentAllow", () => Answer(true));
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
            MaxHeight = Math.Max(240, WorkingHeight - 300),
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Top,
        };
        layout.Children.Add(scroll);
        card.Child = layout;
        // thin strips over the card's left and right edges change the width of the window
        var frame = new Grid();
        frame.Children.Add(card);
        frame.Children.Add(EdgeGrip(left: true));
        frame.Children.Add(EdgeGrip(left: false));
        Content = frame;
        UpdateAllow();

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Answer(false);
            }
            else if (e.Key is Key.Enter or Key.Space)
            {
                e.Handled = true;
            }
        };
        Opened += (_, _) =>
        {
            Activate();
            Focus();
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _timeout.Dispose();
            _answer.TrySetResult(ConsentAnswer.Declined);
        };
    }

    public Task<ConsentAnswer> Result => _answer.Task;

    /// <summary>
    /// A strip over the card's left or right edge: dragging it widens or narrows the window. The window is a corner window with no frame of its own,
    /// so the width is set here from the pointer's place on the screen; a left edge keeps the right edge where it is, a right edge keeps the left one.
    /// </summary>
    Control EdgeGrip(bool left)
    {
        var grip = new Border
        {
            Width = 6, Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.SizeWestEast),
            HorizontalAlignment = left ? HorizontalAlignment.Left : HorizontalAlignment.Right,
            Margin = left ? new Thickness(ShadowMargin, 0, 0, 0) : new Thickness(0, 0, ShadowMargin, 0),
        };
        AutomationProperties.SetAutomationId(grip, left ? "ConsentGripLeft" : "ConsentGripRight");
        var startX = 0;
        var startWidth = 0.0;
        var startPosition = new PixelPoint();
        int ScreenX(PointerEventArgs e) => ScreenArea.CursorX() ?? this.PointToScreen(e.GetPosition(this)).X;
        grip.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            e.Handled = true;                                   // not a drag of the whole window
            e.Pointer.Capture(grip);
            startX = ScreenX(e);
            startWidth = Width;
            startPosition = Frame()?.Position ?? Position;
            Detach();
        };
        grip.PointerMoved += (_, e) =>
        {
            if (!ReferenceEquals(e.Pointer.Captured, grip)) return;
            var moved = (ScreenX(e) - startX) / RenderScaling;      // DIPs; to the right is positive
            var wanted = Math.Clamp(left ? startWidth - moved : startWidth + moved, MinWidth, Math.Min(MaxWidth, WorkingWidth));
            Width = wanted;
            if (left) Position = new PixelPoint(startPosition.X + (int)Math.Round((startWidth - wanted) * RenderScaling), startPosition.Y);
        };
        grip.PointerReleased += (_, e) =>
        {
            if (!ReferenceEquals(e.Pointer.Captured, grip)) return;
            e.Pointer.Capture(null);
            _preferredCard = Width - 2 * ShadowMargin;
        };
        return grip;
    }

    public void CloseOn(CancellationToken ct) => _timeout = ct.Register(() => DesktopUi.PostSafe(() =>
    {
        _answer.TrySetResult(ConsentAnswer.Declined);
        if (!_closed) Close();
    }));

    void Answer(bool allow)
    {
        _answer.TrySetResult(allow
            ? new ConsentAnswer(true, _budget, _model.AutoDecide, _leftOff.ToList(),
                _picks.Count == 0 ? null : _picks.ToDictionary(p => p.Key, p => (IReadOnlyList<string>)p.Value.Order.ToList(), StringComparer.Ordinal),
                _model.ShowModelNames, _siteLimits?.IsChecked != false, _checkSame?.IsChecked == true)
            : ConsentAnswer.Declined);
        if (!_closed) Close();
    }

    /// <summary>
    /// The connect check code (CONTRACT-DIRECT.md section 3a): the page where the visitor pressed Connect shows the same four characters. When the
    /// site says it shows the code, Allow waits for his tick that they are the same; otherwise he is told that nothing can prove he started this.
    /// </summary>
    Control CheckBlock(string code)
    {
        var big = new TextBlock
        {
            Text = code, FontSize = 30, FontWeight = FontWeight.Bold, LetterSpacing = 6,
            FontFamily = new FontFamily("Cascadia Mono, Menlo, Consolas, DejaVu Sans Mono, Liberation Mono, monospace"), HorizontalAlignment = HorizontalAlignment.Center,
        };
        AutomationProperties.SetAutomationId(big, "ConsentCheckCode");
        AutomationProperties.SetName(big, code);
        var left = Ui.Column(2, Ui.Text("Check code", "strong"), big);
        var right = Ui.Column(6);
        right.Margin = new Thickness(14, 0, 0, 0);
        if (_model.CheckRequired)
        {
            var how = Ui.Wrap("The page where you pressed Connect shows the same code. If you did not just press Connect on this site yourself, Decline.", "muted", "small");
            _checkSame = new CheckBox { Content = Ui.Wrap("The code is the same on the site's page"), FontSize = 14 };
            AutomationProperties.SetAutomationId(_checkSame, "ConsentCheckSame");
            AutomationProperties.SetName(_checkSame, "The code is the same on the site's page");
            _checkSame.IsCheckedChanged += (_, _) =>
            {
                UpdateAllow();
                RefreshGlow();
            };
            right.Children.Add(how);
            right.Children.Add(Glow(_checkSame));
        }
        else
        {
            var warn = Ui.Wrap(_model.Handed ? HandedNoCheck : "This site gives no way to check that you started this connection. Allow only if you pressed Connect on its page a moment ago.", "strong", "small");
            AutomationProperties.SetAutomationId(warn, "ConsentNoCheck");
            right.Children.Add(warn);
        }

        return Ui.Box("field", Ui.Columns("Auto,*", left, right));
    }

    /// <summary>
    /// The tick that confirms the code is what the visitor must press before Allow works, and it is easy to miss: it sits in a ring of amber light
    /// that swells and fades once a second until the tick is made.
    /// </summary>
    Control Glow(CheckBox tick)
    {
        var duration = TimeSpan.FromMilliseconds(500);
        _glow = new Border
        {
            Padding = new Thickness(8, 5, 10, 5),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1.5),
            BorderBrush = Brushes.Transparent,
            Background = Brushes.Transparent,
            Child = tick,
            Transitions = new Transitions
            {
                new BoxShadowsTransition { Property = Border.BoxShadowProperty, Duration = duration, Easing = new SineEaseInOut() },
                new BrushTransition { Property = Border.BorderBrushProperty, Duration = duration, Easing = new SineEaseInOut() },
                new BrushTransition { Property = Border.BackgroundProperty, Duration = duration, Easing = new SineEaseInOut() },
            },
        };
        _pulse = new DispatcherTimer { Interval = duration };
        _pulse.Tick += (_, _) =>
        {
            _glowBright = !_glowBright;
            ShowGlow();
        };
        Closed += (_, _) => _pulse.Stop();
        RefreshGlow();
        return _glow;
    }

    /// <summary>The ring pulses while the tick is not made; once it is made the ring goes out and stays out (and comes back if the tick is taken away).</summary>
    void RefreshGlow()
    {
        if (_glow is null || _pulse is null) return;
        if (_checkSame is { IsChecked: not true })
        {
            if (!_pulse.IsEnabled)
            {
                _glowBright = true;
                _pulse.Start();
            }
        }
        else
        {
            _pulse.Stop();
            _glowBright = false;
        }

        ShowGlow();
    }

    void ShowGlow()
    {
        if (_glow is null) return;
        if (_checkSame is not { IsChecked: not true })
        {
            _glow.BoxShadow = default;
            _glow.BorderBrush = Brushes.Transparent;
            _glow.Background = Brushes.Transparent;
            return;
        }

        var bright = _glowBright;
        _glow.BoxShadow = new BoxShadows(new BoxShadow { Blur = bright ? 22 : 8, Spread = bright ? 2 : 0, Color = Color.FromArgb((byte)(bright ? 215 : 85), 255, 168, 40) });
        _glow.BorderBrush = new SolidColorBrush(Color.FromArgb((byte)(bright ? 255 : 120), 255, 176, 48));
        _glow.Background = new SolidColorBrush(Color.FromArgb((byte)(bright ? 42 : 14), 255, 176, 48));
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
            Child = Ui.Columns("Auto,*,Auto", icon, Ui.Plain("Agent wallet", "label"), close),
        };
        icon.Margin = new Thickness(0, 0, 8, 0);
        bar.Cursor = DragCursor;        // the whole window drags (CornerWindow); the title bar is where one looks for the handle
        return bar;
    }

    Control Body()
    {
        var body = new StackPanel { Spacing = 10, Margin = new Thickness(16, 14, 16, 14) };

        // The browser vouches for an extension origin. A direct link has no such proof until the site's welcome echoes the link, after the visitor allows it.
        var host = Ui.Text(_model.Host, "hostmono");
        // A link has no proof of where it came from until the site answers after Allow; the check code below is what the visitor can act on,
        // so no line here says "came by a link". Only a page that is not secure is told.
        StackPanel? verified = _model.FromLink ? null
            : _model.Secure ? Ui.Row(4, Ui.InkCheck(14), Ui.Text("verified", "small", "strong"))
            : Ui.Row(4, Ui.Text("local test page (http)", "muted", "small"));
        if (verified is not null && _model.Secure) ((TextBlock)verified.Children[1]).Foreground = Ui.Brush("Sign");
        var hostInfo = verified is null ? Ui.Column(3, host) : Ui.Column(3, host, verified);
        hostInfo.Margin = new Thickness(9, 0, 6, 0);
        // the chips ("updated", "paused") sit at the right end of the row; the host takes the room that is left
        var chipRow = Ui.Row(4);
        if (_model.IsDifference) chipRow.Children.Add(Ui.Chip("updated", "outline"));
        if (_model.PausedNote is not null) chipRow.Children.Add(Ui.Chip("paused", "outline"));
        var hostRow = Ui.Columns("Auto,*,Auto", Ui.Tile(Ui.Globe, 28), hostInfo, chipRow);
        AutomationProperties.SetName(hostRow, _model.Host);
        body.Children.Add(hostRow);
        if (_model.Handed) body.Children.Add(HandedNotice());

        // What the site says it is for, as it wrote it (no quotes): its first two lines, the rest behind "More".
        body.Children.Add(Ui.Box("field", Expandable(_model.Summary, null, "ConsentPurpose")));

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
        if (_model.IsDifference && _model.Rows.Count == 0 && _model.Limits is null)
        {
            body.Children.Add(Ui.Wrap("No new skill. The site now expects to spend more per session:", "muted"));
        }

        if (_model.Limits is { } ask) body.Children.Add(LimitsBlock(ask));
        body.Children.Add(BudgetLine());
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
                UpdateAllow();
            };
            title.Children.Add(include);
        }

        if (row.IsNew) title.Children.Add(Ui.Text("New:", "strong"));
        title.Children.Add(Ui.Text(row.SkillName, "strong"));
        if (!row.Required) title.Children.Add(Ui.Chip("Optional"));
        if (row.Models is not null) return ModelRow(row, panel, title);

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
            // Languages that go to the same source are one line; only when the sources differ is each line told its languages.
            var groups = RouteGroup.Of(row.Routes);
            foreach (var group in groups)
            {
                var route = group.Route;
                // the name wraps when it is long (a model id and "this computer"), and the check mark stays at the right edge
                var arrow = Ui.Glyph(Ui.Arrow, Ui.Brush("Muted"), 14, 2);
                var kind = Ui.Glyph(route.Where == "API key" ? Ui.Key : Ui.Screen, Ui.Brush("Ink2"), 14, 2);
                var name = Ui.Wrap(route.SourceName, "strong");
                name.FontSize = 14;
                arrow.Margin = new Thickness(0, 0, 6, 0);
                kind.Margin = new Thickness(0, 0, 6, 0);
                Control mark = route.Fits ? Ui.InkCheck(15) : new Panel();
                mark.Margin = new Thickness(6, 0, 0, 0);
                var line = Ui.Columns("Auto,Auto,*,Auto", arrow, kind, name, mark);
                // Where it runs, its price and (when the sources differ) its languages go under the name, and the speed note under that:
                // at the narrow consent width a long model name leaves no room for them on its own line.
                var about = route.Where + " · " + (route.Paid ? "paid" : "free")
                    + (groups.Count > 1 && group.Languages.Count > 0 ? " · " + string.Join(", ", group.Languages.Select(Vocabulary.LanguageName)) : "");
                var lines = Ui.Column(2, line, Ui.Wrap(about, "muted", "small"));
                if (route.Detail is { Length: > 0 } detail) lines.Children.Add(Ui.Wrap(detail, "muted", "small"));
                foreach (var under in lines.Children.Skip(1)) under.Margin = new Thickness(20, 0, 0, 0);
                panel.Children.Add(lines);
            }
        }

        // "why?" opens the needs as tags and the manager's reason, under a dashed rule.
        var details = new StackPanel { Spacing = 6, IsVisible = false, Margin = new Thickness(0, 4, 0, 0) };
        details.Children.Add(new Border { Height = 1, Background = Ui.Brush("Rule"), Margin = new Thickness(0, 2) });
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
        return Ui.Column(6, Ui.Text("How much may this site spend?", "strong"), Ui.Row(6, _budgetText, Ui.Text("·", "muted"), change), _stepper);
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
        var row = Ui.Columns("Auto,*", details, buttons);
        // the check code and its tick are in the footer, right above Allow: nothing to scroll to before the decision
        Control content = _model.CheckCode is { } code ? Ui.Column(12, CheckBlock(code), row) : row;
        return new Border
        {
            Background = Ui.Brush("WinBar"),
            BorderBrush = Ui.Brush("WinRule"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(14, 10, 14, 10),
            Child = content,
        };
    }
}
