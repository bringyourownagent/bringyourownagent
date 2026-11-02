using System.Globalization;
using Avalonia.Automation;
using Avalonia.Controls;
using Byoa.Wallet.Engine;
using Byoa.Wallet.Settings;

namespace Byoa.Wallet.Android;

/// <summary>
/// Limits, with the desktop wallet's rules: money and calls for the whole wallet, the defaults of every site, the text limits (size, length, wait,
/// questions at once and a minute), and the same for each allowed site. A call goes ahead only within all of them. Each number is changed with a
/// minus and a plus, the way the desktop changes money, so that a phone needs no keyboard here.
/// </summary>
sealed class LimitsPage : ListPage
{
    readonly string? _first;
    readonly bool _reached;

    /// <param name="shell">The shell.</param>
    /// <param name="first">The site whose limits come first.</param>
    /// <param name="reached">The site reached one of its limits (the page was opened from the notice or the raise question that says so): only then does the page say it.
    /// Opened from the site's Settings, the page says nothing of the kind.</param>
    public LimitsPage(Shell shell, string? first = null, bool reached = false) : base(shell)
    {
        _first = first;
        _reached = reached;
    }

    public override string? Title => "Limits";

    protected override void Fill()
    {
        Body.Children.Add(Kit.Text("Limits for each site and for the whole wallet. A call goes ahead only within all of them, and within the limits the site declared itself.", "muted"));
        var settings = W.Store.Snapshot();
        var now = DateTimeOffset.UtcNow;
        var allowed = settings.Sites.Where(s => s.Status == SiteStatus.Allowed).ToList();

        // a site that reached a limit (or whose Settings opened this page) comes first, so the limit to raise (or to look at) is the first thing on the page
        var first = allowed.FirstOrDefault(s => s.Origin == _first);
        if (first is not null)
        {
            SiteLimits(first, settings, now, reached: _reached);
            allowed.Remove(first);
        }

        Body.Children.Add(Kit.Text("Whole wallet, all sites", "label"));
        var (allSpent, allCalls) = W.Activity.Today(now);
        Body.Children.Add(Kit.Box("card", Kit.Column(10,
            MoneyLimit("Money per day", settings.Limits.WalletPerDay, allSpent, v => W.Store.Update(s => s.Limits.WalletPerDay = v), "LimitWalletDay"),
            Kit.Text(allCalls.ToString(CultureInfo.InvariantCulture) + (allCalls == 1 ? " call today." : " calls today."), "muted"))));

        Body.Children.Add(Kit.Text("Every site, unless it has its own", "label"));
        Body.Children.Add(Kit.Box("card", Kit.Column(10,
            MoneyLimit("Budget per session (proposed when a site asks)", settings.Limits.DefaultSessionBudget, null, v => W.Store.Update(s => s.Limits.DefaultSessionBudget = v), "LimitSession"),
            MoneyLimit("Money per site per day", settings.Limits.SitePerDay, null, v => W.Store.Update(s => s.Limits.SitePerDay = v), "LimitSiteDay"),
            CountLimit("Calls per site per day", settings.Limits.SiteCallsPerDay, null, 0, 1_000_000, 100, v => W.Store.Update(s => s.Limits.SiteCallsPerDay = v), "LimitSiteCalls"))));

        Body.Children.Add(Kit.Text("Text, every site: size, length, wait", "label"));
        Body.Children.Add(TextLimits(null));

        foreach (var site in allowed) SiteLimits(site, settings, now, reached: false);
    }

    /// <summary>One site's money and call limits, and its text limits.</summary>
    void SiteLimits(SiteRecord site, WalletSettings settings, DateTimeOffset now, bool reached)
    {
        var host = Kit.HostOf(site.Origin);
        var (spent, calls) = W.Activity.Today(now, site.Origin);
        Body.Children.Add(Kit.Text("This site: " + host, "label"));
        var card = Kit.Column(10,
            MoneyLimit("Budget per session", site.Budget, null, v => W.Store.Update(s => s.Site(site.Origin)!.Budget = v), "LimitBudget-" + host),
            MoneyLimit("Money per day", site.PerDay ?? settings.Limits.SitePerDay, spent, v => W.Store.Update(s => s.Site(site.Origin)!.PerDay = v), "LimitDay-" + host),
            CountLimit("Calls per day", site.CallsPerDay ?? settings.Limits.SiteCallsPerDay, calls, 0, 1_000_000, 100, v => W.Store.Update(s => s.Site(site.Origin)!.CallsPerDay = v), "LimitCalls-" + host),
            Kit.Text("A change counts at once, for the session that is running too.", "muted", "small"));
        if (reached) card.Children.Insert(0, Kit.Text("This site reached one of these limits. Raise it, and its calls go on.", "strong"));
        Body.Children.Add(Kit.Box("card", card));
        Body.Children.Add(Kit.Text("Text for this site: " + host, "label"));
        Body.Children.Add(TextLimits(site));
    }

    Control MoneyLimit(string label, string value, decimal? used, Action<string> set, string id)
    {
        var amount = Money.Parse(value, 0);
        var col = Kit.Column(4, Kit.Stepper(label, Money.Show(amount), id,
            () => set(Money.ToSpec(Money.StepDown(amount))), () => set(Money.ToSpec(Money.StepUp(amount)))));
        if (used is { } u)
        {
            col.Children.Add(new ProgressBar { Minimum = 0, Maximum = 1, Value = amount == 0 ? 0 : (double)Math.Min(1, u / amount), Height = 6 });
            col.Children.Add(Kit.Text(Money.Show(u) + " of " + Money.Show(amount) + " used today", "muted", "small"));
        }

        return col;
    }

    Control CountLimit(string label, int value, int? used, int min, int max, int step, Action<int> set, string id, string? hint = null)
    {
        var col = Kit.Column(2, Kit.Stepper(label, value.ToString("N0", CultureInfo.InvariantCulture), id,
            () => set(Math.Max(min, value - step)), () => set(Math.Min(max, value + step)), value > min, value < max));
        if (used is { } u) col.Children.Add(Kit.Text(u.ToString(CultureInfo.InvariantCulture) + " of " + value.ToString("N0", CultureInfo.InvariantCulture) + " used today", "muted", "small"));
        if (hint is not null) col.Children.Add(Kit.Text(hint, "muted", "small"));
        return col;
    }

    static string N(int value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>The text limits of every site (<paramref name="site"/> null), or of one site (what it has of its own, and what it recommends).</summary>
    Control TextLimits(SiteRecord? site)
    {
        var settings = W.Store.Snapshot();
        var current = Settings.TextLimits.ForSite(settings.Limits, site is null ? null : settings.Site(site.Origin));
        var suffix = site is null ? "Text" : "SiteText-" + Kit.HostOf(site.Origin);

        void Changed(Action<WalletSettings> change)
        {
            W.Store.Update(change);
            W.Engine.LimitsChanged(site?.Origin);
        }

        var rows = new List<Control>
        {
            CountLimit("Text a call may send (characters)", current.InputChars, null, 1_000, Settings.TextLimits.MaxInputChars, 10_000,
                v => Changed(s => { if (site is null) s.Limits.TextInputChars = v; else s.Site(site.Origin)!.TextInputChars = v; }), "LimitInput-" + suffix,
                "About 4 characters make a token. The wallet never takes more than " + N(Settings.TextLimits.MaxInputChars) + "."),
            CountLimit("Longest answer (tokens)", current.AnswerTokens, null, 16, Settings.TextLimits.MaxAnswerTokens, 1_000,
                v => Changed(s => { if (site is null) s.Limits.TextAnswerTokens = v; else s.Site(site.Origin)!.TextAnswerTokens = v; }), "LimitAnswer-" + suffix,
                "The most a model may write for one question. Up to " + N(Settings.TextLimits.MaxAnswerTokens) + "."),
            CountLimit("Longest wait for one answer (seconds)", current.CallSeconds, null, 5, Settings.TextLimits.MaxCallSeconds, 30,
                v => Changed(s => { if (site is null) s.Limits.TextCallSeconds = v; else s.Site(site.Origin)!.TextCallSeconds = v; }), "LimitWait-" + suffix,
                "A model that is still starting may need a minute or two. Up to " + N(Settings.TextLimits.MaxCallSeconds) + "."),
            CountLimit("Questions at once", current.ParallelCalls, null, 1, Settings.TextLimits.MaxParallelCalls, 1,
                v => Changed(s => { if (site is null) s.Limits.ParallelCalls = v; else s.Site(site.Origin)!.ParallelCalls = v; }), "LimitParallel-" + suffix,
                "A model on a server answers one at a time; the others wait their turn. Up to " + N(Settings.TextLimits.MaxParallelCalls) + "."),
            CountLimit("Questions a minute", current.CallsPerMinute, null, 1, Settings.TextLimits.MaxCallsPerMinute, 10,
                v => Changed(s => { if (site is null) s.Limits.CallsPerMinute = v; else s.Site(site.Origin)!.CallsPerMinute = v; }), "LimitRate-" + suffix),
        };

        if (site is null)
        {
            rows.Add(Kit.Text("These are the limits of every site. A site may ask for more in its request, and you decide when it asks; a site can also be given limits of its own here.", "muted", "small"));
            return Kit.Box("card", Kit.Column(10, rows.ToArray()));
        }

        var host = Kit.HostOf(site.Origin);
        var stored = settings.Site(site.Origin) ?? site;
        if (stored.Asked is { } asked)
        {
            var beyond = Settings.TextLimits.Beyond(current, new RequestedLimits(asked.InputChars, asked.AnswerTokens, asked.CallSeconds, asked.ParallelCalls, asked.CallsPerMinute));
            if (beyond is not null)
            {
                var parts = new List<string>();
                if (beyond.InputChars is { } input) parts.Add("text up to " + N(input) + " characters");
                if (beyond.AnswerTokens is { } tokens) parts.Add("answers up to " + N(tokens) + " tokens");
                if (beyond.CallSeconds is { } seconds) parts.Add("waiting up to " + N(seconds) + " seconds");
                if (beyond.ParallelCalls is { } parallel) parts.Add(N(parallel) + " questions at once");
                if (beyond.CallsPerMinute is { } perMinute) parts.Add(N(perMinute) + " questions a minute");
                var use = Kit.Button("Use these", "allow", "UseSiteLimits-" + host, () => Changed(s =>
                {
                    var record = s.Site(site.Origin)!;
                    record.TextInputChars = beyond.InputChars ?? record.TextInputChars;
                    record.TextAnswerTokens = beyond.AnswerTokens ?? record.TextAnswerTokens;
                    record.TextCallSeconds = beyond.CallSeconds ?? record.TextCallSeconds;
                    record.ParallelCalls = beyond.ParallelCalls ?? record.ParallelCalls;
                    record.CallsPerMinute = beyond.CallsPerMinute ?? record.CallsPerMinute;
                }), "small");
                var recommends = Kit.Text("The site recommends: " + string.Join(", ", parts) + ".", "strong");
                AutomationProperties.SetAutomationId(recommends, "SiteRecommends-" + host);
                rows.Add(Kit.Box("field", Kit.Column(6, recommends, use)));
            }
        }

        rows.Add(Kit.Button("Back to the defaults", "deny", "SiteTextLimitsReset-" + host, () => Changed(s =>
        {
            var record = s.Site(site.Origin)!;
            record.TextInputChars = record.TextAnswerTokens = record.TextCallSeconds = record.ParallelCalls = record.CallsPerMinute = null;
        }), "small"));
        return Kit.Box("card", Kit.Column(10, rows.ToArray()));
    }
}
