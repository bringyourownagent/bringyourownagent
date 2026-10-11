using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Byoa.Wallet.Desktop;

public partial class App : Application
{
    WalletServices? _services;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && OperatingSystem.IsWindows())
        {
            var services = new WalletServices();
            _services = services;
            services.Ui.CreateTray(this, () => desktop.Shutdown());
            services.Pipe.Start();
            services.Engine.WarmAllowedSites();
            if (!Program.Background) services.Ui.OpenSettings("sites");
            desktop.Exit += (_, _) => services.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
