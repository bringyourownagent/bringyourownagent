using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace Byoa.Wallet.Android;

/// <summary>The wallet's notifications: the quiet one that stays while sites are connected, and the loud one for a question that came while the app was not on screen.</summary>
static class Notifier
{
    public const string ServingChannel = "serving";
#if DEBUG
    // A Debug build keeps its silent questions in a channel of its own: a channel is state that stays on the phone, and a Release build installed over a
    // Debug one must not inherit its silence.
    public const string PromptChannel = "prompts-debug";
#else
    public const string PromptChannel = "prompts";
#endif
    public const int ServingId = 1;
    public const int PromptId = 2;

    /// <summary>"Open the wallet so that it can serve": Android refused to start the service while the app was in the background.</summary>
    public const int OpenToServeId = 3;

    /// <summary>A notification that opens the app at the question waiting in it.</summary>
    public const string ActionOpenPrompt = "org.bringyourownagent.wallet.OPEN_PROMPT";

    /// <summary>The permission to show notifications (Android 13 and later).</summary>
    public const string PostNotifications = "android.permission.POST_NOTIFICATIONS";

    public static void EnsureChannels(Context context)
    {
        var manager = (NotificationManager)context.GetSystemService(Context.NotificationService)!;
        if (manager.GetNotificationChannel(ServingChannel) is null)
        {
            var serving = new NotificationChannel(ServingChannel, "Serving sites", NotificationImportance.Low)
            {
                Description = "Shown while sites are connected to your wallet and use your models.",
            };
            serving.SetShowBadge(false);
            manager.CreateNotificationChannel(serving);
        }

        if (manager.GetNotificationChannel(PromptChannel) is null)
        {
            var prompts = new NotificationChannel(PromptChannel, "Questions from sites", NotificationImportance.High)
            {
                Description = "A site wants to use your AI, or a site's call needs your answer.",
            };
#if DEBUG
            // A Debug build is the one the tests run on a phone that somebody may be asleep beside: its question still comes up as a banner on
            // the screen (high importance), but it makes no sound, no vibration and no light. A Release build keeps the system's own sound.
            prompts.SetSound(null, null);
            prompts.EnableVibration(false);
            prompts.EnableLights(false);
#endif
            manager.CreateNotificationChannel(prompts);
        }
    }

    /// <summary>The permission to show notifications is a visitor's choice since Android 13; without it the foreground service still runs.</summary>
    public static bool Allowed(Context context) =>
        Build.VERSION.SdkInt < BuildVersionCodes.Tiramisu
        || context.CheckSelfPermission(PostNotifications) == Permission.Granted;

    static PendingIntent OpenApp(Context context, string? action = null)
    {
        var intent = new Intent(context, typeof(MainActivity)).AddFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop | ActivityFlags.ClearTop);
        if (action is not null) intent.SetAction(action);
        return PendingIntent.GetActivity(context, action is null ? 0 : 1, intent, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;
    }

    /// <summary>"Agent wallet is serving 2 sites" (or "Connecting to host…"): tap opens the app, "Stop" ends the connections.</summary>
    public static Notification Serving(Context context, int sites, string? connectingHost = null)
    {
        EnsureChannels(context);
        var stop = new Intent(context, typeof(ServingService)).SetAction(ServingService.ActionStop);
        var stopIntent = PendingIntent.GetService(context, 2, stop, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;
        return new Notification.Builder(context, ServingChannel)
            .SetSmallIcon(Resource.Drawable.ic_stat_wallet)!
            .SetContentTitle(ServingPlan.Title(sites, connectingHost))!
            .SetContentText(ServingPlan.Line(sites))!
            .SetContentIntent(OpenApp(context))!
            .SetOngoing(true)!
            .SetOnlyAlertOnce(true)!
            .SetShowWhen(false)!
            .SetCategory(Notification.CategoryService)!
            .AddAction(new Notification.Action.Builder(null, "Stop", stopIntent).Build())!
            .Build()!;
    }

    /// <summary>A question that cannot wait for the visitor to open the app: the notification opens it at the question.</summary>
    public static void PostPrompt(Context context, string title, string text)
    {
        if (!Allowed(context)) return;
        EnsureChannels(context);
        // On a locked phone the notification is private: it says only that a site wants something, not which site (that is in the title below,
        // which the phone shows after it is unlocked).
        var publicVersion = new Notification.Builder(context, PromptChannel)
            .SetSmallIcon(Resource.Drawable.ic_stat_wallet)!
            .SetContentTitle("Agent wallet")!
            .SetContentText("A site wants to use your AI")!
            .Build()!;
        var notification = new Notification.Builder(context, PromptChannel)
            .SetSmallIcon(Resource.Drawable.ic_stat_wallet)!
            .SetContentTitle(title)!
            .SetContentText(text)!
            .SetContentIntent(OpenApp(context, ActionOpenPrompt))!
            .SetAutoCancel(true)!
            .SetCategory(Notification.CategoryMessage)!
            .SetVisibility(NotificationVisibility.Private)!
            .SetPublicVersion(publicVersion)!
            .Build()!;
        var manager = (NotificationManager)context.GetSystemService(Context.NotificationService)!;
        manager.Notify(PromptId, notification);
    }

    /// <summary>A site is waiting and Android did not let the app start its service from the background: the visitor is asked to open the app, which can.</summary>
    public static void PostOpenToServe(Context context)
    {
        if (!Allowed(context)) return;
        EnsureChannels(context);
        var notification = new Notification.Builder(context, PromptChannel)
            .SetSmallIcon(Resource.Drawable.ic_stat_wallet)!
            .SetContentTitle("Open the wallet to keep serving")!
            .SetContentText("A site you allowed is waiting. Open the wallet, so that Android lets it keep running.")!
            .SetContentIntent(OpenApp(context))!
            .SetAutoCancel(true)!
            .SetVisibility(NotificationVisibility.Private)!
            .Build()!;
        ((NotificationManager)context.GetSystemService(Context.NotificationService)!).Notify(OpenToServeId, notification);
    }

    public static void CancelOpenToServe(Context context) =>
        ((NotificationManager)context.GetSystemService(Context.NotificationService)!).Cancel(OpenToServeId);

    public static void CancelPrompt(Context context)
    {
        var manager = (NotificationManager)context.GetSystemService(Context.NotificationService)!;
        manager.Cancel(PromptId);
    }
}
