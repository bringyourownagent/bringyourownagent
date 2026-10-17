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
/// The small notice above the tray, in the style of a Windows notification. It never takes the focus away from
/// the browser (it is shown without activation). It answers with the id of the button pressed, or null when it was
/// closed or timed out.
/// </summary>
sealed class ToastWindow : CornerWindow
{
    readonly TaskCompletionSource<string?> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly StackPanel _body = new() { Spacing = 10 };
    CancellationTokenRegistration _timeout;
    bool _closed;

    ToastWindow(string title) : base(352)
    {
        Title = title;
        ShowActivated = false;
        var card = new Border { Child = _body, Padding = new Thickness(14, 12, 12, 12), Margin = new Thickness(ShadowMargin), Background = Brushes.White };
        card.Classes.Add("popup");
        card.Classes.Add("notice");
        Content = card;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Answer(null);
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _timeout.Dispose();
            _answer.TrySetResult(null);
        };
    }

    public Task<string?> Result => _answer.Task;

    void Answer(string? id)
    {
        _answer.TrySetResult(id);
        if (!_closed) Close();
    }

    /// <summary>Closes the toast when <paramref name="ct"/> is cancelled (no answer in time).</summary>
    public void CloseOn(CancellationToken ct) =>
        _timeout = ct.Register(() => DesktopUi.PostSafe(() => Answer(null)));

    Control Header(Control icon, Control text, bool closable = true)
    {
        var close = new Button { Content = Ui.Glyph(Ui.Cross, Ui.Brush("Ink2"), 12, 2.4) };
        close.Classes.Add("close");
        AutomationProperties.SetAutomationId(close, "NoticeClose");
        AutomationProperties.SetName(close, "Close the notice");
        close.Click += (_, _) => Answer(null);
        var grid = Ui.Columns("Auto,*,Auto", icon, text, closable ? close : new Panel());
        icon.Margin = new Thickness(0, 2, 10, 0);
        icon.VerticalAlignment = VerticalAlignment.Top;
        close.VerticalAlignment = VerticalAlignment.Top;
        return grid;
    }

    /// <summary>"host wants to use your AI": Show, Decline, Block this site (a link's notice: Show, Not now, Block in Sites…).</summary>
    public static ToastWindow Request(NoticeModel model)
    {
        var t = new ToastWindow("Agent wallet notice");
        var lines = Ui.Column(2,
            Ui.Runs((model.Host, true), (" wants to use your AI", false)),
            Ui.Text("Agent wallet", "muted", "small"));
        AutomationProperties.SetName(lines, model.Host + " wants to use your AI");
        t._body.Children.Add(t.Header(Ui.Tile(Ui.Globe, 24), lines));
        var (declineText, blockText) = Choices(model);
        var show = Ui.Button("Show", "allow", "NoticeShow", () => t.Answer("show"), "small");
        var decline = Ui.Button(declineText, "deny", "NoticeDecline", () => t.Answer("decline"), "small");
        var blockAnswer = model.FromLink ? "sites" : "block";
        var block = Ui.Link(blockText, "NoticeBlock", () => t.Answer(blockAnswer));
        block.HorizontalAlignment = HorizontalAlignment.Right;
        t._body.Children.Add(Ui.Columns("Auto,*", Ui.Row(8, show, decline), block));
        return t;
    }

    /// <summary>The words of the notice's "no" and of its block link. A link's notice cannot block: its link opens Sites.</summary>
    public static (string Decline, string Block) Choices(NoticeModel model) =>
        model.FromLink ? ("Not now", "Block in Sites…") : ("Decline", "Block this site");

    /// <summary>"Your local models could not answer: use the paid source?" Allow once, Always for this site, No.</summary>
    public static ToastWindow Paid(PaidModel model)
    {
        var t = new ToastWindow("Agent wallet paid fallback");
        var lines = Ui.Column(3,
            Ui.Runs((model.Host, true), (" needs a paid source", false)),
            Ui.Wrap("Use " + model.SourceName + " for this clip? About " + Money.Show(model.Estimate) + ", from your API key. " + model.Why, "muted"),
            Ui.Text("Agent wallet", "muted", "small"));
        t._body.Children.Add(t.Header(Ui.Tile(Ui.Key, 24, "SignalDeep"), lines));
        var once = Ui.Button("Allow once", "allow", "PaidOnce", () => t.Answer("once"), "small");
        var always = Ui.Button("Always for this site", "deny", "PaidAlways", () => t.Answer("always"), "small");
        var no = Ui.Link("No", "PaidNo", () => t.Answer("no"));
        no.HorizontalAlignment = HorizontalAlignment.Right;
        t._body.Children.Add(Ui.Columns("Auto,*", Ui.Row(8, once, always), no));
        return t;
    }

    /// <summary>A message with an optional button; it closes on its own after a while.</summary>
    public static ToastWindow Info(InfoModel model, Action<string?>? open)
    {
        var t = new ToastWindow("Agent wallet message");
        var lines = Ui.Column(3, Ui.Runs((model.Title, true)), Ui.Wrap(model.Text, "muted"));
        t._body.Children.Add(t.Header(Ui.Tile(Ui.Globe, 24), lines));
        if (model.ActionText is not null)
        {
            var button = Ui.Button(model.ActionText, "deny", "InfoAction", () =>
            {
                open?.Invoke(model.OpenPage);
                t.Answer("open");
            }, "small");
            t._body.Children.Add(button);
        }

        DispatcherTimer.RunOnce(() => t.Answer(null), TimeSpan.FromSeconds(20));
        return t;
    }

    /// <summary>
    /// "Preparing your model…": shown after Allow while the routed models load, so the page gets its grant only
    /// when its first call will be quick. It closes itself when the wallet disposes it; Close only hides it.
    /// </summary>
    public static ToastWindow Preparing(PreparingModel model)
    {
        var t = new ToastWindow("Agent wallet preparing");
        var bar = new ProgressBar { IsIndeterminate = true };
        bar.Classes.Add("meter");
        var title = Ui.Runs(("Preparing your model…", true));
        AutomationProperties.SetAutomationId(title, "NoticePreparing");
        AutomationProperties.SetName(title, "Preparing your model");
        var lines = Ui.Column(4,
            title,
            Ui.Wrap("Loading " + string.Join(" and ", model.Sources) + " for " + model.Host + ", so its first answer is quick.", "muted"),
            bar,
            Ui.Text("Agent wallet", "muted", "small"));
        t._body.Children.Add(t.Header(Ui.Tile(Ui.Screen, 24), lines));
        return t;
    }

    /// <summary>Closes the toast from code (no answer).</summary>
    public void Dismiss() => Answer(null);
}
