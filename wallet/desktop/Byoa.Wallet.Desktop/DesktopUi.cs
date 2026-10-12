using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using Byoa.Wallet.Engine;
using Byoa.Wallet.Registry;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Desktop;

/// <summary>
/// The desktop side of <see cref="IWalletUi"/>: the tray icon (with its quiet badge), the notices above the tray,
/// the consent window and the settings window. Called from any thread; everything runs on the UI thread.
/// </summary>
[SupportedOSPlatform("windows")]
sealed class DesktopUi : IWalletUi, IAgentConsentUi
{
    readonly WalletServices _w;
    TrayIcon? _tray;
    WindowIcon? _icon;
    WindowIcon? _badgeIcon;
    SettingsWindow? _settings;
    HelpWindow? _help;
    ToastWindow? _download;

    public DesktopUi(WalletServices services) => _w = services;

    /// <summary>Creates the tray icon; called once the Avalonia application is up.</summary>
    public void CreateTray(Application app, Action quit)
    {
        _icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Byoa.Wallet/Assets/tray.ico")));
        _badgeIcon = new WindowIcon(AssetLoader.Open(new Uri("avares://Byoa.Wallet/Assets/tray-badge.ico")));
        var menu = new NativeMenu();
        var open = new NativeMenuItem("Open the wallet");
        open.Click += (_, _) => OpenSettings("sites");
        var activity = new NativeMenuItem("Activity");
        activity.Click += (_, _) => OpenSettings("activity");
        var help = new NativeMenuItem("Help");
        help.Click += (_, _) => OpenHelp();
        var agent = new NativeMenuItem("Connect your AI agent");
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
        _w.Engine.SitesChanged += () => Dispatcher.UIThread.Post(UpdateBadge);
        _w.Store.Changed += () => Dispatcher.UIThread.Post(() => _settings?.Invalidate());
        _w.Activity.Added += _ => Dispatcher.UIThread.Post(() => _settings?.Invalidate());
        _w.Secrets.Changed += () => Dispatcher.UIThread.Post(() => _settings?.Invalidate());
        _w.Direct.Changed += () => Dispatcher.UIThread.Post(() => _settings?.Invalidate());
        _w.Registry.Changed += _ => Dispatcher.UIThread.Post(() => _settings?.Invalidate());
        UpdateBadge();
    }

    void UpdateBadge()
    {
        if (_tray is null) return;
        var quiet = _w.Engine.UnseenQuiet;
        _tray.Icon = quiet > 0 ? _badgeIcon : _icon;
        _tray.ToolTipText = quiet > 0 ? "Agent wallet: a site asked without a click" : "Agent wallet";
    }

    public Task<NoticeAnswer> ShowNoticeAsync(NoticeModel model, CancellationToken ct) => OnUi(async () =>
    {
        var toast = ToastWindow.Request(model);
        toast.CloseOn(ct);
        toast.Show();
        return await toast.Result switch
        {
            "show" => NoticeAnswer.Show,
            "decline" => NoticeAnswer.Decline,
            "block" => NoticeAnswer.Block,
            _ => NoticeAnswer.Closed,
        };
    });

    public Task<ConsentAnswer> ShowConsentAsync(ConsentModel model, CancellationToken ct) => OnUi(async () =>
    {
        var window = new ConsentWindow(model, page => OpenSettings(page));
        window.CloseOn(ct);
        window.Show();
        return await window.Result;
    });

    public Task<PaidAnswer> AskPaidAsync(PaidModel model, CancellationToken ct) => OnUi(async () =>
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
    });

    public void ShowInfo(InfoModel model) => Dispatcher.UIThread.Post(() => ToastWindow.Info(model, page => OpenSettings(page)).Show());

    public IDisposable ShowPreparing(PreparingModel model)
    {
        var handle = new PreparingHandle();
        Dispatcher.UIThread.Post(() =>
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
            Dispatcher.UIThread.Post(() => Toast?.Dismiss());
        }
    }

    public void QuietRequest(string origin) => Dispatcher.UIThread.Post(UpdateBadge);

    public Task<AgentConsentAnswer> AskAgentAsync(AgentConsentModel model, CancellationToken ct) => OnUi(async () =>
    {
        var window = new AgentConsentWindow(model);
        window.Show();
        using var registration = ct.Register(() => Dispatcher.UIThread.Post(window.Close));
        return await window.Result;
    });

    public Task<RegistryOperationResult> RequestApiKeyEntryAsync(ApiKeyEntryRequest request, CancellationToken ct) => OnUi(async () =>
    {
        var window = new ApiKeyEntryWindow(request.SourceName);
        window.Show();
        using var registration = ct.Register(() => Dispatcher.UIThread.Post(window.Close));
        var key = await window.Result;
        if (key is null) return RegistryOperationResult.Failure("The visitor cancelled the API-key entry. Ask them to try again in the wallet.");
        var result = await _w.Registry.SetApiKeyAsync(request.SourceId, key, request.Actor);
        if (!result.Ok) ShowInfo(new InfoModel("API key", result.Message));
        return result;
    });

    public void DownloadStarted(ISpeechSource source) => Dispatcher.UIThread.Post(() =>
    {
        if (source is not IDownloadableSource download || _download is { IsVisible: true }) return;
        _download = ToastWindow.Download(source, download);
        _download.Show();
    });

    public void OpenSettings(string? page = null) => Dispatcher.UIThread.Post(() =>
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
    });

    public void OpenHelp(string? topic = null) => Dispatcher.UIThread.Post(() =>
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
    });

    public void OpenAddModel(string? sourceId = null) => Dispatcher.UIThread.Post(() => new AddModelWindow(_w, sourceId).Show());

    static Task<T> OnUi<T>(Func<Task<T>> work) => Dispatcher.UIThread.InvokeAsync(work);
}
