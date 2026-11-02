using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Byoa.Wallet.Activity;
using Byoa.Wallet.Direct;
using Byoa.Wallet.Engine;
using Byoa.Wallet.Protocol;
using Byoa.Wallet.Secrets;

namespace Byoa.Wallet.Android;

/// <summary>
/// The home screen: what is connected now, the last few lines of activity, and the doors (Connect by link, My models, Sites, Activity).
/// A site reaches the wallet by a link that Android hands to the app; this screen is where the visitor looks to see that it is serving.
/// </summary>
sealed class HomePage : Page
{
    readonly StackPanel _body = new() { Spacing = 14, Margin = new Thickness(16, 16, 16, 24) };

    public HomePage(Shell shell) : base(shell)
    {
    }

    public override string? Title => null;

    protected override Control Build()
    {
        Fill();
        return new ScrollViewer
        {
            Content = _body,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };
    }

    public override void Refresh()
    {
        if (Built) Fill();
    }

    void Fill()
    {
        _body.Children.Clear();
        _body.Children.Add(Kit.Column(2,
            Kit.Text("Agent wallet", "h1"),
            Kit.Text("Version " + WalletPaths.Version + ". Your own AI, for the sites you allow.", "muted", "small")));

        if (W.Ui.StaleNote is { } stale)
        {
            var note = Kit.Text(stale, "strong", "small");
            AutomationProperties.SetAutomationId(note, "HomeStale");
            _body.Children.Add(Kit.Box("field", note));
        }

        // what is connected now
        var origins = W.Serving.Origins;
        var connectingHost = W.Serving.ConnectingHostNow;          // a site that was allowed, whose models are still getting ready: it has no connection yet
        var connected = Kit.Column(8);
        if (origins.Count == 0 && connectingHost is null)
        {
            var none = Kit.Text("No site is connected.", "strong");
            AutomationProperties.SetAutomationId(none, "HomeNoSites");
            connected.Children.Add(none);
            connected.Children.Add(Kit.Text("A site shows a Connect button, a link or a QR code. Open the link on this phone, or scan the code, and the wallet asks you here; or paste the link under Connect by link.", "muted"));
        }
        else
        {
            if (origins.Count > 0)
            {
                var heading = Kit.Text(origins.Count == 1 ? "Serving 1 site" : "Serving " + origins.Count + " sites", "strong");
                AutomationProperties.SetAutomationId(heading, "HomeServing");
                connected.Children.Add(heading);
            }

            if (connectingHost is not null)
            {
                var connecting = Kit.Text("Connecting to " + connectingHost + "…", "strong");
                AutomationProperties.SetAutomationId(connecting, "HomeConnecting");
                connected.Children.Add(connecting);
                connected.Children.Add(Kit.Text("The site's models are getting ready; the connection comes when they are.", "muted", "small"));
            }

            foreach (var origin in origins)
            {
                var state = ServingPlan.BestState(W.Direct.Connections.Where(c => c.Origin == origin).Select(c => c.State));
                var line = Kit.Columns("Auto,*", Kit.Tile(Kit.Globe, 32), Kit.Column(0, Kit.Text(Kit.HostOf(origin), "hostmono"), Kit.Text(StateWords(state), "muted", "small")));
                ((Control)line.Children[1]).Margin = new Thickness(10, 0, 0, 0);
                AutomationProperties.SetName(line, "Connected: " + Kit.HostOf(origin));
                connected.Children.Add(line);
            }

            connected.Children.Add(Kit.Button("Stop serving", "deny", "HomeStop", () => W.Serving.StopAll(), "small"));
        }

        _body.Children.Add(Kit.Box("card", connected));

        // the doors
        var connect = Kit.Button("Connect by link", "allow", "HomeConnect", () => Shell.Push(new ConnectPage(Shell)));
        var scan = Kit.Button("Scan a QR code", "deny", "HomeScan", () => W.ScanCode());
        var models = Kit.Button("My models", "deny", "HomeModels", () => Shell.Push(new ModelsPage(Shell)));
        var sites = Kit.Button("Sites", "deny", "HomeSites", () => Shell.Push(new SitesPage(Shell)));
        var activity = Kit.Button("Activity", "deny", "HomeActivity", () => Shell.Push(new ActivityPage(Shell)));
        var limits = Kit.Button("Limits", "deny", "HomeLimits", () => Shell.Push(new LimitsPage(Shell)));
        var help = Kit.Button("Help", "deny", "HomeHelp", () => Shell.Push(new HelpPage(Shell)));
        foreach (var b in new[] { connect, scan, models, sites, activity, limits, help }) b.HorizontalAlignment = HorizontalAlignment.Stretch;
        _body.Children.Add(Kit.Column(10, connect, scan, models, sites, activity, limits, help));

        // the last lines of activity
        var recent = W.Activity.Recent(5);
        var lines = Kit.Column(8, Kit.Text("Recently", "label"));
        if (recent.Count == 0) lines.Children.Add(Kit.Text("Nothing yet.", "muted"));
        foreach (var entry in recent) lines.Children.Add(ActivityPage.Line(entry));
        AutomationProperties.SetAutomationId(lines, "HomeRecent");
        _body.Children.Add(lines);

        _body.Children.Add(Kit.Text(SecretProtectors.StorageSentence, "muted", "small"));
    }

    static string StateWords(DirectState state) => state switch
    {
        DirectState.Connected => "connected",
        DirectState.Connecting => "connecting…",
        DirectState.Waiting => "waiting to reconnect…",
        _ => "closed",
    };
}
