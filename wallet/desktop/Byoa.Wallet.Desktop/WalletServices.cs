using System.Runtime.Versioning;
using Byoa.Wallet.Activity;
using Byoa.Wallet.Engine;
using Byoa.Wallet.Protocol;
using Byoa.Wallet.Providers;
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
        Engine = new WalletEngine(Store, Sources.All, Activity, Ui);
        Pipe = new PipeServer(Engine);
    }

    public SettingsStore Store { get; }
    public SecretStore Secrets { get; }
    public ActivityLog Activity { get; }
    public HttpClient Http { get; }
    public SourceSet Sources { get; }
    public DesktopUi Ui { get; }
    public WalletEngine Engine { get; }
    public PipeServer Pipe { get; }

    public void Dispose()
    {
        Pipe.Dispose();
        Sources.Dispose();
        Http.Dispose();
    }
}
