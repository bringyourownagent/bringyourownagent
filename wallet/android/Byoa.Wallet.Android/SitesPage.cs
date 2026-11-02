using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Byoa.Wallet.Engine;
using Byoa.Wallet.Settings;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Android;

/// <summary>
/// Sites, as on the desktop wallet's Sites page: every site that asked and the visitor's answer, one line each, with its settings under "Settings"
/// (what it may use and its limits, the models it is given, Pause and Resume, Revoke, Block), the blocked sites in a list of their own with Unblock,
/// and the one setting that is for every site: how long an unused site stays allowed.
/// </summary>
sealed class SitesPage : ListPage
{
    static readonly (int Hours, string Label)[] PauseChoices = [(0, "never"), (1, "an hour"), (24, "a day"), (168, "a week")];

    /// <summary>Sites a page at a time: a long list stays light on a phone.</summary>
    const int RowsPerPage = 20;

    readonly HashSet<string> _open = new(StringComparer.Ordinal);
    int _page;

    public SitesPage(Shell shell) : base(shell)
    {
    }

    public override string? Title => "Sites";

    protected override void Fill()
    {
        Body.Children.Add(Kit.Text("Every site that asked, and your answer. A site gets the same answer, “AI unavailable”, whether you declined, blocked it or have no wallet.", "muted"));
        Body.Children.Add(Kit.Button("Connect by link", "allow", "SitesConnect", () => Shell.Push(new ConnectPage(Shell)), "small"));
        var all = W.Store.Read(s => s.Sites.Select(r => W.Engine.FindSite(r.Origin)).OfType<SiteRecord>().ToList())
            .Where(s => s.Status != SiteStatus.None || s.Skills.Count > 0)
            .OrderByDescending(s => W.Engine.LastSeen(s) ?? DateTimeOffset.MinValue).ToList();
        var open = all.Where(s => s.Status != SiteStatus.Blocked).ToList();
        var blocked = all.Where(s => s.Status == SiteStatus.Blocked).ToList();
        if (all.Count == 0)
        {
            Body.Children.Add(Kit.Text("No site has asked yet. A site may ask only after you press something on it, and the wallet asks you before it answers.", "muted"));
        }

        var pages = Math.Max(1, (open.Count + RowsPerPage - 1) / RowsPerPage);
        _page = Math.Clamp(_page, 0, pages - 1);
        foreach (var site in open.Skip(_page * RowsPerPage).Take(RowsPerPage)) Body.Children.Add(Row(site));
        if (pages > 1) Body.Children.Add(Pager(pages));

        if (blocked.Count > 0)
        {
            Body.Children.Add(Kit.Text("Blocked sites", "label"));
            Body.Children.Add(Kit.Text("A blocked site never shows a notice, and hears only “AI unavailable”. To the site, a block looks exactly like a decline or a missing wallet.", "muted", "small"));
            foreach (var site in blocked) Body.Children.Add(BlockedRow(site));
        }

        // a setting for every site, not for one: it comes after the sites, at the very bottom
        Body.Children.Add(Kit.Rule(6, 2));
        Body.Children.Add(PauseSetting());
    }

    Control Pager(int pages)
    {
        var newer = Kit.Button("Newer", "deny", "SitesNewer", () => { _page--; Redraw(); ScrollToTop(); }, "small");
        var older = Kit.Button("Older", "deny", "SitesOlder", () => { _page++; Redraw(); ScrollToTop(); }, "small");
        newer.IsEnabled = _page > 0;
        older.IsEnabled = _page < pages - 1;
        return Kit.Columns("Auto,*,Auto", newer, Kit.Text("page " + (_page + 1).ToString(CultureInfo.InvariantCulture) + " of " + pages.ToString(CultureInfo.InvariantCulture), "muted", "small"), older);
    }

    /// <summary>«Pause a site after it goes unused for [a day]».</summary>
    Control PauseSetting()
    {
        var hours = W.Store.Read(s => s.Limits.PauseUnusedAfterHours);
        var choices = new WrapPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };
        var all = PauseChoices.ToList();
        if (all.FindIndex(c => c.Hours == hours) < 0) all.Add((hours, WalletEngine.Hours(hours)));
        foreach (var (h, label) in all)
        {
            var choice = Kit.Button(label, h == hours ? "allow" : "deny", "PauseUnusedAfter-" + h.ToString(CultureInfo.InvariantCulture), () => W.Store.Update(s => s.Limits.PauseUnusedAfterHours = h), "small");
            choice.Margin = new Thickness(0, 0, 8, 6);
            choices.Children.Add(choice);
        }

        return Kit.Column(6,
            Kit.Text("Pause a site after it goes unused for", "strong"),
            choices,
            Kit.Text("A paused site keeps what you allowed, but nothing runs for it on its own: its open sessions end, it does not reconnect by itself, and its next request, after your click, asks you to resume.", "muted", "small"));
    }

    /// <summary>A dot for how recently the site used the wallet: green within 3 minutes, amber within a day, grey before that.</summary>
    static (Control Dot, string When) Activity(DateTimeOffset? last)
    {
        var ago = last is { } t ? DateTimeOffset.UtcNow - t : (TimeSpan?)null;
        var color = ago switch
        {
            { } a when a <= TimeSpan.FromMinutes(3) => "#2e9d57",
            { } a when a <= TimeSpan.FromDays(1) => "#e0a100",
            _ => "#b5bac4",
        };
        var when = ago switch
        {
            null => "never used",
            { TotalMinutes: < 1 } => "used just now",
            { TotalMinutes: < 60 } a => "last used " + ((int)a.TotalMinutes).ToString(CultureInfo.InvariantCulture) + " min ago",
            { TotalHours: < 24 } a => "last used " + ((int)a.TotalHours).ToString(CultureInfo.InvariantCulture) + " h ago",
            { TotalDays: < 2 } => "last used yesterday",
            { } a => "last used " + ((int)a.TotalDays).ToString(CultureInfo.InvariantCulture) + " days ago",
        };
        return (Kit.Dot(color), when);
    }

    Control Row(SiteRecord site)
    {
        var host = Kit.HostOf(site.Origin);
        var paused = W.Engine.PausedNow(site);
        var (chip, kind) = site.Status switch
        {
            SiteStatus.Allowed when paused is not null => ("Paused", "orange"),
            SiteStatus.Allowed => ("Allowed", "allowed"),
            SiteStatus.Declined => ("Declined", "outline"),
            _ => ("Not allowed yet", "outline"),
        };
        var (dot, when) = Activity(W.Engine.LastSeen(site));
        var name = Kit.Text(host, "hostmono");
        name.TextWrapping = Avalonia.Media.TextWrapping.NoWrap;
        name.TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis;
        var header = Kit.Column(4,
            Kit.Row(8, dot, name),
            Kit.Row(8, Kit.Chip(chip, kind), Kit.Text(W.Direct.IsConnected(site.Origin) ? "connected now" : when, "muted", "small")));
        return Kit.Fold(_open, site.Origin, "SiteExpand-" + host, host + ": " + chip, header, () => Details(site, host, paused), Redraw);
    }

    Control BlockedRow(SiteRecord site)
    {
        var host = Kit.HostOf(site.Origin);
        var line = Kit.Columns("*,Auto", Kit.Text(host, "hostmono"), Kit.Button("Unblock", "deny", "SiteUnblock-" + host, () => W.Engine.Block(site.Origin, false), "small"));
        var row = Kit.Box("row", line);
        AutomationProperties.SetName(row, host + ": Blocked");
        return row;
    }

    /// <summary>All the settings of one site: what it was told, its limits, its models, and what can be done with it.</summary>
    Control Details(SiteRecord site, string host, string? paused)
    {
        var card = Kit.Column(8);
        var unavailable = site.Status == SiteStatus.Allowed ? MissingRequiredSkill(site) : null;
        var line = site.Status switch
        {
            SiteStatus.Allowed when paused is not null => paused + ". It keeps what you allowed. Press Resume, or its next request, after your click, asks you to resume.",
            SiteStatus.Allowed => site.AutoDecide ? "Allowed. Next time the wallet decides on its own, within these limits." : "Allowed within your limits. Each new visit asks again after your click.",
            SiteStatus.Declined => "You declined last time. It may ask again after your next click.",
            _ => "Not allowed yet. Its next request, after your click, shows the short notice.",
        };
        if (unavailable is not null) line = "Allowed, but nothing on this phone can serve " + Vocabulary.SkillName(unavailable) + ".";
        card.Children.Add(Kit.Text(line, "muted"));
        if (!string.IsNullOrEmpty(site.Summary)) card.Children.Add(Kit.Box("field", Kit.Text("“" + site.Summary + "”")));

        if (site.Status == SiteStatus.Allowed)
        {
            foreach (var skill in site.Skills) card.Children.Add(Kit.Row(8, Kit.Text(Vocabulary.SkillName(skill), "strong"), Kit.Text(skill, "mono", "small")));
            var sessions = W.Engine.ActiveSessions(site.Origin);
            card.Children.Add(Kit.Text(
                "Up to " + Money.Show(Money.Parse(site.Budget, 0)) + " per session · paid fallback: " + (site.PaidAllowed ? "allowed without asking" : "asks first")
                + (sessions > 0 ? " · " + sessions.ToString(CultureInfo.InvariantCulture) + " open session" + (sessions == 1 ? "" : "s") : ""), "muted"));
            if (W.Direct.IsConnected(site.Origin) || W.Pairings.Get(site.Origin) is not null)
            {
                card.Children.Add(Kit.Text((W.Direct.IsConnected(site.Origin) ? "Connected now (the wallet's own connection to the site)" : "Not connected now")
                    + (W.Pairings.Get(site.Origin) is not null ? " · paired: the wallet reconnects on its own while “decide on my own” is on" : ""), "muted"));
            }

            if (ModelsBlock(site, host) is { } models) card.Children.Add(models);
            card.Children.Add(Kit.Tick("Next time, decide on my own within these limits", "SiteAuto-" + host, site.AutoDecide, on => W.Engine.SetAutoDecide(site.Origin, on)));
            card.Children.Add(Kit.Button("Limits of this site", "deny", "SiteLimits-" + host, () => Shell.Push(new LimitsPage(Shell, site.Origin)), "small"));
        }

        if (site.QuietRequests > 0)
        {
            card.Children.Add(Kit.Text(site.QuietRequests.ToString(CultureInfo.InvariantCulture) + (site.QuietRequests == 1 ? " request" : " requests")
                + " without a click held back: no notice.", "muted"));
        }

        var buttons = new WrapPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };
        void Add(Button b)
        {
            b.Margin = new Thickness(0, 0, 8, 6);
            buttons.Children.Add(b);
        }

        if (site.Status == SiteStatus.Allowed)
        {
            Add(paused is null
                ? Kit.Button("Pause", "deny", "SitePause-" + host, () => W.Engine.Pause(site.Origin), "small")
                : Kit.Button("Resume", "allow", "SiteResume-" + host, () => W.Engine.Resume(site.Origin), "small"));
            Add(Kit.Button("Revoke", "deny", "SiteRevoke-" + host, () => W.Engine.Revoke(site.Origin), "small"));
        }

        Add(Kit.Button("Block", "deny", "SiteBlock-" + host, () => W.Engine.Block(site.Origin, true), "small"));
        card.Children.Add(buttons);
        return card;
    }

    /// <summary>The models a site is given, in its order, and the models of the visitor that it could be given too.</summary>
    Control? ModelsBlock(SiteRecord site, string host)
    {
        var all = W.Registry.ListSources(Vocabulary.Generate).Where(s => !s.BuiltIn || s.Kind is not null).ToList();
        string NameOf(string id) => all.FirstOrDefault(s => s.Id == id) is { } info ? info.Name : id;
        var panel = Kit.Column(6);
        var any = false;
        foreach (var skill in new[] { Vocabulary.Generate, Vocabulary.ImageGenerate })
        {
            if (site.ModelsFor(skill) is not { SourceIds.Count: > 0 } entry) continue;
            any = true;
            Skill(skill, entry.SourceIds.ToList());
        }

        if (!any) return null;

        void Skill(string skill, List<string> ids)
        {
            var pictures = skill == Vocabulary.ImageGenerate;
            var tag = pictures ? "Pictures" : "";
            var notes = W.Engine.SiteModelNotes(site.Origin, skill);          // what is amiss with a model for this site: it falls short, or the wallet does not know
            panel.Children.Add(Kit.Text(pictures ? "Models this site draws pictures with" : "Models this site is given", "label"));
            for (var i = 0; i < ids.Count; i++)
            {
                var index = i;
                var id = ids[i];
                var safe = SafeId(id);
                var known = all.FirstOrDefault(s => s.Id == id);
                var up = Kit.Move(true, NameOf(id), "SiteModel" + tag + "Up-" + host + "-" + safe, () => Move(index, -1));
                var down = Kit.Move(false, NameOf(id), "SiteModel" + tag + "Down-" + host + "-" + safe, () => Move(index, 1));
                var remove = Kit.Button("Remove", "deny", "SiteModel" + tag + "Remove-" + host + "-" + safe,
                    () => W.Engine.SetSiteModels(site.Origin, skill, ids.Where(x => x != id).ToList()), "small");
                up.IsEnabled = index > 0;
                down.IsEnabled = index < ids.Count - 1;
                var text = Kit.Column(1, Kit.Text((index + 1).ToString(CultureInfo.InvariantCulture) + ".  " + NameOf(id), "strong"),
                    Kit.Text(known is null ? "no longer in your models" : known.StateText, "muted", "small"));
                if (notes.TryGetValue(id, out var note)) text.Children.Add(Kit.Text(note, "small", "bad"));
                panel.Children.Add(Kit.Box("field", Kit.Column(6, text, Kit.Row(6, up, down, remove))));
            }

            void Move(int index, int by)
            {
                var j = index + by;
                if (j < 0 || j >= ids.Count) return;
                (ids[index], ids[j]) = (ids[j], ids[index]);
                W.Engine.SetSiteModels(site.Origin, skill, ids);
            }

            var others = all.Where(s => !ids.Contains(s.Id) && (pictures ? W.Engine.ServesPictures(s.Id) : W.Engine.ServesText(s.Id))).ToList();
            if (others.Count > 0)
            {
                panel.Children.Add(Kit.Text("Add one of your models", "muted", "small"));
                var adds = new WrapPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };
                foreach (var other in others)
                {
                    var add = Kit.Button("Add " + other.Name.Split(" · ")[0], "deny", "SiteModel" + tag + "Add-" + host + "-" + SafeId(other.Id), () =>
                    {
                        ids.Add(other.Id);
                        W.Engine.SetSiteModels(site.Origin, skill, ids);
                    }, "small");
                    add.Margin = new Thickness(0, 0, 8, 6);
                    adds.Children.Add(add);
                }

                panel.Children.Add(adds);
            }
        }

        panel.Children.Add(Kit.Tick("The site sees the names of its models", "SiteShowNames-" + host, site.ShowModelNames, on => W.Engine.SetShowModelNames(site.Origin, on)));
        panel.Children.Add(Kit.Text("The site reaches only these models, by a code of its own, and never the others. A change reaches it at once.", "muted", "small"));
        return Kit.Box("field", panel);
    }

    static string SafeId(string value) => new(value.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());

    /// <summary>Checks the same language-specific route the engine will use, without changing settings (the phone's counterpart of the desktop's check).</summary>
    string? MissingRequiredSkill(SiteRecord site)
    {
        var settings = W.Store.Snapshot();
        foreach (var skill in site.Skills)
        {
            var languages = site.Languages.Count == 0 ? new string?[] { null } : site.Languages.Cast<string?>();
            foreach (var language in languages)
            {
                var primary = Vocabulary.PrimaryLanguage(language);
                var chain = settings.Chains.FirstOrDefault(c => c.Skill == skill && c.Language == primary)
                    ?? settings.Chains.FirstOrDefault(c => c.Skill == skill)
                    ?? new SkillChain { Skill = skill, Language = primary };
                var enabled = new Func<string, bool>(id => settings.Sources.FirstOrDefault(s => s.Id == id)?.Enabled ?? true);
                // (pictures have no route of the wallet's own: the site can draw when one of the picture models it was given is ready)
                var usable = skill == Vocabulary.Generate
                    ? Router.PlanText(chain, W.Sources.Text, enabled, language).First is not null
                    : skill == Vocabulary.Transcribe
                        ? Router.Plan(chain, W.Sources.All, enabled, language).First is not null
                        : skill == Vocabulary.ImageGenerate
                            && (site.ModelsFor(skill)?.SourceIds ?? []).Any(id => enabled(id) && W.Sources.Text.FirstOrDefault(s => s.Id == id) is IImageSource { State: SourceState.Ready });
                if (!usable) return skill;
            }
        }

        return null;
    }
}
