using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Byoa.Wallet.Engine;

namespace Byoa.Wallet.Android;

/// <summary>One screen of the wallet (home, models, sites...): it builds its content once and rebuilds it when something it shows has changed.</summary>
abstract class Page
{
    protected Page(Shell shell) => Shell = shell;

    protected Shell Shell { get; }
    protected AndroidServices W => Shell.W;

    /// <summary>The words in the bar above the screen; null gives the screen the whole height (the home screen has a header of its own).</summary>
    public abstract string? Title { get; }

    public Control View => _view ??= Build();
    Control? _view;

    /// <summary>The screen has been drawn once (before that there is nothing to draw again).</summary>
    protected bool Built => _view is not null;

    protected abstract Control Build();

    /// <summary>Something the screen shows has changed (settings, activity, connections): the screen draws it again. It is called on the UI thread.</summary>
    public virtual void Refresh()
    {
    }

    /// <summary>The Back button: true when the screen used it (it closed something of its own), false when the shell should go back.</summary>
    public virtual bool HandleBack() => false;
}

/// <summary>
/// The wallet's one view: the bar with the Back arrow, a banner for a short message, and the screen on top of a stack of screens. A question of the
/// engine (a consent, a paid fallback, a raise of a limit) covers all of it, full screen, until it is answered; Back then declines it.
/// The shell is made once for the process and shown again whenever the activity is made again, so what the visitor was doing is still there.
/// </summary>
sealed class Shell : UserControl
{
    readonly Stack<Page> _stack = new();
    readonly Border _bar = new();
    readonly Button _backButton;
    readonly TextBlock _barTitle = Kit.Text("", "h2");
    readonly Border _banner = new() { IsVisible = false };
    readonly Border _preparing = new() { IsVisible = false };
    readonly TextBlock _preparingText = Kit.Text("", "small");
    readonly ContentControl _content = new();
    readonly Grid _frame = new();
    readonly DispatcherTimer _redraw = new() { Interval = TimeSpan.FromMilliseconds(200) };
    DispatcherTimer? _bannerTimer;
    Prompt? _shown;

    public Shell(AndroidServices services)
    {
        W = services;
        Classes.Add("shell");
        var back = _backButton = Kit.Button("", "close", "BarBack", () => Back());
        back.Content = Kit.Glyph(Kit.Back, Kit.Brush("Ink2"), 22, 2.4);
        AutomationProperties.SetName(back, "Back");
        _bar.Padding = new Thickness(4, 0, 12, 0);
        _bar.BorderBrush = Kit.Brush("WinRule");
        _bar.BorderThickness = new Thickness(0, 0, 0, 1);
        _bar.Background = Kit.Brush("WinBar");
        _bar.Child = Kit.Columns("Auto,*", back, _barTitle);
        _barTitle.FontWeight = FontWeight.SemiBold;
        AutomationProperties.SetAutomationId(_barTitle, "BarTitle");

        _banner.Margin = new Thickness(12, 8, 12, 0);
        _preparing.Margin = new Thickness(12, 8, 12, 0);
        _preparing.Classes.Add("field");
        _preparing.Child = Kit.Column(4, _preparingText, new ProgressBar { IsIndeterminate = true, Height = 6 });
        AutomationProperties.SetAutomationId(_preparing, "Preparing");

        _frame.RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*");
        Grid.SetRow(_bar, 0);
        Grid.SetRow(_banner, 1);
        Grid.SetRow(_preparing, 2);
        Grid.SetRow(_content, 3);
        _frame.Children.Add(_bar);
        _frame.Children.Add(_banner);
        _frame.Children.Add(_preparing);
        _frame.Children.Add(_content);
        Content = _frame;

        _redraw.Tick += (_, _) =>
        {
            _redraw.Stop();
            if (_shown is null) _stack.Peek().Refresh();
        };
        // what the screens show changes on any thread; the shell redraws on the UI thread, at most five times a second
        void Changed() => Dispatcher.UIThread.Post(() =>
        {
            if (!_redraw.IsEnabled) _redraw.Start();
        });
        W.Store.Changed += Changed;
        W.Activity.Added += _ => Changed();
        W.Secrets.Changed += Changed;
        W.Registry.Changed += _ => Changed();
        W.Serving.Changed += Changed;
        W.Ui.PromptsChanged += () => Dispatcher.UIThread.Post(ShowCurrent);
        W.Ui.InfoShown += info => Dispatcher.UIThread.Post(() => Say(info));
        W.Ui.PreparingChanged += () => Dispatcher.UIThread.Post(ShowPreparing);
        W.Ui.SettingsRequested += page => Dispatcher.UIThread.Post(() => Open(page));

        _stack.Push(new HomePage(this));
        foreach (var info in W.Ui.TakeUndelivered()) Say(info);
        ShowCurrent();
    }

    public AndroidServices W { get; }

    /// <summary>The page the visitor is on (under a question, if one covers it).</summary>
    public Page Top => _stack.Peek();

    public void Push(Page page)
    {
        W.Ui.ClearStale();
        _stack.Push(page);
        ShowCurrent();
    }

    /// <summary>Goes back one screen; false when there is none (the home screen).</summary>
    public bool Back()
    {
        if (_stack.Count <= 1) return false;
        _stack.Pop();
        ShowCurrent();
        return true;
    }

    /// <summary>Opens a screen by the name the engine uses for it; the screens a phone has not got yet open the nearest one.</summary>
    public void Open(string? page)
    {
        Page? next = page switch
        {
            "sources" or "add-model" or "models" => new ModelsPage(this),
            "sites" => new SitesPage(this),
            "limits" or "skills" => new LimitsPage(this),
            _ when page is { } p && p.StartsWith("limits:", StringComparison.Ordinal) => new LimitsPage(this, p["limits:".Length..], reached: true),     // the notice that a site reached a limit: that site comes first, and the page says so
            "activity" => new ActivityPage(this),
            "help" => new HelpPage(this),
            _ => null,
        };
        if (next is null)
        {
            while (_stack.Count > 1) _stack.Pop();
            ShowCurrent();
            return;
        }

        while (_stack.Count > 1) _stack.Pop();
        Push(next);
    }

    /// <summary>
    /// The Back button of the phone. A question on the screen is declined; a screen goes back; at the home screen the app goes to the
    /// background, as with the Home button, and keeps serving what it serves.
    /// </summary>
    public bool HandleBack()
    {
        if (W.Ui.Current is { } prompt)
        {
            if (prompt.View is IBackHandler { } handler && handler.HandleBack()) return true;
            prompt.Decline();
            return true;
        }

        if (_stack.Peek().HandleBack()) return true;
        if (Back()) return true;
        AppState.Activity?.MoveTaskToBack(true);
        return true;
    }

    void ShowCurrent()
    {
        var prompt = W.Ui.Current;
        if (prompt is not null)
        {
            prompt.View ??= PromptPages.Create(prompt, this);
            _shown = prompt;
            _bar.IsVisible = false;
            _content.Content = prompt.View;
            return;
        }

        _shown = null;
        var page = _stack.Peek();
        _bar.IsVisible = page.Title is not null;
        _barTitle.Text = page.Title ?? "";
        _backButton.IsVisible = _stack.Count > 1;
        page.Refresh();
        _content.Content = page.View;
    }

    // ------------------------------------------------------------------------------------------------

    /// <summary>A short message of the engine, at the top of the screen for a while (it needs no answer).</summary>
    void Say(InfoModel info)
    {
        var lines = Kit.Column(2, Kit.Text(info.Title, "strong"), Kit.Text(info.Text, "muted"));
        if (info.ActionText is { } action)
            lines.Children.Add(Kit.Button(action, "tint", "InfoAction", () =>
            {
                Dismiss();
                Open(info.OpenPage);
            }, "small"));
        var close = Kit.Button("", "close", "InfoClose", Dismiss);
        close.Content = Kit.Glyph(Kit.Cross, Kit.Brush("Ink2"), 14, 2.4);
        AutomationProperties.SetName(close, "Close the message");
        _banner.Classes.Clear();
        _banner.Classes.Add("field");
        _banner.Child = Kit.Columns("*,Auto", lines, close);
        AutomationProperties.SetAutomationId(_banner, "Info");
        _banner.IsVisible = true;
        _bannerTimer?.Stop();
        _bannerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _bannerTimer.Tick += (_, _) => Dismiss();
        _bannerTimer.Start();
    }

    void Dismiss()
    {
        _bannerTimer?.Stop();
        _banner.IsVisible = false;
    }

    void ShowPreparing()
    {
        var model = W.Ui.Preparing;
        _preparing.IsVisible = model is not null;
        if (model is not null) _preparingText.Text = "Preparing your model… Loading " + string.Join(" and ", model.Sources) + " for " + model.Host + ".";
    }

    // ------------------------------------------------------------------------------------------------

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (TopLevel.GetTopLevel(this) is not { } top) return;
        top.BackRequested += OnBackRequested;
        // Android 15 draws the app under the system bars: keep the screens clear of them
        if (top.InsetsManager is { } insets)
        {
            Padding = insets.SafeAreaPadding;
            insets.SafeAreaChanged += OnSafeArea;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is { } top)
        {
            top.BackRequested -= OnBackRequested;
            if (top.InsetsManager is { } insets) insets.SafeAreaChanged -= OnSafeArea;
        }

        base.OnDetachedFromVisualTree(e);
    }

    void OnSafeArea(object? sender, Avalonia.Controls.Platform.SafeAreaChangedArgs e) => Padding = e.SafeAreaPadding;

    void OnBackRequested(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => e.Handled = HandleBack();
}

/// <summary>A question's page that has something of its own to close before Back declines it (the "Add models" window over the consent).</summary>
interface IBackHandler
{
    bool HandleBack();
}
