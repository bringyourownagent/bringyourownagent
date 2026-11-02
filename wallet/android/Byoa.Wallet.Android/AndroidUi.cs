using Android.Content;
using Avalonia.Controls;
using Byoa.Wallet.Engine;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Android;

/// <summary>One question the engine put to the visitor and is waiting for: it stays here, not in a window, so that it survives the activity being made again.</summary>
abstract class Prompt
{
    protected Prompt(object model, string title, string text)
    {
        Model = model;
        Title = title;
        Text = text;
    }

    /// <summary>The engine's model of the question (<see cref="ConsentModel"/>, <see cref="PaidModel"/>, <see cref="RaiseModel"/>).</summary>
    public object Model { get; }

    /// <summary>What a notification says when the app is not on screen: the title and a line under it.</summary>
    public string Title { get; }

    public string Text { get; }

    /// <summary>The page that shows it; made by the shell the first time the question is on the screen, and kept while the question waits.</summary>
    public Control? View { get; set; }

    /// <summary>The visitor left the page or no answer came in time: the answer is "no".</summary>
    public abstract void Decline();

    public abstract Task Completed { get; }
}

sealed class Prompt<T> : Prompt
{
    readonly TaskCompletionSource<T> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly T _no;

    public Prompt(object model, string title, string text, T no) : base(model, title, text) => _no = no;

    public Task<T> Answer => _answer.Task;
    public override Task Completed => _answer.Task;

    public void Give(T answer) => _answer.TrySetResult(answer);

    public override void Decline() => Give(_no);
}

/// <summary>
/// The phone's side of <see cref="IWalletUi"/>. A question of the engine becomes a <see cref="Prompt"/> that the shell shows full screen, one at a
/// time, the oldest first; when the app is not on screen it also becomes a notification that opens the app at the question. The engine's own timeout
/// ends a question nobody opened: its token is cancelled and the answer is "no", as on the desktop. A short message needs no answer and shows
/// as a banner; it is kept for the shell when the activity is gone for a moment.
/// </summary>
sealed class AndroidUi : IWalletUi
{
    readonly AndroidServices _w;
    readonly Context _context;
    readonly object _gate = new();
    readonly List<Prompt> _prompts = [];
    readonly List<InfoModel> _undelivered = [];
    PreparingModel? _preparing;

    public AndroidUi(AndroidServices services, Context context)
    {
        _w = services;
        _context = context;
    }

    /// <summary>The question on the screen now (the oldest one waiting), or null.</summary>
    public Prompt? Current
    {
        get
        {
            lock (_gate) return _prompts.FirstOrDefault();
        }
    }

    public int Waiting
    {
        get
        {
            lock (_gate) return _prompts.Count;
        }
    }

    /// <summary>Raised (on any thread) when a question comes or goes.</summary>
    public event Action? PromptsChanged;

    /// <summary>A short message for the visitor (on any thread); when nobody listens it is kept for <see cref="TakeUndelivered"/>.</summary>
    public event Action<InfoModel>? InfoShown;

    /// <summary>The engine asks to open a page of the wallet ("sites", "sources", "activity", "limits"...).</summary>
    public event Action<string?>? SettingsRequested;

    /// <summary>Raised when "Preparing your model…" starts or ends.</summary>
    public event Action? PreparingChanged;

    public PreparingModel? Preparing
    {
        get
        {
            lock (_gate) return _preparing;
        }
    }

    public IReadOnlyList<InfoModel> TakeUndelivered()
    {
        lock (_gate)
        {
            var all = _undelivered.ToList();
            _undelivered.Clear();
            return all;
        }
    }

    /// <summary>Called when the app leaves the screen: a question that is still waiting must not wait unseen.</summary>
    public void TellAboutWaiting()
    {
        if (Current is { } prompt) Notifier.PostPrompt(_context, prompt.Title, prompt.Text);
    }

    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// "A site wants to use your AI". Links go straight to the consent (the engine asks no notice first), and a phone has no browser extension, so this
    /// is a path that never comes: it answers as a wallet with no desktop does, "closed", and the engine treats it as no answer.
    /// </summary>
    public Task<NoticeAnswer> ShowNoticeAsync(NoticeModel model, CancellationToken ct) => Task.FromResult(NoticeAnswer.Closed);

    public async Task<ConsentAnswer> ShowConsentAsync(ConsentModel model, CancellationToken ct)
    {
        var answer = await Ask(new Prompt<ConsentAnswer>(model, Kit.HostOf(model.Origin) + " wants to use your AI", "Open the wallet to look at what it asks and to allow or decline.", ConsentAnswer.Declined), ct).ConfigureAwait(false);
        // Allow: the site is going to be connected, which may take a while (its models get ready). The foreground service starts now, while the visitor
        // still looks at the app: from the background Android would refuse it. It says "Connecting to host…" until the connection is up.
        if (answer.Allow) _w.Serving.Connecting(Kit.HostOf(model.Origin));
        return answer;
    }

    public Task<PaidAnswer> AskPaidAsync(PaidModel model, CancellationToken ct) =>
        Ask(new Prompt<PaidAnswer>(model, model.Host + " needs a paid source", "Open the wallet to allow it once, always for this site, or not.", PaidAnswer.No), ct);

    public Task<decimal?> AskRaiseAsync(RaiseModel model, CancellationToken ct) =>
        Ask(new Prompt<decimal?>(model, model.Host + " reached a limit", "Open the wallet to raise the limit, or not.", null), ct);

    Task<T> Ask<T>(Prompt<T> prompt, CancellationToken ct)
    {
        var registration = ct.Register(prompt.Decline);
        lock (_gate) _prompts.Add(prompt);
        _ = prompt.Completed.ContinueWith(_ =>
        {
            registration.Dispose();
            Remove(prompt);
        }, TaskScheduler.Default);
        PromptsChanged?.Invoke();
        // the visitor is not looking at the wallet: the question comes to him
        if (!AppState.Visible) Notifier.PostPrompt(_context, prompt.Title, prompt.Text);
        // The first question is the first moment the app needs to show a notification (a later question may come while the visitor is away): Android
        // asks him now, once, whether the wallet may.
        else AppState.Activity?.RunOnUiThread(() => AppState.Activity?.AskForNotificationsOnce());
        _stale = null;
        return prompt.Answer;
    }

    string? _stale;

    /// <summary>
    /// A line for the home screen when the visitor came from the notification of a question that is gone (the app was restarted since: the question lived
    /// only in the process that died, and the site has given up waiting), or null.
    /// </summary>
    public string? StaleNote
    {
        get
        {
            lock (_gate) return _stale;
        }
    }

    /// <summary>The visitor opened a question's notification and nothing is waiting: say so.</summary>
    public void NoteStale()
    {
        lock (_gate) _stale = "That question was from before the wallet restarted; ask the site again.";
        PromptsChanged?.Invoke();
    }

    public void ClearStale()
    {
        lock (_gate) _stale = null;
    }

    /// <summary>
    /// One plain line on the home screen (an address or a code that carried nothing usable, a scanner that could not start): the visitor is taken to the home
    /// screen and reads it there. Nothing has been connected and no site has been contacted.
    /// </summary>
    public void NoteLine(string text)
    {
        lock (_gate) _stale = text;
        SettingsRequested?.Invoke("home");
        PromptsChanged?.Invoke();
    }

    void Remove(Prompt prompt)
    {
        bool none;
        lock (_gate)
        {
            _prompts.Remove(prompt);
            none = _prompts.Count == 0;
        }

        if (none) Notifier.CancelPrompt(_context);
        PromptsChanged?.Invoke();
    }

    // ------------------------------------------------------------------------------------------------

    public void ShowInfo(InfoModel model)
    {
        var listener = InfoShown;
        if (listener is not null)
        {
            listener(model);
            return;
        }

        lock (_gate)
        {
            _undelivered.Add(model);
            if (_undelivered.Count > 5) _undelivered.RemoveAt(0);
        }
    }

    public IDisposable ShowPreparing(PreparingModel model)
    {
        lock (_gate) _preparing = model;
        PreparingChanged?.Invoke();
        return new PreparingHandle(this, model);
    }

    sealed class PreparingHandle(AndroidUi owner, PreparingModel model) : IDisposable
    {
        public void Dispose()
        {
            lock (owner._gate)
            {
                if (!ReferenceEquals(owner._preparing, model)) return;
                owner._preparing = null;
            }

            owner.PreparingChanged?.Invoke();
        }
    }

    /// <summary>A request without a click was held back: a link always comes from a tap, so this is a path of the extension and a phone never takes it.</summary>
    public void QuietRequest(string origin)
    {
    }

    public void OpenSettings(string? page = null) => SettingsRequested?.Invoke(page);
}
