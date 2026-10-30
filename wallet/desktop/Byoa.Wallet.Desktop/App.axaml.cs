using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Byoa.Wallet.Protocol;

namespace Byoa.Wallet.Desktop;

public partial class App : Application
{
    WalletServices? _services;
    readonly List<PosixSignalRegistration> _signals = [];

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            && (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsLinux()))
        {
            var services = new WalletServices();
            _services = services;
            services.Quit = () => Avalonia.Threading.Dispatcher.UIThread.Post(() => desktop.Shutdown());
            services.Ui.CreateTray(this, () => desktop.Shutdown());
            // An installed wallet puts its registration right at every start: a key that was lost, or that points at an older copy.
            if (services.Registration is { } registration && services.Updater.CanUpdateItself)
                _ = Task.Run(() => { try { registration.Apply(AppContext.BaseDirectory); } catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { /* the keys stay as they were */ } });
            services.Pipe.Start();
            services.AgentPipe.Start();
            services.Everywhere?.Start();
            services.Engine.WarmAllowedSites();
            services.Direct.ResumePairings();
            if (OperatingSystem.IsMacOS())
            {
                ListenForMacActivation(services);
                _ = Task.Run(MacBundle.RegisterBrowsers);
                ListenForStop(desktop);
            }
            else if (OperatingSystem.IsLinux())
            {
                // The wallet tells the desktop where its host, its links and its icon are at every start; a failure is logged and never stops it.
                if (services.LinuxRegistration is { } linux)
                {
                    _ = Task.Run(() =>
                    {
                        try { linux.Apply(AppContext.BaseDirectory); }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException
                            or InvalidDataException or FormatException or System.Text.Json.JsonException)
                        {
                            ErrorLog.Write(WalletPaths.Home, e);
                        }
                    });
                }

                // A stop from outside (a logout, `kill`) ends the wallet the way Quit does, so that its sockets are taken away.
                ListenForStop(desktop);
            }

            if (Program.LinkArgument is not null) services.OpenLink(Program.LinkArgument);
            if (ShotsMode.Requested) _ = ShotsMode.RunAsync(services, desktop);
            else if (!Program.Background) services.Ui.OpenSettings("sites");
            desktop.Exit += (_, _) => services.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// A stop from outside (a logout, <c>kill</c>, a restart of the system) ends the wallet the way the tray's Quit does, so that its sockets
    /// are taken away and a second start does not meet a stale one.
    /// </summary>
    void ListenForStop(IClassicDesktopStyleApplicationLifetime desktop)
    {
        foreach (var signal in new[] { PosixSignal.SIGTERM, PosixSignal.SIGINT, PosixSignal.SIGHUP })
            _signals.Add(PosixSignalRegistration.Create(signal, context =>
            {
                context.Cancel = true;
                Avalonia.Threading.Dispatcher.UIThread.Post(() => desktop.Shutdown());
            }));
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
