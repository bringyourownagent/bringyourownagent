using System.Runtime.Versioning;
using Byoa.Wallet.Activity;
using Byoa.Wallet.Direct;
using Byoa.Wallet.Engine;
using Byoa.Wallet.Guard;
using Byoa.Wallet.Protocol;
using Byoa.Wallet.Providers;
using Byoa.Wallet.Registry;
using Byoa.Wallet.Secrets;
using Byoa.Wallet.Settings;

namespace Byoa.Wallet.Desktop;

/// <summary>Everything the running wallet is made of, created once at start.</summary>
[SupportedOSPlatform("windows")]
sealed class WalletServices : IDisposable
{
    public WalletServices()
    {
        var home = WalletPaths.Home;
        Directory.CreateDirectory(home);
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
        // The guard: Jev, behind the visitor's OpenRouter key, looks at every request a site makes to his agent. What it spends is counted in a file of its own.
        GuardLedger = new GuardLedger(Path.Combine(home, "guard-ledger.json"));
        // (BYOA_GUARD_ENDPOINT points a test run at a fake decision server; only an address on this computer is taken, so that the key can go nowhere else)
        var guardAddress = Environment.GetEnvironmentVariable("BYOA_GUARD_ENDPOINT") is { Length: > 0 } given
            && Uri.TryCreate(given, UriKind.Absolute, out var uri) && uri.IsLoopback ? uri : null;
        Engine.Guard = new GuardService(Store, GuardLedger, new JevDecisionClient(Sources.TextHttp, () => Secrets.Get(OpenRouterSource.SecretName), guardAddress));
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

    public void Dispose()
    {
        Direct.Dispose();
        Pipe.Dispose();
        AgentPipe.Dispose();
        Sources.Dispose();
        Http.Dispose();
    }
}
