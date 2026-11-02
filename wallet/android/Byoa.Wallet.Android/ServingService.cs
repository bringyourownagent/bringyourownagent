using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace Byoa.Wallet.Android;

/// <summary>
/// The foreground service that keeps the wallet's process alive while at least one site is connected (a live connection of the direct
/// transport): without it Android may stop the app a few minutes after it leaves the screen, and the site would lose the visitor's models
/// in the middle of a game. It does nothing itself; its notification says how many sites are served and has a "Stop" that ends them.
/// <see cref="ServingController"/> starts it with the first connection and stops it after the last.
/// </summary>
[Service(Name = "org.bringyourownagent.wallet.ServingService", Exported = false, ForegroundServiceType = ForegroundService.TypeSpecialUse)]
[Property("android.app.PROPERTY_SPECIAL_USE_FGS_SUBTYPE",
    Value = "Keeps the connections that the user allowed, from sites to the user's own AI models, open while the app is in the background; the notification names the sites served and stops them.")]
public sealed class ServingService : Service
{
    public const string ActionStop = "org.bringyourownagent.wallet.STOP_SERVING";

    /// <summary>The service is alive (set by the service itself, so that nothing has to remember what was once asked of it).</summary>
    public static volatile bool Running;

    /// <summary>The service has called startForeground: from then on it may be stopped from outside (before, a stop is a crash of the app).</summary>
    public static volatile bool Foreground;

    public override IBinder? OnBind(Intent? intent) => null;

    public override void OnCreate()
    {
        base.OnCreate();
        Running = true;
    }

    public override void OnDestroy()
    {
        var wasForeground = Foreground;
        Running = false;
        Foreground = false;
        base.OnDestroy();
        // a service that ended while a site still needs it is started again (a few times, when it fails before it has called startForeground)
        try
        {
            WalletHost.Services.Serving.ServiceGone(wasForeground);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            ErrorLog.Write(e);
        }
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var serving = WalletHost.Services.Serving;
        if (intent?.Action == ActionStop)
        {
            // the connections end and the foreground service ends with them; a Stop that finds no service running must not leave one behind
            serving.StopAll();
            StopSelf();
            return StartCommandResult.NotSticky;
        }

        try
        {
            // Android gives a started foreground service a few seconds to say what it is: the notification goes up first, whatever else is true.
            var notification = Notifier.Serving(this, serving.Sites, serving.IsConnecting ? serving.ConnectingHostNow : null);
            // from Android 14 a foreground service must say what kind it is (its manifest entry says "specialUse" and explains it); before, it did not
            if (OperatingSystem.IsAndroidVersionAtLeast(34)) StartForeground(Notifier.ServingId, notification, ForegroundService.TypeSpecialUse);
            else StartForeground(Notifier.ServingId, notification);
            Foreground = true;
            serving.ServiceStarted();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            ErrorLog.Write(e);
            StopSelf();
            return StartCommandResult.NotSticky;
        }

        // the last connection may have ended before the service got going
        if (!serving.Needed) StopSelf();
        return StartCommandResult.NotSticky;
    }
}
