using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Avalonia.Android;

[assembly: UsesPermission("android.permission.INTERNET")]
[assembly: UsesPermission("android.permission.FOREGROUND_SERVICE")]
[assembly: UsesPermission("android.permission.FOREGROUND_SERVICE_SPECIAL_USE")]
[assembly: UsesPermission("android.permission.POST_NOTIFICATIONS")]

namespace Byoa.Wallet.Android;

/// <summary>
/// The wallet's one activity. A <c>bringyourownagent://</c> link (a tap in the browser; Chrome asks "Open with Agent wallet?") starts it or, when it
/// is running, reaches <see cref="OnNewIntent"/>; either way the link goes to the direct transport, which asks the visitor on the consent page.
/// The wallet's services belong to the process, not to this activity: it can be destroyed (Back) and made again, or rotated, and what the engine
/// is waiting for is still there, shown again by the <see cref="Shell"/>.
/// </summary>
[Activity(Name = "org.bringyourownagent.wallet.MainActivity", Label = "Agent wallet", Icon = "@mipmap/ic_launcher", Theme = "@style/Theme.AppCompat.Light.NoActionBar",
    MainLauncher = true, Exported = true, LaunchMode = LaunchMode.SingleTask,
    ScreenOrientation = ScreenOrientation.Portrait, WindowSoftInputMode = SoftInput.AdjustResize,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.KeyboardHidden)]
[IntentFilter([Intent.ActionView], Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable], DataScheme = "bringyourownagent")]
// the web wallet's address, which a site's QR code carries (a phone camera does not open a custom scheme): https://wallet.bringyourownagent.org/#link=<the link>.
// AutoVerify: Android checks https://wallet.bringyourownagent.org/.well-known/assetlinks.json for this package's certificate, and then opens the address
// here without asking; where the check has not passed it asks, or opens the web wallet in the browser. The path is "/" and nothing else.
[IntentFilter([Intent.ActionView], AutoVerify = true, Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable], DataScheme = "https", DataHost = WalletAddress.WebWalletHost, DataPath = "/")]
public class MainActivity : AvaloniaMainActivity<App>
{
    const string LinkHandled = "org.bringyourownagent.wallet.link-handled";

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        AppState.Activity = this;
        base.OnCreate(savedInstanceState);
        // The wallet starts serving when the app is opened (not when the same activity is made again by a rotation): the sites it has a stored
        // pairing with, and that keep "decide on my own", come back. After "Stop" in the notification this is what brings them back.
        if (savedInstanceState is null) WalletHost.Services.ResumePairings();
        HandleIntent(Intent, fromCreate: true);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        Intent = intent;
        HandleIntent(intent, fromCreate: false);
    }

    /// <summary>
    /// Tests only: an activity that is destroyed and made again is what a rotation or a long stay in the background does, and the wallet must show the
    /// same thing after it. A Debug build can be told to do it from adb; a Release build has no such door. It answers nothing and decides nothing.
    /// </summary>
    public const string ActionDebugRecreate = "org.bringyourownagent.wallet.DEBUG_RECREATE";

    /// <summary>
    /// Tests only: puts the text of the intent on the clipboard, which is where a visitor has a link he copied from a message or a page. The phone's keyboard
    /// loses keys when a test types a long link, and a test must not type a link into the wallet by any way a person would not; the test then presses
    /// "Paste from the clipboard" like a person. A Debug build only; it answers nothing and decides nothing.
    /// </summary>
    public const string ActionDebugCopy = "org.bringyourownagent.wallet.DEBUG_COPY";

    /// <summary>A link goes to the wallet once: the intent keeps its data for as long as the activity lives, and a second look must not act on it again.</summary>
    void HandleIntent(Intent? intent, bool fromCreate)
    {
#if DEBUG
        if (intent?.Action == ActionDebugRecreate)
        {
            intent.SetAction(null);
            Recreate();
            return;
        }

        if (intent?.Action == ActionDebugCopy)
        {
            var text = intent.GetStringExtra("text");
            intent.SetAction(null);
            if (text is not null) ((ClipboardManager)GetSystemService(ClipboardService)!).PrimaryClip = ClipData.NewPlainText("link", text);
            return;
        }
#endif
        // the visitor came from the notification of a question: if nothing is waiting, the question was lost with the process that held it
        if (intent?.Action == Notifier.ActionOpenPrompt && !intent.GetBooleanExtra(LinkHandled, false))
        {
            intent.PutExtra(LinkHandled, true);
            if (WalletHost.Services.Ui.Waiting == 0) WalletHost.Services.Ui.NoteStale();
            return;
        }

        if (intent is null || intent.Action != Intent.ActionView || intent.GetBooleanExtra(LinkHandled, false)) return;
        var link = intent.DataString;
        if (link is not null && link.StartsWith("https:", StringComparison.OrdinalIgnoreCase))
        {
            HandleAddress(link, fromCreate, intent);
            return;
        }

        if (link is null || !link.StartsWith("bringyourownagent:", StringComparison.OrdinalIgnoreCase)) return;
        intent.PutExtra(LinkHandled, true);
        // After the process died, Android makes the activity again with the intent it was first started with: the link that was tapped before. That link
        // was acted on by the process that died, and what it asked is gone with it (the site has stopped waiting): it is not acted on a second time.
        if (fromCreate && SeenBefore(link)) return;
        Remember(link);
        WalletHost.Services.OpenLink(link);
    }

    /// <summary>
    /// The web wallet's address, opened by the camera, a browser or a messenger: the link in its fragment goes to the wallet as a handed link (it came from
    /// another device's screen, so the consent says so and shows the check code); an address with no usable link opens the home screen with one plain line.
    /// No site is contacted either way: the consent comes from the link alone.
    /// </summary>
    void HandleAddress(string address, bool fromCreate, Intent intent)
    {
        intent.PutExtra(LinkHandled, true);
        if (fromCreate && SeenBefore(address)) return;
        Remember(address);
        if (WalletAddress.TryLinkFromAddress(address, out var link)) WalletHost.Services.OpenHandedOrSay(link);
        else WalletHost.Services.Ui.NoteLine(WalletAddress.NoLinkLine);
    }

    // ------------------------------------------------------------------------------------------------
    // the links that were acted on, kept for half an hour (a hash of each, nothing of what it says) in the app's own preferences

    static readonly TimeSpan LinkMemory = TimeSpan.FromMinutes(30);

    static string LinkKey(string link) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(link)))[..16];

    List<string> RememberedLinks()
    {
        var prefs = GetSharedPreferences("wallet", FileCreationMode.Private)!;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return (prefs.GetStringSet("handled-links", null) ?? new List<string>())
            .Where(entry => entry.Split('|') is [_, var at] && long.TryParse(at, out var t) && now - t < (long)LinkMemory.TotalSeconds).ToList();
    }

    bool SeenBefore(string link) => RememberedLinks().Any(entry => entry.StartsWith(LinkKey(link) + "|", StringComparison.Ordinal));

    void Remember(string link)
    {
        var entries = RememberedLinks();
        entries.Add(LinkKey(link) + "|" + DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
        GetSharedPreferences("wallet", FileCreationMode.Private)!.Edit()!.PutStringSet("handled-links", entries.TakeLast(16).ToList())!.Apply();
    }

    protected override void OnResume()
    {
        base.OnResume();
        AppState.Visible = true;
        AppState.Activity = this;
        Notifier.CancelPrompt(this);
        // a question that came while the app was away is the first moment to ask whether the wallet may show notifications
        if (WalletHost.Services.Ui.Waiting > 0) AskForNotificationsOnce();
        // the service that could not be started from the background can be now
        WalletHost.Services.Serving.Refresh();
    }

    protected override void OnPause()
    {
        AppState.Visible = false;
        // a question that is still waiting must not wait unseen
        WalletHost.Services.Ui.TellAboutWaiting();
        base.OnPause();
    }

    protected override void OnDestroy()
    {
        if (ReferenceEquals(AppState.Activity, this)) AppState.Activity = null;
        base.OnDestroy();
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        // the serving notification was posted before the visitor answered: Android kept it back, so it goes up again now
        if (grantResults.Length > 0 && grantResults[0] == Permission.Granted) WalletHost.Services.Serving.Refresh();
    }

    /// <summary>
    /// Android 13 and later let the visitor decide whether an app shows notifications. The wallet asks once, at the first moment it needs one: when
    /// the first question is put to him (so that a question he leaves unanswered can be announced); without it the wallet still serves, only the
    /// notifications are not shown.
    /// </summary>
    public void AskForNotificationsOnce()
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.Tiramisu || Notifier.Allowed(this)) return;
        var prefs = GetSharedPreferences("wallet", FileCreationMode.Private)!;
        if (prefs.GetBoolean("asked-notifications", false)) return;
        prefs.Edit()!.PutBoolean("asked-notifications", true)!.Apply();
        RequestPermissions([Notifier.PostNotifications], 1);
    }
}
