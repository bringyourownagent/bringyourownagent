using Android.App;
using Android.Content;
using Android.Runtime;

namespace Byoa.Wallet.Android;

/// <summary>
/// The application object of the process: it gives the core its Keystore protector before anything opens a secret file, and holds the
/// wallet's services for the whole life of the process, whatever happens to the activity (it is destroyed by Back, rotated, or sent to the
/// background and made again). One instance per process.
/// </summary>
[Application(Label = "Agent wallet", Icon = "@mipmap/ic_launcher", AllowBackup = false)]
public sealed class WalletApplication(IntPtr handle, JniHandleOwnership transfer) : Application(handle, transfer)
{
    public override void OnCreate()
    {
        base.OnCreate();
        AndroidKeystoreProtector.Install();
        WalletHost.Context = this;
        // a question lives only in the process that holds it: the notification of one that died with an earlier process is taken down (a visitor who
        // taps it before this runs finds the home screen, which tells him that the question is gone)
        Notifier.CancelPrompt(this);
        DebugDoors.StandInForTests(this);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception exception) ErrorLog.Write(exception);
        };
        TaskScheduler.UnobservedTaskException += (_, e) => ErrorLog.Write(e.Exception);
    }
}

/// <summary>
/// Tests only. In a Debug build, when the app's own folder holds a file <c>debug-openrouter.txt</c> with an <c>http://127.0.0.1:port</c> address,
/// OpenRouter's models and its public list are read from that address instead of openrouter.ai. It is how the end-to-end test lets the wallet spend
/// "money" at a fake paid model on the test computer (through <c>adb reverse</c>), without a real key and without a real bill. The core takes nothing
/// but an <c>http</c> address on the device itself, and only for a wallet with a data folder of its own, which the second variable declares (the folder
/// is the usual one). It answers no question of the wallet and decides nothing. A Release build has no such code: the method is empty there.
/// </summary>
static class DebugDoors
{
    public static void StandInForTests(Context context)
    {
#if DEBUG
        var file = Path.Combine(context.FilesDir!.AbsolutePath, "debug-openrouter.txt");
        if (!File.Exists(file)) return;
        Environment.SetEnvironmentVariable(Providers.SourceSet.StandInVariable, File.ReadAllText(file).Trim());
        Environment.SetEnvironmentVariable(Protocol.WalletPaths.HomeVariable, Protocol.WalletPaths.Home);
#endif
    }
}

/// <summary>The one place that makes the wallet's services, once, the first time anyone asks (the activity, the service, a notification).</summary>
static class WalletHost
{
    static readonly object Gate = new();
    static AndroidServices? _services;

    public static Context Context { get; set; } = null!;

    public static AndroidServices Services
    {
        get
        {
            lock (Gate) return _services ??= new AndroidServices(Context);
        }
    }
}

/// <summary>What the app knows about its own screen: whether the visitor can see it now (a question that comes while he cannot gets a notification).</summary>
static class AppState
{
    /// <summary>The activity is on the screen and in front.</summary>
    public static volatile bool Visible;

    /// <summary>The activity that is alive now, if any (it asks for the notification permission and brings the app to the front).</summary>
    public static MainActivity? Activity { get; set; }
}
