using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Byoa.Wallet.Activity;
using Byoa.Wallet.Engine;
using Byoa.Wallet.Providers;
using Byoa.Wallet.Settings;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Desktop;

/// <summary>
/// The wallet's detailed settings: Sites, Sources, Skills &amp; fallbacks, Limits and Activity. Opened from the tray
/// or from "Detailed settings…" in the consent window.
/// </summary>
sealed class SettingsWindow : Window
{
    static readonly (string Id, string Name, string Glyph)[] Pages =
    [
        ("sites", "Sites", Ui.Globe),
        ("sources", "Sources", Ui.Screen),
        ("skills", "Skills & fallbacks", Ui.Arrow),
        ("limits", "Limits", Ui.Gauge),
        ("activity", "Activity", Ui.List),
    ];

    readonly WalletServices _w;
    readonly ListBox _tabs;
    readonly Dictionary<string, Avalonia.Controls.Shapes.Path> _tabGlyphs = [];
    readonly ScrollViewer _content = new() { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    readonly TextBlock _todayText = new() { FontFamily = Ui.SignFace, FontWeight = FontWeight.Bold, FontSize = 17 };
    readonly ProgressBar _todayBar = new() { Minimum = 0, Maximum = 1 };
    readonly DispatcherTimer _refresh;
    string _page = "sites";
    bool _dirty;
    DateTimeOffset _rendered;

    public SettingsWindow(WalletServices services)
    {
        _w = services;
        Title = "Agent wallet";
        Width = 940;
        Height = 680;
        MinWidth = 720;
        MinHeight = 480;
        Background = Brushes.White;
        Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://Byoa.Wallet/Assets/app.ico")));
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _tabs = new ListBox { SelectionMode = SelectionMode.Single };
        _tabs.Classes.Add("sidebar");
        AutomationProperties.SetAutomationId(_tabs, "SettingsTabs");
        foreach (var (id, name, glyph) in Pages)
        {
            var icon = Ui.Glyph(glyph, Ui.Brush("Ink2"), 16, 2);
            _tabGlyphs[id] = (Avalonia.Controls.Shapes.Path)((Canvas)((Viewbox)icon).Child!).Children[0];
            var item = new ListBoxItem { Content = Ui.Row(10, icon, Ui.Text(name)), Tag = id };
            AutomationProperties.SetAutomationId(item, "Tab-" + id);
            AutomationProperties.SetName(item, name);
            _tabs.Items.Add(item);
        }

        _tabs.SelectionChanged += (_, _) =>
        {
            if (_tabs.SelectedItem is ListBoxItem { Tag: string id } && id != _page)
            {
                _page = id;
                if (id == "activity") _activityPage = 0;        // back to the newest entries
                PaintTabs();
                Render();
                if (id == "sites") _w.Engine.ClearBadge();
            }
        };

        _todayBar.Classes.Add("meter");
        var sidebarFoot = Ui.Column(4, Ui.Text("Wallet today", "muted", "small"), _todayText, _todayBar);
        sidebarFoot.Margin = new Thickness(16, 12, 16, 16);
        var sidebar = new DockPanel { Background = Ui.Brush("Field") };
        var head = Ui.Row(8,
            new Image { Source = new Avalonia.Media.Imaging.Bitmap(Avalonia.Platform.AssetLoader.Open(new Uri("avares://Byoa.Wallet/Assets/icon-192.png"))), Width = 22, Height = 22 },
            Ui.Text("Agent wallet", "h2"));
        head.Margin = new Thickness(16, 16, 16, 10);
        DockPanel.SetDock(head, Dock.Top);
        DockPanel.SetDock(sidebarFoot, Dock.Bottom);
        sidebar.Children.Add(head);
        sidebar.Children.Add(sidebarFoot);
        sidebar.Children.Add(_tabs);
        var sideBorder = new Border { Child = sidebar, BorderBrush = Ui.Brush("WinRule"), BorderThickness = new Thickness(0, 0, 1, 0), Width = 210 };

        Content = Ui.Columns("Auto,*", sideBorder, _content);

        // Refresh at most twice a second while something changes.
        _refresh = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) =>
        {
            // Sites also redraws every 30 s on its own: its activity dots change colour with time alone
            if (!_dirty && !(_page == "sites" && DateTimeOffset.UtcNow - _rendered > TimeSpan.FromSeconds(30))) return;
            _dirty = false;
            Render();
        });
        _refresh.Start();
        Closed += (_, _) => _refresh.Stop();
        Show("sites");
    }

    /// <summary>The drawing of the open page's menu item takes the sign colour, like its text (App.axaml, ListBox.sidebar).</summary>
    void PaintTabs()
    {
        foreach (var (id, path) in _tabGlyphs) path.Stroke = Ui.Brush(id == _page ? "Sign" : "Ink2");
    }

    /// <summary>Something changed in the wallet: redraw the current page soon (not while the visitor types).</summary>
    public void Invalidate() => _dirty = true;

    public void Show(string? page)
    {
        _page = Pages.Any(p => p.Id == page) ? page! : _page;
        _tabs.SelectedIndex = Array.FindIndex(Pages, p => p.Id == _page);
        PaintTabs();
        Render();
        if (_page == "sites") _w.Engine.ClearBadge();
    }

    void Render()
    {
        var (spent, _) = _w.Activity.Today(DateTimeOffset.UtcNow);
        var limit = Money.Parse(_w.Store.Read(s => s.Limits.WalletPerDay), 1m);
        _todayText.Text = Money.Show(spent) + " of " + Money.Show(limit);
        _todayBar.Value = limit == 0 ? 0 : (double)Math.Min(1, spent / limit);

        _rendered = DateTimeOffset.UtcNow;
        // Keep the scroll position when a page redraws itself.
        var offset = _content.Offset;
        var page = new StackPanel { Spacing = 14, Margin = new Thickness(28, 22, 28, 28) };
        switch (_page)
        {
            case "sites": SitesPage(page); break;
            case "sources": SourcesPage(page); break;
            case "skills": SkillsPage(page); break;
            case "limits": LimitsPage(page); break;
            default: ActivityPage(page); break;
        }

        var previous = _content.Tag as string;
        _content.Content = page;
        _content.Tag = _page;
        if (previous == _page) Dispatcher.UIThread.Post(() => _content.Offset = offset, DispatcherPriority.Loaded);
    }

    static void Head(StackPanel page, string title, string sub)
    {
        page.Children.Add(Ui.Column(2, Ui.Text(title, "h1"), Ui.Wrap(sub, "muted")));
    }

    static Control Section(string title, Control? right = null)
    {
        var t = Ui.Text(title, "h2");
        return right is null ? t : Ui.Columns("*,Auto", t, right);
    }

    // ------------------------------------------------------------------------------------------------
    // Sites

    void SitesPage(StackPanel page)
    {
        Head(page, "Sites", "Every site that asked, and your answer. A site gets the same answer, “AI unavailable”, whether you declined, blocked it or have no wallet.");
        page.Children.Add(PauseSetting());
        var sites = _w.Store.Snapshot().Sites.OrderByDescending(s => _w.Engine.LastSeen(s) ?? DateTimeOffset.MinValue).ToList();
        if (sites.Count == 0)
        {
            page.Children.Add(Ui.Wrap("No site has asked yet. When a page asks to use your AI after your click, a short notice appears above the tray.", "muted"));
            return;
        }

        foreach (var site in sites) page.Children.Add(SiteCard(site));
    }

    static readonly (int Hours, string Label)[] PauseChoices = [(0, "never"), (1, "an hour"), (24, "a day"), (168, "a week")];

    /// <summary>«Pause a site after it goes unused for [a day ▾]».</summary>
    Control PauseSetting()
    {
        var hours = _w.Store.Read(s => s.Limits.PauseUnusedAfterHours);
        var choice = new ComboBox { MinWidth = 110 };
        AutomationProperties.SetAutomationId(choice, "PauseUnusedAfter");
        AutomationProperties.SetName(choice, "Pause a site after it goes unused for");
        foreach (var (h, label) in PauseChoices) choice.Items.Add(new ComboBoxItem { Content = label, Tag = h });
        if (Array.FindIndex(PauseChoices, c => c.Hours == hours) < 0) choice.Items.Add(new ComboBoxItem { Content = WalletEngine.Hours(hours), Tag = hours });
        choice.SelectedIndex = choice.Items.Cast<ComboBoxItem>().ToList().FindIndex(i => (int)i.Tag! == hours);
        choice.SelectionChanged += (_, _) =>
        {
            if (choice.SelectedItem is ComboBoxItem { Tag: int h } && h != _w.Store.Read(s => s.Limits.PauseUnusedAfterHours))
            {
                _w.Store.Update(s => s.Limits.PauseUnusedAfterHours = h);
                Render();
            }
        };
        return Ui.Column(4,
            Ui.Row(8, Ui.Text("Pause a site after it goes unused for", "strong"), choice),
            Ui.Wrap("A paused site keeps what you allowed, but nothing runs for it on its own: its open sessions end, it does not reconnect by itself, "
                + "and its next request, after your click, asks you to resume.", "muted", "small"));
    }

    /// <summary>A coloured dot for how recently the site used the wallet: green within 3 minutes, yellow within a day, grey before that.</summary>
    static Control ActivityDot(DateTimeOffset? last, out string when)
    {
        var ago = last is { } t ? DateTimeOffset.Now - t : (TimeSpan?)null;
        var (color, state) = ago switch
        {
            { } a when a <= TimeSpan.FromMinutes(3) => ("#2e9d57", "active now"),
            { } a when a <= TimeSpan.FromDays(1) => ("#e0a100", "active today"),
            _ => ("#b5bac4", "inactive"),
        };
        when = ago switch
        {
            null => "never used",
            { TotalMinutes: < 1 } => "used just now",
            { TotalMinutes: < 60 } a => "last used " + ((int)a.TotalMinutes).ToString(CultureInfo.InvariantCulture) + " min ago",
            { TotalHours: < 24 } a => "last used " + ((int)a.TotalHours).ToString(CultureInfo.InvariantCulture) + " h ago",
            { TotalDays: < 2 } => "last used yesterday",
            { } a => "last used " + ((int)a.TotalDays).ToString(CultureInfo.InvariantCulture) + " days ago",
        };
        var dot = new Avalonia.Controls.Shapes.Ellipse { Width = 11, Height = 11, Fill = new SolidColorBrush(Color.Parse(color)), VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(dot, state + " · " + when);
        AutomationProperties.SetName(dot, state);
        return dot;
    }

    Control SiteCard(SiteRecord site)
    {
        Origins.TryParse(site.Origin, out var host, out _);
        if (host.Length == 0) host = site.Origin;
        var paused = _w.Engine.PausedNow(site);
        var (chip, kind, line) = site.Status switch
        {
            SiteStatus.Allowed when paused is not null => ("Paused", "orange", paused + ". It keeps what you allowed; its next request, after your click, asks you to resume."),
            SiteStatus.Allowed => ("Allowed", "", site.AutoDecide ? "Allowed. Next time the wallet decides on its own, within these limits." : "Allowed within your limits. Each new visit asks again after your click."),
            SiteStatus.Declined => ("Declined", "outline", "You declined last time. It may ask again after your next click."),
            SiteStatus.Blocked => ("Blocked", "ink", "Blocked: its requests never show a notice, and it hears only “AI unavailable”."),
            _ => ("Not allowed yet", "outline", "Not allowed yet. Its next request, after your click, shows the short notice."),
        };

        var card = new StackPanel { Spacing = 8 };
        var dot = ActivityDot(_w.Engine.LastSeen(site), out var when);
        AutomationProperties.SetAutomationId(dot, "SiteActivity-" + host);
        var title = Ui.Row(9, dot, Ui.Tile(Ui.Globe, 26), Ui.Text(host, "host"), Ui.Text(when, "muted", "small"));
        ((TextBlock)title.Children[2]).FontSize = 19;
        card.Children.Add(Ui.Columns("*,Auto", title, Ui.Chip(chip, kind)));
        card.Children.Add(Ui.Wrap(line, "muted"));
        if (!string.IsNullOrEmpty(site.Summary))
        {
            card.Children.Add(Ui.Box("field", Ui.Wrap("“" + site.Summary + "”")));
        }

        if (site.Status == SiteStatus.Allowed)
        {
            foreach (var skill in site.Skills)
            {
                card.Children.Add(Ui.Row(8, Ui.Text(Vocabulary.SkillName(skill), "strong"), Ui.Text(skill, "mono")));
            }

            var sessions = _w.Engine.ActiveSessions(site.Origin);
            card.Children.Add(Ui.Wrap(
                "Up to " + Money.Show(Money.Parse(site.Budget, 0)) + " per session · paid fallback: " + (site.PaidAllowed ? "allowed without asking" : "asks first")
                + (sessions > 0 ? " · " + sessions.ToString(CultureInfo.InvariantCulture) + " open session" + (sessions == 1 ? "" : "s") : ""), "muted"));
            if (_w.Direct.IsConnected(site.Origin) || _w.Pairings.Get(site.Origin) is not null)
            {
                card.Children.Add(Ui.Wrap((_w.Direct.IsConnected(site.Origin) ? "Connected without the extension (the wallet's own connection to the site)" : "Not connected now")
                    + (_w.Pairings.Get(site.Origin) is not null ? " · paired: the wallet reconnects on its own while “decide on my own” is on" : ""), "muted"));
            }

            var auto = new CheckBox { Content = "Next time, decide on my own within these limits", IsChecked = site.AutoDecide };
            AutomationProperties.SetAutomationId(auto, "SiteAuto-" + host);
            auto.IsCheckedChanged += (_, _) => _w.Engine.SetAutoDecide(site.Origin, auto.IsChecked == true);
            card.Children.Add(auto);
        }

        if (site.QuietRequests > 0)
        {
            card.Children.Add(Ui.Wrap(site.QuietRequests.ToString(CultureInfo.InvariantCulture) + (site.QuietRequests == 1 ? " request" : " requests")
                + " without a click held back: no notice, only a dot on the tray.", "muted"));
        }

        var buttons = Ui.Row(8);
        if (site.Status == SiteStatus.Allowed)
        {
            if (paused is null) buttons.Children.Add(Ui.Button("Pause", "deny", "SitePause-" + host, () => { _w.Engine.Pause(site.Origin); Render(); }, "small"));
            buttons.Children.Add(Ui.Button("Revoke", "deny", "SiteRevoke-" + host, () => { _w.Engine.Revoke(site.Origin); Render(); }, "small"));
        }

        buttons.Children.Add(site.Status == SiteStatus.Blocked
            ? Ui.Button("Unblock", "deny", "SiteUnblock-" + host, () => { _w.Engine.Block(site.Origin, false); Render(); }, "small")
            : Ui.Button("Block", "deny", "SiteBlock-" + host, () => { _w.Engine.Block(site.Origin, true); Render(); }, "small"));
        card.Children.Add(buttons);
        if (site.Status == SiteStatus.Blocked)
        {
            card.Children.Add(Ui.Wrap("To the site, a block looks exactly like a decline or a missing wallet.", "muted", "small"));
        }

        return Ui.Box("card", card);
    }

    // ------------------------------------------------------------------------------------------------
    // Sources

    void SourcesPage(StackPanel page)
    {
        Head(page, "Sources", "Your models and keys. The site never learns which one answered, and keys never leave this computer.");
        var settings = _w.Store.Snapshot();

        page.Children.Add(Section("On this PC"));
        page.Children.Add(SourceCard(_w.Sources.GigaAm, settings,
            "GigaAM v3 transducer (sherpa-onnx). " + (_w.Sources.GigaAm.Folder ?? "Not found in your model folders."), null));
        page.Children.Add(SourceCard(_w.Sources.Whisper, settings, "whisper.cpp model " + settings.WhisperModel + ". " + (_w.Sources.Whisper.ModelPath ?? "It downloads into " + Byoa.Wallet.Protocol.WalletPaths.Models + " on first use."), WhisperExtras(settings)));

        page.Children.Add(Section("API keys"));
        page.Children.Add(SourceCard(_w.Sources.OpenRouter, settings, "ElevenLabs Scribe v2 through OpenRouter, with your own key. Stored with Windows DPAPI for your account only; never shown, never logged.", KeyExtras()));

        page.Children.Add(Section("Local models found",
            Ui.Button("Rescan", "deny", "ModelsRescan", () => { _w.Sources.Catalog.Rescan(); Render(); }, "small")));
        var found = _w.Sources.Catalog.Found;
        var list = new StackPanel { Spacing = 6 };
        if (found.Count == 0) list.Children.Add(Ui.Wrap("None yet.", "muted"));
        foreach (var m in found)
        {
            list.Children.Add(Ui.Columns("Auto,*,Auto",
                Ui.Chip(m.Kind == "gigaam" ? "GigaAM" : "ggml"),
                Ui.Wrap("  " + m.Name + "  ·  " + m.Path, "muted"),
                Ui.Text((m.Bytes / 1_000_000).ToString(CultureInfo.InvariantCulture) + " MB", "muted")));
        }

        page.Children.Add(Ui.Box("card", list));

        page.Children.Add(Section("Model folders"));
        var folders = new StackPanel { Spacing = 6 };
        foreach (var f in settings.ModelFolders)
        {
            var remove = Ui.Link("remove", "FolderRemove", () =>
            {
                _w.Store.Update(s => s.ModelFolders.Remove(f));
                _w.Sources.Catalog.Rescan();
                Render();
            });
            folders.Children.Add(Ui.Columns("*,Auto", Ui.Wrap(f), remove));
        }

        folders.Children.Add(Ui.Button("Add a folder…", "deny", "FolderAdd", async () =>
        {
            var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "A folder with speech models", AllowMultiple = false });
            if (picked.Count > 0 && picked[0].TryGetLocalPath() is { } path)
            {
                _w.Store.Update(s => { if (!s.ModelFolders.Contains(path)) s.ModelFolders.Add(path); });
                _w.Sources.Catalog.Rescan();
                Render();
            }
        }, "small"));
        folders.Children.Add(Ui.Wrap("The wallet only reads these folders; it never changes them.", "muted", "small"));
        page.Children.Add(Ui.Box("card", folders));
    }

    Control SourceCard(ISpeechSource source, WalletSettings settings, string description, Control? extras)
    {
        var enabled = new CheckBox { Content = "Use it", IsChecked = settings.Sources.FirstOrDefault(x => x.Id == source.Id)?.Enabled ?? true };
        AutomationProperties.SetAutomationId(enabled, "SourceEnabled-" + source.Id);
        AutomationProperties.SetName(enabled, "Use " + source.Name);
        enabled.IsCheckedChanged += (_, _) => _w.Store.Update(s => s.Source(source.Id).Enabled = enabled.IsChecked == true);

        var langs = source.Languages is null ? source.LanguageCount.ToString(CultureInfo.InvariantCulture) + " languages" : string.Join(", ", source.Languages.Select(Vocabulary.LanguageName));
        var props = new WrapPanel();
        foreach (var (k, v) in new[]
                 {
                     ("languages", langs),
                     ("runs", source.Runs),
                     ("latency", "≈ " + source.ApproxSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s"),
                     ("cost", source.Paid ? "≈ " + Money.Show(source.EstimateCost(5)) + " / 5 s" : "no cloud bill"),
                 })
        {
            var t = new TextBlock();
            t.Inlines!.Add(new Avalonia.Controls.Documents.Run(k + " ") { FontFamily = (FontFamily)Application.Current!.FindResource("Mono")!, Foreground = Ui.Brush("Sign"), FontSize = 11.5 });
            t.Inlines.Add(new Avalonia.Controls.Documents.Run(v) { FontSize = 12, Foreground = Ui.Brush("Ink2") });
            var b = Ui.Box("tag", t);
            b.Margin = new Thickness(0, 0, 6, 4);
            props.Children.Add(b);
        }

        var stateChip = source.State switch
        {
            SourceState.Ready => Ui.Chip("Ready"),
            SourceState.Downloading => Ui.Chip("Downloading", "orange"),
            SourceState.NeedsDownload => Ui.Chip("Not downloaded", "outline"),
            SourceState.NoKey => Ui.Chip("No key", "outline"),
            _ => Ui.Chip(source.State == SourceState.Missing ? "Not found" : "Failed", "ink"),
        };
        var panel = Ui.Column(6,
            Ui.Columns("*,Auto,Auto", Ui.Text(source.Name, "h2"), stateChip, enabled),
            Ui.Wrap(description, "muted"),
            props,
            Ui.Wrap(source.StateText, "small"));
        enabled.Margin = new Thickness(12, 0, 0, 0);
        if (extras is not null) panel.Children.Add(extras);
        return Ui.Box("card", panel);
    }

    Control WhisperExtras(WalletSettings settings)
    {
        var whisper = _w.Sources.Whisper;
        var panel = new StackPanel { Spacing = 6 };
        var models = new ComboBox { MinWidth = 280 };
        AutomationProperties.SetAutomationId(models, "WhisperModel");
        foreach (var (file, note) in WhisperChoices)
        {
            models.Items.Add(new ComboBoxItem { Content = WhisperSource.ModelLabel(file) + "  —  " + note, Tag = file });
        }

        models.SelectedIndex = Math.Max(0, Array.FindIndex(WhisperChoices, c => c.File == settings.WhisperModel));
        models.SelectionChanged += (_, _) =>
        {
            if (models.SelectedItem is ComboBoxItem { Tag: string file } && file != _w.Store.Read(s => s.WhisperModel))
            {
                _w.Store.Update(s => s.WhisperModel = file);
                Render();
            }
        };
        panel.Children.Add(Ui.Row(8, Ui.Text("Model", "label"), models));
        if (whisper.State is SourceState.NeedsDownload or SourceState.Broken)
        {
            panel.Children.Add(Ui.Button("Download now", "allow", "WhisperDownload", () =>
            {
                _ = whisper.EnsureDownloadedAsync(CancellationToken.None);
                _w.Ui.DownloadStarted(whisper);
                Render();
            }, "small"));
        }

        if (whisper.State == SourceState.Downloading)
        {
            var bar = new ProgressBar { Minimum = 0, Maximum = 1, Value = whisper.DownloadProgress ?? 0 };
            bar.Classes.Add("meter");
            panel.Children.Add(bar);
        }

        return panel;
    }

    static readonly (string File, string Note)[] WhisperChoices =
    [
        ("ggml-large-v3-q5_0.bin", "best for Slovak and other languages, 1.1 GB, GPU recommended"),
        ("ggml-large-v3-turbo-q8_0.bin", "faster, weaker on Slovak, 0.9 GB"),
        ("ggml-medium-q8_0.bin", "smaller, 0.8 GB"),
        ("ggml-small-q8_0.bin", "for computers without a GPU, 0.3 GB"),
    ];

    Control KeyExtras()
    {
        var panel = new StackPanel { Spacing = 8 };
        var has = _w.Secrets.Has(OpenRouterSource.SecretName);
        var box = new TextBox { PasswordChar = '•', Watermark = "sk-or-…", MinWidth = 300 };
        AutomationProperties.SetAutomationId(box, "KeyInput");
        var save = Ui.Button(has ? "Replace the key" : "Add the key", "deny", "KeySave", () =>
        {
            if (string.IsNullOrWhiteSpace(box.Text)) return;
            _w.Secrets.Set(OpenRouterSource.SecretName, box.Text);
            box.Text = "";
            Render();
        }, "small");
        panel.Children.Add(Ui.Row(8, box, save));
        var import = Ui.Button("Import from a file…", "deny", "KeyImport", async () =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "A text file with your OpenRouter key", AllowMultiple = false });
            if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
            {
                _w.Secrets.ImportFromFile(OpenRouterSource.SecretName, path);
                Render();
            }
        }, "small");
        var row = Ui.Row(8, import);
        if (has)
        {
            row.Children.Add(Ui.Button("Remove the key", "deny", "KeyRemove", () =>
            {
                _w.Secrets.Remove(OpenRouterSource.SecretName);
                Render();
            }, "small"));
        }

        panel.Children.Add(row);
        return panel;
    }

    // ------------------------------------------------------------------------------------------------
    // Skills & fallbacks

    void SkillsPage(StackPanel page)
    {
        Head(page, "Skills & fallbacks", "Each skill has a route of sources. A source that is off, busy, failing or does not know the language is skipped and the next stop answers. A paid stop runs only if you allow it.");
        var settings = _w.Store.Snapshot();
        foreach (var chain in settings.Chains)
        {
            page.Children.Add(Section(Vocabulary.SkillName(chain.Skill), Ui.Text(chain.Skill, "mono")));
            var stops = new StackPanel { Spacing = 10 };
            for (var i = 0; i < chain.Stops.Count; i++)
            {
                var stop = chain.Stops[i];
                var source = _w.Sources.All.FirstOrDefault(s => s.Id == stop.SourceId);
                if (source is null) continue;
                stops.Children.Add(StopRow(chain.Skill, i, chain.Stops.Count, stop, source));
            }

            page.Children.Add(Ui.Box("card", stops));

            // The manager's suggestion, from the sources' properties (simple rules in 0.1, no AI).
            var manager = new StackPanel { Spacing = 6 };
            manager.Children.Add(Ui.Text("Manager suggests", "label"));
            foreach (var language in new string?[] { "ru", "sk", "en", null })
            {
                var plan = Router.Plan(chain, _w.Sources.All, id => settings.Source(id).Enabled, language);
                var label = language is null ? "Language not given" : Vocabulary.LanguageName(language);
                manager.Children.Add(Ui.Wrap(label + ": " + Router.Explain(plan, language), "small"));
            }

            page.Children.Add(Ui.Box("manager", manager));
        }
    }

    Control StopRow(string skill, int index, int count, ChainStop stop, ISpeechSource source)
    {
        var name = Ui.Row(8, Ui.Text(source.Name, "strong"),
            Ui.Glyph(source.Runs == Runs.ApiKey ? Ui.Key : Ui.Screen, Ui.Brush("Ink2"), 13, 2),
            Ui.Text(EngineWhere(source), "muted"));
        var langs = source.Languages is null ? source.LanguageCount.ToString(CultureInfo.InvariantCulture) + " languages" : "only " + string.Join(", ", source.Languages.Select(Vocabulary.LanguageName));
        var info = Ui.Wrap((index == 0 ? "First choice · " : "") + langs + " · " + (source.Paid ? "paid" : "no cloud bill") + " · " + source.StateText, "muted");

        var on = new CheckBox { Content = "On", IsChecked = stop.Enabled };
        AutomationProperties.SetAutomationId(on, "StopEnabled-" + source.Id);
        on.IsCheckedChanged += (_, _) => _w.Store.Update(s => s.Chain(skill).Stops.First(x => x.SourceId == source.Id).Enabled = on.IsChecked == true);
        var options = Ui.Row(14, on);
        if (source.Paid)
        {
            var ask = new CheckBox { Content = "Ask before paid", IsChecked = stop.AskBeforePaid };
            AutomationProperties.SetAutomationId(ask, "StopAsk-" + source.Id);
            ask.IsCheckedChanged += (_, _) => _w.Store.Update(s => s.Chain(skill).Stops.First(x => x.SourceId == source.Id).AskBeforePaid = ask.IsChecked == true);
            options.Children.Add(ask);
        }

        var up = Ui.Button("", "deny", "StopUp-" + source.Id, () => Move(skill, source.Id, -1), "square");
        up.Content = Ui.Glyph(Ui.Up, Ui.Brush("Ink"), 14);
        up.IsEnabled = index > 0;
        AutomationProperties.SetName(up, "Move " + source.Name + " up");
        var down = Ui.Button("", "deny", "StopDown-" + source.Id, () => Move(skill, source.Id, 1), "square");
        down.Content = Ui.Glyph(Ui.Down, Ui.Brush("Ink"), 14);
        down.IsEnabled = index < count - 1;
        AutomationProperties.SetName(down, "Move " + source.Name + " down");

        var middle = Ui.Column(3, name, info, options);
        middle.Margin = new Thickness(12, 0, 12, 0);
        return Ui.Columns("Auto,*,Auto", Ui.Stop(index + 1, index == 0, source.Paid), middle, Ui.Row(6, up, down));
    }

    static string EngineWhere(ISpeechSource s) => WalletEngine.Where(s);

    void Move(string skill, string id, int by)
    {
        _w.Store.Update(s =>
        {
            var stops = s.Chain(skill).Stops;
            var i = stops.FindIndex(x => x.SourceId == id);
            var j = i + by;
            if (i < 0 || j < 0 || j >= stops.Count) return;
            (stops[i], stops[j]) = (stops[j], stops[i]);
        });
        Render();
    }

    // ------------------------------------------------------------------------------------------------
    // Limits

    void LimitsPage(StackPanel page)
    {
        Head(page, "Limits", "Limits for each site and for the whole wallet. A call goes ahead only within all of them, and within the limits the site declared itself.");
        var settings = _w.Store.Snapshot();
        var now = DateTimeOffset.UtcNow;

        page.Children.Add(Section("Whole wallet, all sites"));
        var (allSpent, allCalls) = _w.Activity.Today(now);
        page.Children.Add(Ui.Box("card", Ui.Column(10,
            MoneyLimit("Money per day", settings.Limits.WalletPerDay, allSpent, v => _w.Store.Update(s => s.Limits.WalletPerDay = v), "LimitWalletDay"),
            Ui.Wrap(allCalls.ToString(CultureInfo.InvariantCulture) + " calls today.", "muted"))));

        page.Children.Add(Section("Every site, unless it has its own"));
        page.Children.Add(Ui.Box("card", Ui.Column(10,
            MoneyLimit("Budget per session (proposed in the consent window)", settings.Limits.DefaultSessionBudget, null, v => _w.Store.Update(s => s.Limits.DefaultSessionBudget = v), "LimitSession"),
            MoneyLimit("Money per site per day", settings.Limits.SitePerDay, null, v => _w.Store.Update(s => s.Limits.SitePerDay = v), "LimitSiteDay"),
            CountLimit("Calls per site per day", settings.Limits.SiteCallsPerDay, null, v => _w.Store.Update(s => s.Limits.SiteCallsPerDay = v), "LimitSiteCalls"))));

        foreach (var site in settings.Sites.Where(s => s.Status == SiteStatus.Allowed))
        {
            Origins.TryParse(site.Origin, out var host, out _);
            var (spent, calls) = _w.Activity.Today(now, site.Origin);
            page.Children.Add(Section("This site: " + host));
            page.Children.Add(Ui.Box("card", Ui.Column(10,
                MoneyLimit("Budget per session", site.Budget, null, v => _w.Store.Update(s => s.Site(site.Origin)!.Budget = v), "LimitBudget-" + host),
                MoneyLimit("Money per day", site.PerDay ?? settings.Limits.SitePerDay, spent, v => _w.Store.Update(s => s.Site(site.Origin)!.PerDay = v), "LimitDay-" + host),
                CountLimit("Calls per day", site.CallsPerDay ?? settings.Limits.SiteCallsPerDay, calls, v => _w.Store.Update(s => s.Site(site.Origin)!.CallsPerDay = v), "LimitCalls-" + host),
                Ui.Wrap("A new budget per session applies to the site's next session.", "muted", "small"))));
        }
    }

    Control MoneyLimit(string label, string value, decimal? used, Action<string> set, string id)
    {
        var amount = Money.Parse(value, 0);
        var text = Ui.Text(Money.Show(amount), "figure");
        text.MinWidth = 64;
        var down = Ui.Button("", "deny", id + "-down", () => { set(Money.ToSpec(Money.StepDown(amount))); Render(); }, "square");
        down.Content = Ui.Glyph(Ui.Minus, Ui.Brush("Ink"), 14);
        AutomationProperties.SetName(down, "Lower: " + label);
        var up = Ui.Button("", "deny", id + "-up", () => { set(Money.ToSpec(Money.StepUp(amount))); Render(); }, "square");
        up.Content = Ui.Glyph(Ui.Plus, Ui.Brush("Ink"), 14);
        AutomationProperties.SetName(up, "Raise: " + label);
        var col = Ui.Column(4, Ui.Columns("*,Auto", Ui.Text(label), Ui.Row(8, down, text, up)));
        if (used is { } u)
        {
            var bar = new ProgressBar { Minimum = 0, Maximum = 1, Value = amount == 0 ? 0 : (double)Math.Min(1, u / amount) };
            bar.Classes.Add("meter");
            col.Children.Add(bar);
            col.Children.Add(Ui.Text(Money.Show(u) + " of " + Money.Show(amount) + " used today", "muted", "small"));
        }

        return col;
    }

    Control CountLimit(string label, int value, int? used, Action<int> set, string id)
    {
        var box = new NumericUpDown { Value = value, Minimum = 0, Maximum = 1_000_000, Increment = 100, FormatString = "0", Width = 150 };
        AutomationProperties.SetAutomationId(box, id);
        AutomationProperties.SetName(box, label);
        box.ValueChanged += (_, e) =>
        {
            if (e.NewValue is decimal v) set((int)v);
        };
        var col = Ui.Column(4, Ui.Columns("*,Auto", Ui.Text(label), box));
        if (used is { } u) col.Children.Add(Ui.Text(u.ToString(CultureInfo.InvariantCulture) + " of " + value.ToString(CultureInfo.InvariantCulture) + " used today", "muted", "small"));
        return col;
    }

    // ------------------------------------------------------------------------------------------------
    // Activity

    static readonly int[] PageSizes = [50, 100, 200];
    static int _activitySize = 50;          // kept while the wallet runs, for every settings window
    int _activityPage;                      // 0 = the newest entries

    void ActivityPage(StackPanel page)
    {
        Head(page, "Activity", "Every request and call: when, which site, which source answered, the cost, and the fallback when one happened. The audio and the text are never stored.");
        var all = _w.Activity.Recent(int.MaxValue);
        var calls = all.Where(e => e.Kind == "call" && e.Outcome == "ok").ToList();
        page.Children.Add(Ui.Text(
            calls.Count.ToString(CultureInfo.InvariantCulture) + " calls · " + Money.Show(calls.Sum(e => e.CostValue)) + " · "
            + calls.Count(e => e.Fallback is not null).ToString(CultureInfo.InvariantCulture) + " with a fallback (" + Entries(all.Count) + ")", "muted"));
        if (all.Count == 0)
        {
            page.Children.Add(Ui.Wrap("Nothing yet.", "muted"));
            return;
        }

        var pages = (all.Count + _activitySize - 1) / _activitySize;
        _activityPage = Math.Clamp(_activityPage, 0, pages - 1);
        var first = _activityPage * _activitySize;
        var shown = all.Skip(first).Take(_activitySize).ToList();

        page.Children.Add(ActivityPager(pages, first, shown.Count, all.Count, withSize: true, "Top"));
        var list = new StackPanel();
        list.Children.Add(Ui.Rule(0, 0));
        DateTime? day = null;
        foreach (var e in shown)
        {
            var local = e.At.ToLocalTime().Date;
            if (local != day) list.Children.Add(DayHeading(local));      // the times say nothing without the day
            day = local;
            list.Children.Add(ActivityRow(e));
        }

        page.Children.Add(list);
        if (pages > 1 && shown.Count > 10) page.Children.Add(ActivityPager(pages, first, shown.Count, all.Count, withSize: false, "Bottom"));
    }

    static Control DayHeading(DateTime day)
    {
        var today = DateTime.Now.Date;
        var text = day == today ? "Today" : day == today.AddDays(-1) ? "Yesterday" : day.ToString("dddd, d MMMM yyyy", CultureInfo.InvariantCulture);
        var t = Ui.Text(text, "strong", "small");
        return new Border { Child = t, Padding = new Thickness(0, 12, 0, 6), BorderBrush = Ui.Brush("WinRule"), BorderThickness = new Thickness(0, 0, 0, 1) };
    }

    static string Entries(int n) => n.ToString(CultureInfo.InvariantCulture) + (n == 1 ? " entry" : " entries");

    /// <summary>«Show 50 ▾  ·  ‹ Newer · 51–100 of 312 · page 2 of 7 · Older ›», newest first.</summary>
    Control ActivityPager(int pages, int first, int count, int total, bool withSize, string where)
    {
        var row = new DockPanel { LastChildFill = false };
        if (withSize)
        {
            var size = new ComboBox { MinWidth = 76 };
            AutomationProperties.SetAutomationId(size, "ActivityPageSize");
            AutomationProperties.SetName(size, "Entries per page");
            foreach (var n in PageSizes) size.Items.Add(new ComboBoxItem { Content = n.ToString(CultureInfo.InvariantCulture), Tag = n });
            size.SelectedIndex = Math.Max(0, Array.IndexOf(PageSizes, _activitySize));
            size.SelectionChanged += (_, _) =>
            {
                if (size.SelectedItem is not ComboBoxItem { Tag: int n } || n == _activitySize) return;
                var top = _activityPage * _activitySize;            // stay on the entries that were on top
                _activitySize = n;
                _activityPage = top / n;
                Render();
            };
            var label = Ui.Row(8, Ui.Text("Show", "muted"), size, Ui.Text("per page", "muted"));
            DockPanel.SetDock(label, Dock.Left);
            row.Children.Add(label);
        }

        void Go(int to)
        {
            _activityPage = Math.Clamp(to, 0, pages - 1);
            Render();
            // after Render has put the old scroll position back (it posts that at Loaded): another page starts at its top
            Dispatcher.UIThread.Post(() => _content.Offset = new Vector(_content.Offset.X, 0), DispatcherPriority.Background);
        }

        var newest = Ui.Button("« Newest", "deny", "ActivityNewest" + where, () => Go(0), "small");
        var newer = Ui.Button("‹ Newer", "deny", "ActivityNewer" + where, () => Go(_activityPage - 1), "small");
        var older = Ui.Button("Older ›", "deny", "ActivityOlder" + where, () => Go(_activityPage + 1), "small");
        var oldest = Ui.Button("Oldest »", "deny", "ActivityOldest" + where, () => Go(pages - 1), "small");
        newest.IsEnabled = newer.IsEnabled = _activityPage > 0;
        older.IsEnabled = oldest.IsEnabled = _activityPage < pages - 1;
        var place = Ui.Text(
            (first + 1).ToString(CultureInfo.InvariantCulture) + "–" + (first + count).ToString(CultureInfo.InvariantCulture)
            + " of " + total.ToString(CultureInfo.InvariantCulture) + " · page " + (_activityPage + 1).ToString(CultureInfo.InvariantCulture)
            + " of " + pages.ToString(CultureInfo.InvariantCulture), "muted");
        AutomationProperties.SetAutomationId(place, "ActivityPlace" + where);
        place.Margin = new Thickness(6, 0);
        var nav = Ui.Row(6, newest, newer, place, older, oldest);
        DockPanel.SetDock(nav, Dock.Right);
        row.Children.Add(nav);
        return row;
    }

    static Control ActivityRow(ActivityEntry e)
    {
        Origins.TryParse(e.Origin, out var host, out _);
        if (host.Length == 0) host = e.Origin;
        var what = e.Kind switch
        {
            "call" => Vocabulary.SkillName(e.Skill ?? "") + (e.ClipSeconds is { } s ? " · " + s.ToString("0.0", CultureInfo.InvariantCulture) + " s" : "") + (e.Language is { } l ? " · " + l : ""),
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
        if (e.Milliseconds is { } ms) detail.Add(ms.ToString(CultureInfo.InvariantCulture) + " ms");
        if (e.Note is not null) detail.Add(e.Note);
        var (chip, kind) = e.Outcome switch
        {
            "ok" or "allowed" or "released" => (e.Outcome == "ok" ? "Answered" : e.Outcome == "allowed" ? "Allowed" : "Released", ""),
            "auto" => ("On its own", ""),
            "held" => ("Held back", "outline"),
            "declined" => ("Declined", "outline"),
            "closed" => ("No answer", "outline"),
            "limit" => ("Limit", "orange"),
            "refused" => ("Not paid", "outline"),
            "blocked" => ("Blocked", "ink"),
            "revoked" => ("Revoked", "outline"),
            "paused" => ("Paused", "orange"),
            "connected" => ("Connected", ""),
            "ignored" => ("Ignored", "outline"),
            "error" => ("Refused", "ink"),
            _ => (e.Outcome == "failed" ? (e.Kind == "link" ? "Failed" : "No answer") : "Invalid", "ink"),
        };
        var time = Ui.Text(e.At.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture), "muted");
        time.Width = 64;
        time.VerticalAlignment = VerticalAlignment.Top;
        var middle = Ui.Column(2, Ui.Row(8, Ui.Text(what, "strong"), Ui.Text(host, "muted")), Ui.Wrap(string.Join(" · ", detail), "muted", "small"));
        var cost = Ui.Text(e.Kind == "call" && e.Outcome == "ok" ? Money.Show(e.CostValue) : "", "muted");
        cost.Margin = new Thickness(10, 0);
        cost.VerticalAlignment = VerticalAlignment.Top;
        var c = Ui.Chip(chip, kind);
        c.VerticalAlignment = VerticalAlignment.Top;
        var row = Ui.Columns("Auto,*,Auto,Auto", time, middle, cost, c);
        return new Border { Child = row, Padding = new Thickness(0, 9), BorderBrush = Ui.Brush("WinRule"), BorderThickness = new Thickness(0, 0, 0, 1) };
    }
}
