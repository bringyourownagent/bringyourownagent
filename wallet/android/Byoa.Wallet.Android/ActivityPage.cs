using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Byoa.Wallet.Activity;
using Byoa.Wallet.Engine;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Android;

/// <summary>
/// Activity, as on the desktop wallet: the whole log, newest first, a page of entries at a time (so that a long log stays fast), with the day above
/// the entries of each day. No audio and no text is ever in it.
/// </summary>
sealed class ActivityPage : ListPage
{
    static readonly int[] PageSizes = [50, 100, 200];
    static int _size = 50;          // kept while the wallet runs
    int _page;                      // 0 = the newest entries

    public ActivityPage(Shell shell) : base(shell)
    {
    }

    public override string? Title => "Activity";

    protected override void Fill()
    {
        Body.Spacing = 8;
        Body.Children.Add(Kit.Text("Every request and call: when, which site, which source answered, the cost, and the fallback when one happened. The audio and the text are never stored.", "muted", "small"));
        var all = W.Activity.Recent(int.MaxValue);
        var calls = all.Where(e => e.Kind == "call" && e.Outcome == "ok").ToList();
        var summary = Kit.Text(
            calls.Count.ToString(CultureInfo.InvariantCulture) + (calls.Count == 1 ? " call · " : " calls · ") + Money.Show(calls.Sum(e => e.CostValue)) + " · "
            + calls.Count(e => e.Fallback is not null).ToString(CultureInfo.InvariantCulture) + " with a fallback (" + Entries(all.Count) + ")", "muted");
        AutomationProperties.SetAutomationId(summary, "ActivitySummary");
        Body.Children.Add(summary);
        if (all.Count == 0)
        {
            Body.Children.Add(Kit.Text("Nothing yet. Every request, call and connection of a site is written here, without its content.", "muted"));
            return;
        }

        var pages = (all.Count + _size - 1) / _size;
        _page = Math.Clamp(_page, 0, pages - 1);
        var first = _page * _size;
        var shown = all.Skip(first).Take(_size).ToList();
        Body.Children.Add(Pager(pages, first, shown.Count, all.Count, "Top"));
        var list = new StackPanel();
        DateTime? day = null;
        foreach (var e in shown)
        {
            var local = e.At.ToLocalTime().Date;
            if (local != day) list.Children.Add(DayHeading(local));        // the times say nothing without the day
            day = local;
            list.Children.Add(Row(e));
        }

        Body.Children.Add(list);
        if (pages > 1 && shown.Count > 10) Body.Children.Add(Pager(pages, first, shown.Count, all.Count, "Bottom"));
    }

    static string Entries(int n) => n.ToString(CultureInfo.InvariantCulture) + (n == 1 ? " entry" : " entries");

    static Control DayHeading(DateTime day)
    {
        var today = DateTime.Now.Date;
        var text = day == today ? "Today" : day == today.AddDays(-1) ? "Yesterday" : day.ToString("dddd, d MMMM yyyy", CultureInfo.InvariantCulture);
        return new Border { Child = Kit.Text(text, "strong", "small"), Padding = new Thickness(0, 12, 0, 6), BorderBrush = Kit.Brush("WinRule"), BorderThickness = new Thickness(0, 0, 0, 1) };
    }

    /// <summary>«Newest, Newer, 51-100 of 312, Older, Oldest», and how many entries a page has.</summary>
    Control Pager(int pages, int first, int count, int total, string where)
    {
        void Go(int to)
        {
            _page = Math.Clamp(to, 0, pages - 1);
            Redraw();
            ScrollToTop();
        }

        var newest = Kit.Button("Newest", "deny", "ActivityNewest" + where, () => Go(0), "small");
        var newer = Kit.Button("Newer", "deny", "ActivityNewer" + where, () => Go(_page - 1), "small");
        var older = Kit.Button("Older", "deny", "ActivityOlder" + where, () => Go(_page + 1), "small");
        var oldest = Kit.Button("Oldest", "deny", "ActivityOldest" + where, () => Go(pages - 1), "small");
        newest.IsEnabled = newer.IsEnabled = _page > 0;
        older.IsEnabled = oldest.IsEnabled = _page < pages - 1;
        var place = Kit.Text((first + 1).ToString(CultureInfo.InvariantCulture) + "-" + (first + count).ToString(CultureInfo.InvariantCulture)
            + " of " + total.ToString(CultureInfo.InvariantCulture) + " · page " + (_page + 1).ToString(CultureInfo.InvariantCulture)
            + " of " + pages.ToString(CultureInfo.InvariantCulture), "muted", "small");
        AutomationProperties.SetAutomationId(place, "ActivityPlace" + where);
        var nav = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var b in new[] { newest, newer, older, oldest })
        {
            b.Margin = new Thickness(0, 0, 6, 6);
            nav.Children.Add(b);
        }

        var column = Kit.Column(4, place, nav);
        if (where == "Top")
        {
            var sizes = new WrapPanel { Orientation = Orientation.Horizontal };
            sizes.Children.Add(Kit.Text("Show", "muted", "small"));
            foreach (var n in PageSizes)
            {
                var size = Kit.Button(n.ToString(CultureInfo.InvariantCulture), n == _size ? "allow" : "deny", "ActivityPageSize-" + n.ToString(CultureInfo.InvariantCulture), () =>
                {
                    var top = _page * _size;            // stay on the entries that were on top
                    _size = n;
                    _page = top / n;
                    Redraw();
                }, "small");
                size.Margin = new Thickness(8, 0, 0, 6);
                sizes.Children.Add(size);
            }

            sizes.Children.Add(Kit.Text("per page", "muted", "small"));
            column.Children.Add(sizes);
        }

        return column;
    }

    /// <summary>One line, for the home screen: the time, what it was, the site.</summary>
    public static Control Line(ActivityEntry e)
    {
        var host = e.Origin.Length == 0 ? "" : " · " + Kit.HostOf(e.Origin);
        var line = Kit.Columns("Auto,*", Kit.Text(e.At.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture), "muted", "small"), Kit.Text(What(e) + host, "small"));
        ((Control)line.Children[1]).Margin = new Thickness(8, 0, 0, 0);
        return line;
    }

    static string What(ActivityEntry e) => e.Kind switch
    {
        "call" => Vocabulary.SkillName(e.Skill ?? "") + (e.Outcome == "ok" ? " answered" : " " + e.Outcome),
        "change" or "agent" => e.Change ?? "Wallet settings changed",
        "release" => "Released the grant",
        "link" => e.Outcome switch
        {
            "connected" => "Connected to the site",
            "closed" => "Connection to the site closed",
            "error" => "The site refused the connection",
            "failed" => "Could not connect",
            "invalid" => "A link was ignored",
            _ => "Link from a page",
        },
        _ => "Request to use your AI" + (e.Outcome.Length > 0 ? ": " + e.Outcome : ""),
    };

    static Control Row(ActivityEntry e)
    {
        var what = e.Kind switch
        {
            "call" => Vocabulary.SkillName(e.Skill ?? "") + (e.ClipSeconds is { } s ? " · " + s.ToString("0.0", CultureInfo.InvariantCulture) + " s" : "") + (e.Language is { } l ? " · " + l : ""),
            "change" or "agent" => e.Change ?? "Wallet settings changed",
            "release" => "Released the grant",
            "link" => e.Outcome switch
            {
                "connected" => "Connected to the site",
                "closed" => "Connection to the site closed",
                "error" => "The site refused the connection",
                _ => "Link from a page",
            },
            _ => "Request to use your AI",
        };
        var detail = new List<string>();
        if (e.Via == "link") detail.Add("via link");
        if (e.Source is not null) detail.Add(e.Source);
        if (e.Fallback is not null) detail.Add("route: " + e.Fallback);
        if (e.Actor is not null) detail.Add("by " + e.Actor);
        if (e.Milliseconds is { } ms) detail.Add(ms.ToString(CultureInfo.InvariantCulture) + " ms");
        if (e.Note is not null) detail.Add(e.Note);
        var (chip, kind) = e.Kind is "change" or "agent"
            ? ("Changed", "")
            : e.Outcome switch
            {
                "ok" => ("Answered", ""),
                "allowed" => ("Allowed", ""),
                "released" => ("Released", ""),
                "auto" => ("On its own", ""),
                "held" => ("Held back", "outline"),
                "declined" => ("Declined", "outline"),
                "closed" => ("No answer", "outline"),
                "limit" => ("Limit", "outline"),
                "refused" => ("Not paid", "outline"),
                "blocked" => ("Blocked", "ink"),
                "revoked" => ("Revoked", "outline"),
                "paused" => ("Paused", "outline"),
                "resumed" => ("Resumed", ""),
                "connected" => ("Connected", ""),
                "ignored" => ("Ignored", "outline"),
                "error" => ("Refused", "ink"),
                _ => (e.Outcome == "failed" ? (e.Kind == "link" ? "Failed" : "No answer") : "Invalid", "ink"),
            };
        var host = e.Origin.Length == 0 ? "" : Kit.HostOf(e.Origin);
        var top = Kit.Columns("*,Auto", Kit.Text(e.At.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) + (host.Length > 0 ? " · " + host : ""), "muted", "small"), Kit.Chip(chip, kind));
        var cost = e.Kind == "call" && e.Outcome == "ok" ? " · " + Money.Show(e.CostValue) : "";
        var middle = Kit.Column(2, top, Kit.Text(what + cost, "strong"));
        if (detail.Count > 0) middle.Children.Add(Kit.Text(string.Join(" · ", detail), "muted", "small", "mono"));
        var row = new Border { Child = middle, Padding = new Thickness(0, 10), BorderBrush = Kit.Brush("WinRule"), BorderThickness = new Thickness(0, 0, 0, 1) };
        AutomationProperties.SetName(row, what + (host.Length > 0 ? " " + host : "") + ": " + chip);
        return row;
    }
}
