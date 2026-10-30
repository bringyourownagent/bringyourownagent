using System.Runtime.Versioning;
using Byoa.Wallet.Activity;
using Byoa.Wallet.Direct;
using Byoa.Wallet.Engine;
using Byoa.Wallet.Everywhere;
using Byoa.Wallet.Guard;
using Byoa.Wallet.Protocol;
using Byoa.Wallet.Providers;
using Byoa.Wallet.Registry;
using Byoa.Wallet.Secrets;
using Byoa.Wallet.Settings;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Desktop;

/// <summary>Everything the running wallet is made of, created once at start.</summary>
sealed class WalletServices : IDisposable
{
    public WalletServices()
    {
        var home = WalletPaths.Home;
        Directory.CreateDirectory(home);
        // A Linux session bus is asked about the Secret Service while the windows are still being made, so that the first page that
        // says how keys are protected does not wait for it.
        if (OperatingSystem.IsLinux()) _ = Task.Run(() => _ = SecretProtectors.Storage);
        Store = new SettingsStore(Path.Combine(home, "settings.json"));
        Secrets = new SecretStore(Path.Combine(home, "secrets.dat"));
        Activity = new ActivityLog(home, DateTimeOffset.UtcNow);
        Http = new HttpClient { Timeout = TimeSpan.FromHours(2) };
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("BYOA-agent-wallet/" + WalletPaths.Version);
        Sources = new SourceSet(Store, Secrets, Http, catalogPath: Path.Combine(home, "models-catalog.json"));
        Ui = new DesktopUi(this);
        Engine = new WalletEngine(Store, Sources.All, Activity, Ui, textSources: Sources.Text)
        {
            Catalog = Sources.Catalog,
            HasOpenRouterKey = () => Secrets.Has(OpenRouterSource.SecretName),
        };
        // The guard: Jev behind the visitor's OpenRouter key by default, or the text source he picked in Agent guard (a model on this
        // computer keeps the requests home and costs nothing). What a paid judge spends is counted in a file of its own.
        GuardLedger = new GuardLedger(Path.Combine(home, "guard-ledger.json"));
        // (BYOA_GUARD_ENDPOINT points a test run at a fake decision server; only an address on this computer is taken, so that the key can go nowhere else)
        var guardAddress = Environment.GetEnvironmentVariable("BYOA_GUARD_ENDPOINT") is { Length: > 0 } given
            && Uri.TryCreate(given, UriKind.Absolute, out var uri) && uri.IsLoopback ? uri : null;
        // (Jev has two doors: OpenRouter with the OpenRouter key, or TypeSafe's own API with a TypeSafe key; the visitor picks one in Agent guard)
        Engine.Guard = new GuardService(Store, GuardLedger, new JevDecisionClient(Sources.TextHttp, () => Secrets.Get(OpenRouterSource.SecretName), guardAddress),
            judgeClient: JudgeClient,
            typeSafeClient: new JevDecisionClient(Sources.TextHttp, () => Secrets.Get(JevDecisionClient.TypeSafeSecretName),
                guardAddress ?? JevDecisionClient.TypeSafeEndpoint, JevDecisionClient.TypeSafeModel, JevDecisionClient.TypeSafeName));
        Registry = new WalletRegistry(Store, Secrets, Activity, Sources.All, Sources.Text,
            Sources.Scanner, engine: Engine, textFactory: Sources.CreateTextSource,
            draftTextFactory: Sources.CreateTextSource, speechFactory: Sources.CreateSpeechSource);
        Registry.ApiKeyEntryRequested += Ui.RequestApiKeyEntryAsync;
        Registry.EndpointConfirmationRequested += Ui.ConfirmEndpointAsync;
        Registry.SecretDeletionRequested += Ui.ConfirmSecretDeletionAsync;
        Pairings = new PairingStore(Path.Combine(home, "pairings.dat"));
        Direct = new DirectTransport(Engine, Pairings, new DirectOptions { DevLocal = DirectLink.DevLocalFromEnvironment });
        Pipe = new PipeServer(Engine, OpenLink);
        AgentPipe = new AgentPipeServer(this, Ui);
        // Dictation and the check of selected text listen to the keyboard system-wide: Windows only for now
        Everywhere = OperatingSystem.IsWindows() ? CreateEverywhere() : null;
        // "Check for updates": the installer's own updater on Windows, the feed of the Linux package on Linux; a copy neither installed only
        // points to the download page.
        Updater = new WalletUpdater(OperatingSystem.IsWindows() ? new VelopackEngine()
            : OperatingSystem.IsLinux() ? new LinuxUpdateEngine()
            : new NoUpdateEngine(), () => Quit());
        Registration = OperatingSystem.IsWindows() && !WalletPaths.IsCustomHome ? new WindowsRegistration(new UserRegistry()) : null;
        LinuxRegistration = OperatingSystem.IsLinux() ? Byoa.Wallet.Desktop.LinuxRegistration.ForThisUser() : null;
    }

    /// <summary>The wallet's check for a newer version, and its button in the settings.</summary>
    public WalletUpdater Updater { get; }

    /// <summary>What an installed wallet registers for the current user (Windows); null on a Mac, on Linux and in a test wallet.</summary>
    public WindowsRegistration? Registration { get; }

    /// <summary>What the wallet registers for the current Linux user (browsers, links, icons, start at login); null elsewhere and in a test wallet.</summary>
    public LinuxRegistration? LinuxRegistration { get; }

    /// <summary>Closes the wallet the way the tray's Quit does; the application sets it once it is up.</summary>
    public Action Quit { get; set; } = () => Environment.Exit(0);

    /// <summary>
    /// Dictation and the check of selected text (the Everywhere page): the native parts, the state machines over them, and the service that listens for
    /// the hotkeys once the visitor has turned a feature on. Nothing is installed or listened to until then.
    /// </summary>
    [SupportedOSPlatform("windows")]
    EverywhereService CreateEverywhere()
    {
        var keyboard = new WinKeyboard();
        var clipboard = new WinClipboard();
        HotkeyProbe = new WinHotkeyProbe();
        Func<EverywhereSettings> settings = () => Store.Read(s => s.Everywhere.Clone());
        Action<Exception> log = e => ErrorLog.Write(WalletPaths.Home, e);
        var inserter = new FallbackInserter(new ClipboardInserter(clipboard, keyboard), new TypingInserter(keyboard));
        var dictation = new DictationController(new WinMicrophone(), Engine.Own, inserter, new EverywhereIndicator(), settings, onError: log);
        var popup = new CheckPopup();
        var check = new SelectionCheckController(new SelectionReader(clipboard, keyboard), Engine.Own, inserter, clipboard, new WinTargetWindow(), popup, settings, log);
        popup.Controller = check;
        return new EverywhereService(Store, new HotkeyMonitor(new WinKeyboardHook()), dictation, check, HotkeyProbe, keyboard);
    }

    /// <summary>The text source the visitor picked as the guard's judge, wrapped; null when the id is not one of his text models (his agents never judge).</summary>
    IDecisionClient? JudgeClient(string id, int maxInputChars)
    {
        if (id.Equals(GuardSettings.KevJudge, StringComparison.OrdinalIgnoreCase))
        {
            // Kev is no text source of the visitor's: a decision model the Llama app serves at its own /v1/systemone. (A test run's BYOA_GUARD_ENDPOINT,
            // a loopback address, stands in for the app's address.)
            var address = Environment.GetEnvironmentVariable("BYOA_GUARD_ENDPOINT") is { Length: > 0 } given
                && Uri.TryCreate(given, UriKind.Absolute, out var uri) && uri.IsLoopback ? new Uri(uri, "/v1/") : null;
            return new LlamaDecisionClient(Sources.TextHttp, maxInputChars, () => Store.Read(s => s.NeverStartModels), address);
        }

        return Sources.FindText(id) is { } judge && judge is not IAgentTextSource ? new ModelDecisionClient(judge, maxInputChars) : null;
    }

    /// <summary>Acts on a bringyourownagent:// link (from the command line or handed over by a second instance).</summary>
    public void OpenLink(string? url, bool userActivation = true) => _ = Task.Run(async () =>
    {
        try
        {
            await Direct.HandleLinkAsync(url, userActivation: userActivation).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Engine.Record(new ActivityEntry { Kind = "link", Via = WalletEngine.ViaLink, Outcome = "failed", Note = e.GetType().Name });
        }
    });

    /// <summary>Acts on a link the visitor pasted into "Connect a site by link" (it did not come through the browser on this computer).</summary>
    public void OpenHandedLink(string? text) => _ = Task.Run(async () =>
    {
        try
        {
            await Direct.HandleLinkAsync(text, handed: true).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Engine.Record(new ActivityEntry { Kind = "link", Via = WalletEngine.ViaLink, Outcome = "failed", Note = e.GetType().Name });
        }
    });

    public SettingsStore Store { get; }
    public SecretStore Secrets { get; }
    public ActivityLog Activity { get; }
    public HttpClient Http { get; }
    public SourceSet Sources { get; }

    /// <summary>What the guard has spent today.</summary>
    public GuardLedger GuardLedger { get; }
    public DesktopUi Ui { get; }
    public WalletEngine Engine { get; }
    public WalletRegistry Registry { get; }
    public PairingStore Pairings { get; }
    public DirectTransport Direct { get; }
    public PipeServer Pipe { get; }
    public AgentPipeServer AgentPipe { get; }

    /// <summary>Dictation and the check of selected text: listens for their hotkeys while the visitor has them on.</summary>
    public EverywhereService? Everywhere { get; }

    /// <summary>Asks Windows whether another program already holds a hotkey (the Everywhere page checks a hotkey before it keeps it).</summary>
    public IHotkeyProbe HotkeyProbe { get; private set; } = null!;

    public void Dispose()
    {
        Everywhere?.Dispose();
        Direct.Dispose();
        Pipe.Dispose();
        AgentPipe.Dispose();
        Sources.Dispose();
        Http.Dispose();
    }
}
