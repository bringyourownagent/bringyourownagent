using Android.Content;
using Byoa.Wallet.Activity;
using Byoa.Wallet.Direct;
using Byoa.Wallet.Engine;
using Byoa.Wallet.Protocol;
using Byoa.Wallet.Providers;
using Byoa.Wallet.Registry;
using Byoa.Wallet.Secrets;
using Byoa.Wallet.Settings;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Android;

/// <summary>
/// Everything the wallet on the phone is made of, created once for the process (the phone's counterpart of the desktop wallet's
/// <c>WalletServices</c>): settings, sealed secrets, activity, sources, the engine, the registry, the stored pairings and the direct transport.
/// Left out because a phone has no use for them: the pipe servers (no browser extension, no agent's MCP bridge), Everywhere (dictation and
/// the check of selected text), the updater and the registrations, and the agent guard (no agent runs on a phone).
/// </summary>
sealed class AndroidServices : IDisposable
{
    public AndroidServices(Context context)
    {
        ThisDevice.Use("this phone");                 // the texts that say where a model runs say "this phone" (a model at 127.0.0.1 is on this device)
        var home = WalletPaths.Home;
        Directory.CreateDirectory(home);
        Store = new SettingsStore(Path.Combine(home, "settings.json"));
        Secrets = new SecretStore(Path.Combine(home, "secrets.dat"));
        Activity = new ActivityLog(home, DateTimeOffset.UtcNow);
        Http = new HttpClient { Timeout = TimeSpan.FromHours(2) };
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("BYOA-agent-wallet/" + WalletPaths.Version + " (Android)");
        Sources = new SourceSet(Store, Secrets, Http, catalogPath: Path.Combine(home, "models-catalog.json"));
        Ui = new AndroidUi(this, context);
        Engine = new WalletEngine(Store, Sources.All, Activity, Ui, textSources: Sources.Text)
        {
            Catalog = Sources.Catalog,
            HasOpenRouterKey = () => Secrets.Has(OpenRouterSource.SecretName),
            PictureShrinker = new Byoa.Wallet.Desktop.SkiaPictureShrinker(),         // the desktop wallet's own class, compiled in (see the project file)
        };
        Registry = new WalletRegistry(Store, Secrets, Activity, Sources.All, Sources.Text,
            Sources.Scanner, engine: Engine, textFactory: Sources.CreateTextSource,
            draftTextFactory: Sources.CreateTextSource, speechFactory: Sources.CreateSpeechSource);
        Pairings = new PairingStore(Path.Combine(home, "pairings.dat"));
        Direct = new DirectTransport(Engine, Pairings, new DirectOptions { DevLocal = DevLocal });
        Serving = new ServingController(context, Direct);
    }

    /// <summary>
    /// A link to a site on this phone itself (<c>ws://localhost</c>) is refused by design: a real site is on the internet, with a certificate. The
    /// end-to-end test of this app runs a site on a computer and reaches it through <c>adb reverse</c>, which makes it "localhost" for the phone,
    /// so a Debug build allows it. A Release build never does, and nothing here answers a consent by itself.
    /// </summary>
    static bool DevLocal =>
#if DEBUG
        true;
#else
        false;
#endif

    public SettingsStore Store { get; }
    public SecretStore Secrets { get; }
    public ActivityLog Activity { get; }
    public HttpClient Http { get; }
    public SourceSet Sources { get; }
    public AndroidUi Ui { get; }
    public WalletEngine Engine { get; }
    public WalletRegistry Registry { get; }
    public PairingStore Pairings { get; }
    public DirectTransport Direct { get; }

    /// <summary>The foreground service while sites are connected.</summary>
    public ServingController Serving { get; }

    /// <summary>Reconnects to the sites that have a stored pairing and keep "decide on my own" (the desktop wallet does it when it starts).</summary>
    public void ResumePairings() => _ = Task.Run(() =>
    {
        try
        {
            Direct.ResumePairings();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            ErrorLog.Write(e);
        }
    });

    /// <summary>Acts on a <c>bringyourownagent://</c> link that Android handed to the app (a tap in the browser): it counts as the visitor's own click.</summary>
    public void OpenLink(string? url) => _ = Task.Run(async () =>
    {
        var scope = new ServingController.Connect();
        ServingController.Link.Value = scope;              // the consent, answered Allow, says in this scope which site is being connected
        try
        {
            Direct.ResumePairings();               // a link starts serving too: the sites with a stored pairing come back
            var connection = await Direct.HandleLinkAsync(url, scope.Token, userActivation: true).ConfigureAwait(false);
            if (connection is not null && scope.Stopped) connection.Stop("stopped from the notification");      // Stop came as the connection was made
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Engine.Record(new ActivityEntry { Kind = "link", Via = WalletEngine.ViaLink, Outcome = "failed", Note = e.GetType().Name });
        }
        finally
        {
            Serving.ConnectingDone(scope);
        }
    });

    /// <summary>Acts on a link the visitor pasted into "Connect by link" (it did not come through the browser on this phone).</summary>
    public void OpenHandedLink(string? text) => _ = Task.Run(async () =>
    {
        var scope = new ServingController.Connect();
        ServingController.Link.Value = scope;
        try
        {
            Direct.ResumePairings();               // a link starts serving too: the sites with a stored pairing come back
            var connection = await Direct.HandleLinkAsync(text, scope.Token, handed: true).ConfigureAwait(false);
            if (connection is not null && scope.Stopped) connection.Stop("stopped from the notification");
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Engine.Record(new ActivityEntry { Kind = "link", Via = WalletEngine.ViaLink, Outcome = "failed", Note = e.GetType().Name });
        }
        finally
        {
            Serving.ConnectingDone(scope);
        }
    });

    /// <summary>
    /// A link that came from another device's screen (a QR code, the web wallet's address): the same checks as for a pasted link are made first, and what is
    /// wrong with it is said in one plain line on the home screen (no site is contacted, nothing is recorded as a visit); a good link goes on as a handed link.
    /// </summary>
    public void OpenHandedOrSay(string? link)
    {
        if (Direct.CheckAndRecordHandedLink(link) is { } problem) Ui.NoteLine(problem + " Nothing was connected.");
        else OpenHandedLink(link);
    }

    /// <summary>"Scan a QR code": Google's scanner opens over the app; what it reads is looked at by <see cref="WalletAddress"/>.</summary>
    public void ScanCode() => AppState.Activity?.RunOnUiThread(() =>
    {
        if (AppState.Activity is { } activity) QrScanner.Scan(activity, this);
    });

    public void Dispose()
    {
        Serving.Dispose();
        Direct.Dispose();
        Sources.Dispose();
        Http.Dispose();
    }
}
