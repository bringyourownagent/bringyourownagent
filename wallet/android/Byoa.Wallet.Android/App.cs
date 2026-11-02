using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Byoa.Wallet.Android;

/// <summary>
/// The Avalonia application: its styles are in App.axaml. The one view is the <see cref="Shell"/>, made once for the process over the wallet's
/// services; an activity that is made again shows the same shell.
/// </summary>
public partial class App : Avalonia.Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is ISingleViewApplicationLifetime single) single.MainView = new Shell(WalletHost.Services);
        base.OnFrameworkInitializationCompleted();
    }
}
