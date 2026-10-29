using System.Globalization;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using Byoa.Wallet.Engine;
using Byoa.Wallet.Protocol;
using Byoa.Wallet.Registry;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Desktop;

/// <summary>
/// The desktop side of <see cref="IWalletUi"/>: the tray icon (with its quiet badge), the notices above the tray,
/// the consent window and the settings window. Called from any thread; everything runs on the UI thread.
/// </summary>
sealed class DesktopUi : IWalletUi, IAgentConsentUi, IEndpointConfirmationUi, ISecretDeletionUi
{
    readonly WalletServices _w;
    TrayIcon? _tray;
    WindowIcon? _icon;
    WindowIcon? _badgeIcon;
    SettingsWindow? _settings;
    HelpWindow? _help;
    PreparingHandle? _preparing;
    readonly SemaphoreSlim _agentPromptQueue = new(1, 1);
    readonly SemaphoreSlim _visitorPromptQueue = new(1, 1);
    int _waitingAgentPrompts;
    int _waitingVisitorPrompts;
    readonly object _keyGate = new();
    readonly HashSet<string> _keyWindows = new(StringComparer.Ordinal);
    readonly Dictionary<string, Queue<DateTimeOffset>> _keyHistory = new(StringComparer.Ordinal);

    public DesktopUi(WalletServices services) => _w = services;

    /// <summary>Creates the tray icon; called once the Avalonia application is up.</summary>
    public void CreateTray(Application app, Action quit)
    {
        // the menu bar of macOS takes a plain picture (44 px: 22 points on a Retina screen), Windows its .ico
        var type = OperatingSystem.IsMacOS() ? "png" : "ico";
        _icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Byoa.Wallet/Assets/tray." + type)));
        _badgeIcon = new WindowIcon(AssetLoader.Open(new Uri("avares://Byoa.Wallet/Assets/tray-badge." + type)));
        var menu = new NativeMenu();
        var open = new NativeMenuItem("Open the wallet");
        open.Click += (_, _) => OpenSettings("sites");
        var activity = new NativeMenuItem("Activity");
        activity.Click += (_, _) => OpenSettings("activity");
        var help = new NativeMenuItem("Help");
        help.Click += (_, _) => OpenHelp();
        var agent = new NativeMenuItem("My MCP");
        agent.Click += (_, _) => OpenSettings("connect");
        var exit = new NativeMenuItem("Quit");
        exit.Click += (_, _) => quit();
        menu.Items.Add(open);
        menu.Items.Add(activity);
        menu.Items.Add(help);
        menu.Items.Add(agent);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(exit);
        _tray = new TrayIcon { Icon = _icon, ToolTipText = "Agent wallet", Menu = menu, IsVisible = true };
        _tray.Clicked += (_, _) =>
        {
            _w.Engine.ClearBadge();
            OpenSettings("sites");
        };
        TrayIcon.SetIcons(app, [_tray]);
        _w.Engine.SitesChanged += () => PostSafe(UpdateBadge);
        _w.Store.Changed += () => PostSafe(() => _settings?.Invalidate());
        _w.Activity.Added += _ => PostSafe(() => _settings?.Invalidate());
        _w.Secrets.Changed += () => PostSafe(() => _settings?.Invalidate());
        _w.Direct.Changed += () => PostSafe(() => _settings?.Invalidate());
        _w.Registry.Changed += _ => PostSafe(() => _settings?.Invalidate());
        if (_w.Everywhere is { } everywhere) everywhere.StatusChanged += () => PostSafe(() => _settings?.Invalidate());
        UpdateBadge();
    }

    void UpdateBadge()
    {
        if (_tray is null) return;
        var quiet = _w.Engine.UnseenQuiet;
        _tray.Icon = quiet > 0 ? _badgeIcon : _icon;
        _tray.ToolTipText = quiet > 0 ? "Agent wallet: a site asked without a click" : "Agent wallet";
    }

    public Task<NoticeAnswer> ShowNoticeAsync(NoticeModel model, CancellationToken ct) =>
        PromptAsync(ct, agentInitiated: false, async () =>
        {
            var toast = ToastWindow.Request(model);
            toast.CloseOn(ct);
            toast.Show();
            var answer = await toast.Result;
            // A link's notice has no block: "Block in Sites…" opens the Sites list, where a block is stored.
            if (answer == "sites") OpenSettings("sites");
            return answer switch
            {
                "show" => NoticeAnswer.Show,
                "decline" => NoticeAnswer.Decline,
                "sites" => NoticeAnswer.Decline,
                "block" => NoticeAnswer.Block,
                _ => NoticeAnswer.Closed,
            };
        }, NoticeAnswer.Closed);

    public Task<ConsentAnswer> ShowConsentAsync(ConsentModel model, CancellationToken ct) =>
        PromptAsync(ct, agentInitiated: false, async () =>
        {
            var window = new ConsentWindow(model, page =>
            {
                if (page == "add-model") OpenAddModel();
                else OpenSettings(page);
            }, query => _w.Engine.SearchNewModels(query), _w.Engine.HasOpenRouterKey() && _w.Engine.Catalog.Loaded);
            window.CloseOn(ct);
            window.Show();
            return await window.Result;
        }, ConsentAnswer.Declined);

    public Task<PaidAnswer> AskPaidAsync(PaidModel model, CancellationToken ct) =>
        PromptAsync(ct, agentInitiated: false, async () =>
        {
            var toast = ToastWindow.Paid(model);
            toast.CloseOn(ct);
            toast.Show();
            return await toast.Result switch
            {
                "once" => PaidAnswer.Once,
                "always" => PaidAnswer.Always,
                _ => PaidAnswer.No,
            };
        }, PaidAnswer.No);

    /// <summary>
    /// A site reached a limit: raise it? Not in the line of the other questions (a consent window open meanwhile must not hold the site's calls
    /// back): it shows at once, in its own corner toast. "Set it in Limits…" opens the limit and counts as no answer here.
    /// </summary>
    public Task<decimal?> AskRaiseAsync(RaiseModel model, CancellationToken ct) =>
        OnUi(async () =>
        {
            var toast = ToastWindow.Raise(model);
            toast.CloseOn(ct);
            toast.Show();
            var answer = await toast.Result;
            if (answer == "limits") OpenSettings(model.SettingsPage);
            return answer is not null && answer.StartsWith("raise:", StringComparison.Ordinal)
                && int.TryParse(answer["raise:".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 0 && n < model.Raises.Count
                ? model.Raises[n].Value
                : (decimal?)null;
        });

    /// <summary>The guard held a request, or a site's goal, for the visitor: one window at a time, and no answer is a refusal.</summary>
    public Task<GuardAnswer> AskGuardAsync(GuardPrompt model, CancellationToken ct) =>
        PromptAsync(ct, agentInitiated: false, async () =>
        {
            var window = new GuardWindow(model, () => Volatile.Read(ref _waitingVisitorPrompts));
            window.Show();
            using var registration = ct.Register(() => PostSafe(window.Close));
            return await window.Result;
        }, GuardAnswer.Refuse);

    public void ShowInfo(InfoModel model) => PostSafe(() => ToastWindow.Info(model, page => OpenSettings(page)).Show());

    public IDisposable ShowPreparing(PreparingModel model)
    {
        var handle = new PreparingHandle();
        _preparing = handle;
        PostSafe(() =>
        {
            if (handle.Done) return;
            handle.Toast = ToastWindow.Preparing(model);
            handle.Toast.Show();
        });
        return handle;
    }

    /// <summary>Closes the "Preparing your model…" toast, also when it is disposed before it was shown.</summary>
    sealed class PreparingHandle : IDisposable
    {
        public volatile bool Done;
        public ToastWindow? Toast;

        public void Dispose()
        {
            Done = true;
            PostSafe(() => Toast?.Dismiss());
        }
    }

    public void QuietRequest(string origin) => PostSafe(UpdateBadge);

    public async Task<AgentConsentAnswer> AskAgentAsync(AgentConsentModel model, CancellationToken ct)
    {
        try
        {
            var answer = await PromptAsync(ct, agentInitiated: true, async () =>
            {
                var window = new AgentConsentWindow(model, () => Volatile.Read(ref _waitingAgentPrompts));
                window.Show();
                using var registration = ct.Register(() => PostSafe(window.Close));
                return await window.Result;
            }, new AgentConsentAnswer(AgentConsentDecision.NotNow, false)
            {
                Error = "The visitor was busy answering another request; try again later."
            });
            if (ct.IsCancellationRequested)
                return new AgentConsentAnswer(AgentConsentDecision.NotNow, false)
                {
                    Error = "The agent connection closed before the visitor answered."
                };
            return answer;
        }
        catch (RegistryPromptBusyException e)
        {
            return new AgentConsentAnswer(AgentConsentDecision.NotNow, false) { Error = e.Message };
        }
    }

    public async Task<RegistryOperationResult> RequestApiKeyEntryAsync(ApiKeyEntryRequest request, CancellationToken ct)
    {
        var key = request.AgentId ?? request.Actor;
        lock (_keyGate)
        {
            if (request.AgentInitiated && !_keyWindows.Add(request.SourceId))
                return RegistryOperationResult.Failure("An API-key entry window for this source is already open. Wait for the visitor to finish.");
            if (request.AgentInitiated)
            {
                if (!_keyHistory.TryGetValue(key, out var history)) _keyHistory[key] = history = new Queue<DateTimeOffset>();
                while (history.Count > 0 && history.Peek() < DateTimeOffset.UtcNow.AddHours(-1)) history.Dequeue();
                if (history.Count >= 3)
                {
                    _keyWindows.Remove(request.SourceId);
                    return RegistryOperationResult.Failure("This agent has opened the API-key field three times in the last hour. Ask the visitor to use My models in the wallet.");
                }
                history.Enqueue(DateTimeOffset.UtcNow);
            }
        }

        try
        {
            try
            {
                return await PromptAsync(ct, request.AgentInitiated, async () =>
                {
                    var window = new ApiKeyEntryWindow(request.SourceName, () => Volatile.Read(ref _waitingAgentPrompts));
                    window.Show();
                    using var registration = ct.Register(() => PostSafe(window.Close));
                    var entered = await window.Result;
                    if (entered is null) return RegistryOperationResult.Failure("The visitor cancelled the API-key entry. Ask them to try again in the wallet.");
                    // The visitor typed this value in the wallet, so this is the visitor's write path even when
                    // an agent opened the field. The agent-supplied set_api_key path remains Full-access only.
                    var result = await _w.Registry.SetApiKeyAsync(request.SourceId, entered, request.Actor,
                        fullAccess: request.FullAccess, visitorInitiated: true);
                    if (!result.Ok) ShowInfo(new InfoModel("API key", result.Message));
                    return result;
                }, RegistryOperationResult.Failure("The visitor was busy answering another request; try again later."));
            }
            catch (RegistryPromptBusyException e)
            {
                return RegistryOperationResult.Failure(e.Message);
            }
        }
        finally
        {
            if (request.AgentInitiated)
                lock (_keyGate) _keyWindows.Remove(request.SourceId);
        }
    }

    public Task<bool> ConfirmEndpointAsync(EndpointConfirmationRequest request, CancellationToken ct) =>
        PromptAsync(ct, agentInitiated: true, async () =>
        {
            var window = new EndpointConfirmationWindow(request, () => Volatile.Read(ref _waitingAgentPrompts));
            window.Show();
            using var registration = ct.Register(() => PostSafe(window.Close));
            return await window.Result;
        }, false);

    public Task<bool> ConfirmSecretDeletionAsync(SecretDeletionRequest request, CancellationToken ct) =>
        PromptAsync(ct, agentInitiated: true, async () =>
        {
            var window = new SecretDeleteWindow(request.SourceName, () => Volatile.Read(ref _waitingAgentPrompts));
            window.Show();
            using var registration = ct.Register(() => PostSafe(window.Close));
            return await window.Result;
        }, false);

    public void OpenSettings(string? page = null) => PostSafe(() =>
    {
        if (_settings is null)
        {
            _settings = new SettingsWindow(_w);
            _settings.Closed += (_, _) => _settings = null;
            _settings.Show();
        }

        _settings.Show(page);
        if (_settings.WindowState == WindowState.Minimized) _settings.WindowState = WindowState.Normal;
        _settings.Activate();
        MacActivation.BringToFront();
    });

    public void OpenHelp(string? topic = null) => PostSafe(() =>
    {
        if (_help is null)
        {
            _help = new HelpWindow(topic);
            _help.Closed += (_, _) => _help = null;
            _help.Show();
        }
        else
        {
            _help.Activate();
        }

        MacActivation.BringToFront();
    });

    public void OpenAddModel(string? sourceId = null) => PostSafe(() =>
    {
        new AddModelWindow(_w, sourceId).Show();
        MacActivation.BringToFront();
    });

    public void OpenAddAgent() => PostSafe(() =>
    {
        new AddAgentWindow(_w).Show();
        MacActivation.BringToFront();
    });

    internal static void PostSafe(Action action)
    {
        try
        {
            Dispatcher.UIThread.Post(() =>
            {
                try { action(); }
                catch (Exception e) when (e is not OutOfMemoryException) { ErrorLog.Write(WalletPaths.Home, e); }
            });
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            ErrorLog.Write(WalletPaths.Home, e);
        }
    }

    static async Task<T> OnUi<T>(Func<Task<T>> work)
    {
        try { return await Dispatcher.UIThread.InvokeAsync(work).ConfigureAwait(false); }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            ErrorLog.Write(WalletPaths.Home, e);
            throw;
        }
    }

    async Task<T> PromptAsync<T>(CancellationToken ct, bool agentInitiated, Func<Task<T>> work, T busy)
    {
        if (agentInitiated) Interlocked.Increment(ref _waitingAgentPrompts);
        else Interlocked.Increment(ref _waitingVisitorPrompts);
        var acquired = false;
        var queue = agentInitiated ? _agentPromptQueue : _visitorPromptQueue;
        try
        {
            acquired = await queue.WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            if (!acquired)
            {
                if (agentInitiated) throw new RegistryPromptBusyException();
                ShowInfo(new InfoModel("Wallet busy", "Close the open wallet window first."));
                return busy;
            }
            return await OnUi(work).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return busy; }
        finally
        {
            if (acquired) queue.Release();
            if (agentInitiated) Interlocked.Decrement(ref _waitingAgentPrompts);
            else Interlocked.Decrement(ref _waitingVisitorPrompts);
        }
    }
}
