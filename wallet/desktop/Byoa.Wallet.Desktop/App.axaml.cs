using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Byoa.Wallet.Desktop;

public partial class App : Application
{
    WalletServices? _services;
    readonly List<PosixSignalRegistration> _signals = [];

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()))
        {
            var services = new WalletServices();
            _services = services;
            services.Ui.CreateTray(this, () => desktop.Shutdown());
            services.Pipe.Start();
            services.AgentPipe.Start();
            services.Everywhere?.Start();
            services.Engine.WarmAllowedSites();
            services.Direct.ResumePairings();
            if (OperatingSystem.IsMacOS())
            {
                ListenForMacActivation(services);
                _ = Task.Run(MacBundle.RegisterBrowsers);
                // A stop from outside (a logout, `kill`, a restart of the Mac) ends the wallet the way the tray's Quit does, so that its sockets are taken away
                foreach (var signal in new[] { PosixSignal.SIGTERM, PosixSignal.SIGINT, PosixSignal.SIGHUP })
                    _signals.Add(PosixSignalRegistration.Create(signal, context =>
                    {
                        context.Cancel = true;
                        Avalonia.Threading.Dispatcher.UIThread.Post(() => desktop.Shutdown());
                    }));
            }

            if (Program.LinkArgument is not null) services.OpenLink(Program.LinkArgument);
            if (ShotsMode.Requested) _ = ShotsMode.RunAsync(services, desktop);
            else if (!Program.Background) services.Ui.OpenSettings("sites");
            desktop.Exit += (_, _) => services.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// macOS gives a <c>bringyourownagent://</c> link to the running program (or starts it with the link) as an event, not as an argument;
    /// a click on the program's icon again is a "reopen". A link comes from a click in the browser, so it counts as user activation.
    /// </summary>
    void ListenForMacActivation(WalletServices services)
    {
        if (TryGetFeature(typeof(IActivatableLifetime)) is not IActivatableLifetime lifetime) return;
        lifetime.Activated += (_, e) =>
        {
            if (e is ProtocolActivatedEventArgs { Kind: ActivationKind.OpenUri } protocol) services.OpenLink(protocol.Uri.ToString());
            else if (e.Kind == ActivationKind.Reopen) services.Ui.OpenSettings("sites");
        };
    }
}
