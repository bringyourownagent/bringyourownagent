using System.Runtime.Versioning;
using Byoa.Wallet.Activity;
using Byoa.Wallet.Direct;
using Byoa.Wallet.Engine;
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
        Sources = new SourceSet(Store, Secrets, Http);
        Ui = new DesktopUi(this);
        Engine = new WalletEngine(Store, Sources.All, Activity, Ui, textSources: Sources.Text, preferGpu: WhisperSource.ProcessUsesGpu);
        var modelManager = new WalletModelManager(Store, Sources.Whisper, Sources.WhisperCpu, Sources.GigaAm);
        Registry = new WalletRegistry(Store, Secrets, Activity, Sources.All, Sources.Text,
            Sources.Scanner, modelManager, engine: Engine, textFactory: Sources.CreateTextSource,
            draftTextFactory: Sources.CreateTextSource);
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
