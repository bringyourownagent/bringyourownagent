using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using BringYourOwnAgent;
using Byoa.Wallet.Activity;
using Byoa.Wallet.Audio;
using Byoa.Wallet.Protocol;
using Byoa.Wallet.Settings;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Engine;

/// <summary>
/// The wallet's decisions. It answers the three page calls of CONTRACT.md (request, call, release) and enforces
/// everything: the origin binding, the click rule and the quiet badge, block and decline, "decide on my own",
/// asking only for the difference, the gate of spec/rules-0.1-draft.md (through the BringYourOwnAgent library)
/// before any source runs, the per-day limits, routing with fallbacks, and asking before a paid source.
/// Every "no" reaches the page as the same null (or <c>{ ok: false }</c>).
/// </summary>
public sealed class WalletEngine
{
    /// <summary>How long a request may wait for the visitor (the page's promise resolves null after that).</summary>
    public static TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(120);

    /// <summary>How long a paid-fallback question waits for an answer.</summary>
    public static TimeSpan PaidQuestionTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>How long one source may take for one clip before the next one is tried.</summary>
    public static TimeSpan SourceTimeout { get; set; } = TimeSpan.FromSeconds(45);

    const int MaxAudioBytes = 6 * 1024 * 1024;
    const int MaxManifestChars = 64 * 1024;
    const int MaxTextChars = 4000;

    readonly SettingsStore _store;
    readonly IReadOnlyList<ISpeechSource> _sources;
    readonly ActivityLog _activity;
    readonly IWalletUi _ui;
    readonly TimeProvider _time;
    readonly ConcurrentDictionary<string, GrantSession> _sessions = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, Lazy<Task<JsonNode?>>> _asking = new(StringComparer.Ordinal);
    readonly SemaphoreSlim _prompt = new(1, 1);

    public WalletEngine(SettingsStore store, IReadOnlyList<ISpeechSource> sources, ActivityLog activity, IWalletUi ui, TimeProvider? time = null)
    {
        _store = store;
        _sources = sources;
        _activity = activity;
        _ui = ui;
        _time = time ?? TimeProvider.System;
        _store.Update(Router.EnsureDefaults);
    }

    public IReadOnlyList<ISpeechSource> Sources => _sources;
    public SettingsStore Store => _store;
    public ActivityLog Activity => _activity;

    /// <summary>Raised when the sites or the quiet badge change.</summary>
    public event Action? SitesChanged;

    DateTimeOffset Now => _time.GetUtcNow();

    /// <summary>Answers one message from the extension. Never throws for bad input: it answers the fallback.</summary>
    public async Task<JsonNode?> HandleAsync(WireMessage message, CancellationToken ct = default)
    {
        try
        {
            return message.Type switch
            {
                Wire.Request => await RequestAsync(message.Origin, message.UserActivation, message.Payload, ct).ConfigureAwait(false),
                Wire.Call => await CallAsync(message.Origin, message.Payload, ct).ConfigureAwait(false),
                Wire.Release => Release(message.Origin, message.Payload),
                Wire.Status => Status(),
                Wire.Open => OpenWindow(),
                _ => null,
            };
        }
        catch (OperationCanceledException)
        {
            return Wire.Fallback(message.Type);
        }
        catch (Exception e)
        {
            // Whatever went wrong, the page hears the same "unavailable" as for any other no.
            Log(new ActivityEntry { Origin = message.Origin, Kind = message.Type, Outcome = "invalid", Note = e.GetType().Name });
            return Wire.Fallback(message.Type);
        }
    }

    JsonNode Status() => new JsonObject
    {
        ["running"] = true,
        ["version"] = WalletPaths.Version,
        ["quiet"] = UnseenQuiet,
    };

    int _unseenQuiet;

    /// <summary>Requests without a click held back since the visitor last opened the wallet: the tray badge.</summary>
    public int UnseenQuiet => Volatile.Read(ref _unseenQuiet);

    JsonNode OpenWindow()
    {
        _ui.OpenSettings();
        return new JsonObject { ["opened"] = true };
    }

    // ------------------------------------------------------------------------------------------------
    // request

    async Task<JsonNode?> RequestAsync(string origin, bool userActivation, JsonObject payload, CancellationToken ct)
    {
        var entry = new ActivityEntry { Origin = origin, Kind = Wire.Request, Note = userActivation ? "after a click" : "without a click" };
        if (!Origins.TryParse(origin, out var host, out var secure)) return Done(entry, "invalid", "origin not allowed");
        if (payload["manifest"] is not JsonObject manifestNode) return Done(entry, "invalid", "no manifest");
        var manifestText = manifestNode.ToJsonString();
        if (manifestText.Length > MaxManifestChars) return Done(entry, "invalid", "manifest too large");
        var validation = Manifest.Validate(manifestText);
        if (!validation.Ok) return Done(entry, "invalid", "manifest: " + string.Join(", ", validation.Errors.Select(e => e.Code).Distinct()));
        var manifest = RequestedManifest.From(manifestNode);

        var site = _store.Read(s => s.Site(origin) is { } r ? Copy(r) : null) ?? new SiteRecord { Origin = origin };
        _store.Update(s => Site(s, origin).LastRequest = Now);

        if (site.Status == SiteStatus.Blocked) return Done(entry, "blocked", "blocked site: no notice");

        var (newSkills, costRaised) = Difference(site, manifest);
        var covered = site.Status == SiteStatus.Allowed && newSkills.Count == 0 && !costRaised;
        if (covered && site.AutoDecide)
        {
            var auto = Grant(origin, manifest, site.Skills, Money.Parse(site.Budget, 0.20m));
            if (auto is null) return Done(entry, "invalid", "nothing to grant");
            entry.Note = (userActivation ? "after a click" : "without a click") + "; decided on its own";
            return Done(entry, "auto", null, auto.ToResult());
        }

        if (!userActivation)
        {
            _store.Update(s => Site(s, origin).QuietRequests++);
            Interlocked.Increment(ref _unseenQuiet);
            _ui.QuietRequest(origin);
            SitesChanged?.Invoke();
            return Done(entry, "held", "without a click: quiet badge only");
        }

        // A second request from the same origin while the first is still asking gets the same answer.
        var asking = _asking.GetOrAdd(origin, _ => new Lazy<Task<JsonNode?>>(() => AskVisitorAsync(origin, host, secure, manifest, ct)));
        try
        {
            return await asking.Value.ConfigureAwait(false);
        }
        finally
        {
            _asking.TryRemove(new KeyValuePair<string, Lazy<Task<JsonNode?>>>(origin, asking));
        }
    }

    async Task<JsonNode?> AskVisitorAsync(string origin, string host, bool secure, RequestedManifest manifest, CancellationToken ct)
    {
        await Task.Yield();
        var entry = new ActivityEntry { Origin = origin, Kind = Wire.Request, Note = "after a click" };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            await _prompt.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Done(entry, "closed", "no answer in time");
        }

        try
        {
            var notice = await _ui.ShowNoticeAsync(new NoticeModel(origin, host), timeout.Token).ConfigureAwait(false);
            switch (notice)
            {
                case NoticeAnswer.Block:
                    Block(origin, true);
                    return Done(entry, "blocked", "blocked from the notice");
                case NoticeAnswer.Decline:
                    MarkDeclined(origin);
                    return Done(entry, "declined", "declined in the notice");
                case NoticeAnswer.Closed:
                    return Done(entry, "closed", timeout.IsCancellationRequested ? "no answer in time" : "notice closed");
            }

            // The site may have been blocked or changed from the settings while the notice was up.
            var site = _store.Read(s => s.Site(origin) is { } r ? Copy(r) : null) ?? new SiteRecord { Origin = origin };
            if (site.Status == SiteStatus.Blocked) return Done(entry, "blocked", "blocked while asking");
            var model = BuildConsent(origin, host, secure, manifest, site);
            var answer = await _ui.ShowConsentAsync(model, timeout.Token).ConfigureAwait(false);
            if (!answer.Allow || model.MissingRequired)
            {
                if (timeout.IsCancellationRequested) return Done(entry, "closed", "no answer in time");
                MarkDeclined(origin);
                return Done(entry, "declined", "declined in the consent window");
            }

            var offered = model.Rows.Where(r => r.CanServe && !answer.LeftOff.Contains(r.Skill)).Select(r => r.Skill).ToList();
            var leftOff = model.Rows.Where(r => !r.CanServe || answer.LeftOff.Contains(r.Skill)).Select(r => r.Skill).ToList();
            List<string> skills = [];
            _store.Update(s =>
            {
                var r = Site(s, origin);
                var keep = r.Status == SiteStatus.Allowed ? r.Skills : [];
                r.Skills = keep.Union(offered).Except(model.IsDifference ? [] : leftOff).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
                r.Status = SiteStatus.Allowed;
                r.Budget = Money.ToSpec(answer.Budget);
                r.AutoDecide = answer.AutoDecide;
                r.ExpectedCost = manifest.ExpectedPerSession is { } e ? Money.ToSpec(e) : null;
                r.Summary = manifest.Summary;
                r.Privacy = manifest.Privacy;
                r.Languages = manifest.Skills.SelectMany(k => k.Languages).Distinct().ToList();
                r.QuietRequests = 0;
                r.DecidedAt = Now;
                skills = r.Skills;
            });
            SitesChanged?.Invoke();
            var session = Grant(origin, manifest, skills, answer.Budget);
            if (session is null) return Done(entry, "declined", "no skill left to grant");
            entry.Note = "after a click" + (model.IsDifference ? "; only the difference was shown" : "") + (answer.AutoDecide ? "; decide on my own from now on" : "");
            return Done(entry, "allowed", null, session.ToResult());
        }
        catch (OperationCanceledException)
        {
            return Done(entry, "closed", "no answer in time");
        }
        finally
        {
            _prompt.Release();
        }
    }

    static (List<string> NewSkills, bool CostRaised) Difference(SiteRecord site, RequestedManifest manifest)
    {
        if (site.Status != SiteStatus.Allowed) return (manifest.Skills.Select(s => s.Id).ToList(), false);
        var newSkills = manifest.Skills.Where(s => !site.Skills.Contains(s.Id) && Vocabulary.Served.Contains(s.Id)).Select(s => s.Id).ToList();
        var before = Money.TryParse(site.ExpectedCost, out var b) ? b : (decimal?)null;
        var costRaised = manifest.ExpectedPerSession is { } now && now > (before ?? Money.Parse(site.Budget, 0));
        return (newSkills, costRaised);
    }

    ConsentModel BuildConsent(string origin, string host, bool secure, RequestedManifest manifest, SiteRecord site)
    {
        var (newSkills, costRaised) = Difference(site, manifest);
        var isDifference = site.Status == SiteStatus.Allowed && (newSkills.Count > 0 || costRaised);
        var settings = _store.Snapshot();
        var rows = new List<ConsentRow>();
        foreach (var skill in manifest.Skills)
        {
            if (isDifference && !newSkills.Contains(skill.Id)) continue;
            rows.Add(Row(skill, settings, isNew: isDifference));
        }

        var defaultBudget = Money.Parse(settings.Limits.DefaultSessionBudget, 0.20m);
        var current = Money.Parse(site.Budget, defaultBudget);
        decimal budget;
        decimal? before = null;
        if (site.Status == SiteStatus.Allowed)
        {
            budget = costRaised && manifest.ExpectedPerSession is { } raised ? Math.Max(current, raised) : current;
            if (isDifference && costRaised) before = current;
        }
        else
        {
            budget = manifest.ExpectedPerSession is { } expected && expected > 0 ? expected : defaultBudget;
        }

        return new ConsentModel(origin, host, secure, manifest.Summary, rows, budget, before, site.AutoDecide, isDifference);
    }

    ConsentRow Row(RequestedSkill skill, WalletSettings settings, bool isNew)
    {
        var name = Vocabulary.SkillName(skill.Id);
        if (!Vocabulary.Served.Contains(skill.Id))
        {
            return new ConsentRow(skill.Id, name, skill.Required, isNew, false, [], skill.Languages,
                "None of your sources can do this yet: this wallet serves speech to text only.");
        }

        var chain = settings.Chain(skill.Id);
        var languages = skill.Languages.Count > 0 ? skill.Languages.Cast<string?>().ToList() : [null];
        var routes = new List<RouteLine>();
        var whys = new List<string>();
        foreach (var language in languages)
        {
            var plan = Router.Plan(chain, _sources, id => settings.Source(id).Enabled, language);
            whys.Add(Router.Explain(plan, language));
            if (plan.First is { } first)
            {
                routes.Add(new RouteLine(language, first.Source.Name, Where(first.Source), first.Source.Paid, true));
            }
            else
            {
                routes.Add(new RouteLine(language, "no source", "", false, false));
            }
        }

        var canServe = routes.Count > 0 && routes.All(r => r.Fits);
        return new ConsentRow(skill.Id, name, skill.Required, isNew, canServe, routes, skill.Languages, string.Join(" ", whys.Distinct()));
    }

    public static string Where(ISpeechSource s) => s.Runs == Runs.ThisDevice ? "this PC" : "API key";

    GrantSession? Grant(string origin, RequestedManifest manifest, IReadOnlyList<string> allowedSkills, decimal budget)
    {
        var skills = manifest.Skills.Select(s => s.Id).Where(allowedSkills.Contains).Where(Vocabulary.Served.Contains).ToList();
        if (skills.Count == 0) return null;
        var session = new GrantSession(origin, manifest, skills, budget, Now);
        _sessions[session.Id] = session;
        Warm(manifest.Skills.Where(k => skills.Contains(k.Id)).SelectMany(k => k.Languages.Count > 0 ? k.Languages.Cast<string?>() : [null]));
        return session;
    }

    /// <summary>
    /// Loads the first local source of each language's route in the background, so that a page's first call is fast.
    /// Called when a grant is given, and at start for the sites already allowed.
    /// </summary>
    public void Warm(IEnumerable<string?> languages)
    {
        var settings = _store.Snapshot();
        var chain = settings.Chain(Vocabulary.Transcribe);
        var jobs = new List<(IWarmable Source, string? Language)>();
        foreach (var language in languages.Distinct())
        {
            var first = Router.Plan(chain, _sources, id => settings.Source(id).Enabled, language).Steps
                .FirstOrDefault(s => !s.Source.Paid && s.Source.State == SourceState.Ready);
            if (first?.Source is IWarmable w && !jobs.Contains((w, language))) jobs.Add((w, language));
        }

        // Different sources warm in parallel (GigaAM on the CPU, Whisper on the GPU); one source's languages in turn.
        foreach (var group in jobs.GroupBy(j => j.Source))
        {
            _ = Task.Run(async () =>
            {
                foreach (var (source, language) in group)
                {
                    try
                    {
                        await source.WarmAsync(language).ConfigureAwait(false);
                    }
                    catch (Exception e) when (e is not OutOfMemoryException)
                    {
                        // a source that cannot load shows it in its state; calls fall back
                    }
                }
            });
        }
    }

    /// <summary>At start: warm the models the already allowed sites will need.</summary>
    public void WarmAllowedSites()
    {
        var languages = _store.Read(s => s.Sites.Where(x => x.Status == SiteStatus.Allowed)
            .SelectMany(x => x.Languages.Count > 0 ? x.Languages.Cast<string?>() : [null]).Distinct().ToList());
        if (languages.Count > 0) Warm(languages);
    }

    void MarkDeclined(string origin)
    {
        _store.Update(s =>
        {
            var r = Site(s, origin);
            if (r.Status != SiteStatus.Allowed) r.Status = SiteStatus.Declined;
            r.DecidedAt = Now;
        });
        SitesChanged?.Invoke();
    }

    // ------------------------------------------------------------------------------------------------
    // call

    async Task<JsonNode?> CallAsync(string origin, JsonObject payload, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var grantId = Str(payload["grantId"]);
        var skill = Str(payload["skill"]);
        var entry = new ActivityEntry { Origin = origin, Kind = Wire.Call, Skill = skill };

        if (grantId is null || !_sessions.TryGetValue(grantId, out var session) || session.Origin != origin)
        {
            return Failed(entry, "failed", "no such grant for this site");
        }

        var site = _store.Read(s => s.Site(origin) is { } r ? Copy(r) : null);
        if (site is null || site.Status != SiteStatus.Allowed)
        {
            _sessions.TryRemove(session.Id, out _);
            return Failed(entry, "failed", "the grant was revoked");
        }

        if (skill != Vocabulary.Transcribe)
        {
            return Failed(entry, "failed", "skill not served: " + (skill ?? "none"));
        }

        // The input: a WAV clip, measured here and never trusted from the page.
        if (payload["input"]?["audio"] is not JsonObject audio || Str(audio["base64"]) is not { } base64)
        {
            return Failed(entry, "invalid", "no audio in the input");
        }

        var mime = Str(audio["mime"])?.ToLowerInvariant();
        if (mime is not ("audio/wav" or "audio/wave" or "audio/x-wav" or null))
        {
            return Failed(entry, "invalid", "audio is not audio/wav");
        }

        if (base64.Length > MaxAudioBytes / 3 * 4 + 4) return Failed(entry, "invalid", "audio too large");
        var bytes = new byte[base64.Length / 4 * 3 + 3];
        if (!Convert.TryFromBase64String(base64, bytes, out var written)) return Failed(entry, "invalid", "audio is not base64");
        WavClip clip;
        try
        {
            clip = Wav.ParseAs16k(bytes.AsSpan(0, written));
        }
        catch (InvalidDataException e)
        {
            return Failed(entry, "invalid", "not a WAV clip: " + e.Message);
        }

        var seconds = Math.Round(clip.Seconds, 3);
        entry.ClipSeconds = seconds;
        var language = Vocabulary.PrimaryLanguage(Str(payload["params"]?["language"]));
        var declared = session.Manifest.Skill(skill)?.Languages ?? [];
        language ??= declared.Count == 1 ? declared[0] : null;
        entry.Language = language;

        // The per-day limits of the wallet (the draft rules have no daily budgets yet; the wallet adds them).
        var limits = _store.Read(s => (
            SitePerDay: Money.Parse(site.PerDay ?? s.Limits.SitePerDay, 0.20m),
            CallsPerDay: site.CallsPerDay ?? s.Limits.SiteCallsPerDay,
            WalletPerDay: Money.Parse(s.Limits.WalletPerDay, 1.00m)));
        var (siteSpent, siteCalls) = _activity.Today(Now, origin);
        var (walletSpent, _) = _activity.Today(Now);
        if (siteCalls >= limits.CallsPerDay)
        {
            TellLimit(session, site, "calls per day", $"{limits.CallsPerDay} calls a day for this site");
            return Failed(entry, "limit", "calls per day for this site");
        }

        var settings = _store.Snapshot();
        var chain = settings.Chain(skill);
        var plan = Router.Plan(chain, _sources, id => settings.Source(id).Enabled, language);
        int Index(ISpeechSource s) => chain.Stops.FindIndex(x => x.SourceId == s.Id);

        // The fallback route: sources before the one that answers that the visitor turned off or that failed.
        var route = new Route();
        foreach (var k in plan.Skipped.Where(k => k.Why == "turned off")) route.Add(Index(k.Source), k.Source.Name + ": turned off");
        var callParams = new JsonObject();
        if (language is not null) callParams["language"] = language;
        string? stopReason = null;

        foreach (var step in plan.Steps)
        {
            var source = step.Source;
            if (source is IDownloadableSource download && source.State is SourceState.NeedsDownload or SourceState.Downloading)
            {
                if (source.State == SourceState.NeedsDownload)
                {
                    _ui.DownloadStarted(source);
                    _ = download.EnsureDownloadedAsync(CancellationToken.None);
                }

                route.Add(Index(source), source.Name + ": downloading");
                continue;
            }

            var estimate = source.EstimateCost(clip.Seconds);
            if (estimate > 0 && (siteSpent + estimate > limits.SitePerDay || walletSpent + estimate > limits.WalletPerDay))
            {
                route.Add(Index(source), source.Name + ": over the daily limit");
                stopReason ??= "money per day";
                continue;
            }

            // The gate (spec/rules-0.1-draft.md) decides before the source runs; the slot and the estimate are reserved.
            var at = Now;
            var call = new JsonObject
            {
                ["skill"] = skill,
                ["at"] = GrantSession.Time(at),
                ["provider"] = source.ProviderKind,
                ["estimatedCost"] = Money.ToSpec(estimate),
                ["input"] = new JsonObject { ["clipSeconds"] = seconds, ["textChars"] = 0 },
                ["params"] = callParams.DeepClone(),
            }.ToJsonString();

            await session.Gate.WaitAsync(ct).ConfigureAwait(false);
            CheckResult check;
            try
            {
                check = Gate.CheckCall(session.Manifest.Json, session.GrantJson(), call, session.StateJson(at));
                if (check.Decision is Decision.Allowed or Decision.Warn)
                {
                    session.CallTimes.Add(at);
                    session.Spent += estimate;
                }
            }
            finally
            {
                session.Gate.Release();
            }

            var codes = string.Join(", ", check.Reasons.Select(r => r.Code).Distinct());
            if (check.Decision == Decision.Blocked)
            {
                if (check.Reasons.All(r => r.Code == "not-local"))
                {
                    route.Add(Index(source), source.Name + ": not local");
                    continue;
                }

                if (check.Reasons.Any(r => r.Code is "rate-exceeded" or "clip-too-long" or "text-too-long" or "grant-expired"))
                {
                    if (check.Reasons.Any(r => r.Code == "rate-exceeded")) TellLimit(session, site, "rate", "the site's calls per minute");
                    return Failed(entry, "limit", codes, route.All());
                }

                return Failed(entry, "failed", codes, route.All());
            }

            if (check.Decision == Decision.Ask)
            {
                // budget-exceeded: the wallet never spends past the budget without a new yes. 0.1 does not ask
                // mid-game; it says so once and the visitor raises the budget in the settings.
                TellLimit(session, site, "budget", "the budget of " + Money.Show(session.Budget) + " per session");
                route.Add(Index(source), source.Name + ": " + codes);
                stopReason ??= "budget per session";
                continue;
            }

            if (step.AskFirst && !site.PaidAllowed)
            {
                var answer = await AskPaidAsync(session, site, source, estimate, route.All(), ct).ConfigureAwait(false);
                if (answer == PaidAnswer.No)
                {
                    await Unreserve(session, at, estimate).ConfigureAwait(false);
                    route.Add(Index(source), source.Name + ": you said no");
                    return Failed(entry, "refused", "paid fallback not allowed", route.All());
                }

                if (answer == PaidAnswer.Always)
                {
                    _store.Update(s => Site(s, origin).PaidAllowed = true);
                    site.PaidAllowed = true;
                }
            }

            Transcript result;
            var ran = Stopwatch.GetTimestamp();
            try
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                limit.CancelAfter(SourceTimeout);
                result = await source.TranscribeAsync(clip.Samples, language, limit.Token).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                await Unreserve(session, at, estimate).ConfigureAwait(false);
                if (e is OperationCanceledException && ct.IsCancellationRequested) throw;
                route.Add(Index(source), source.Name + ": " + Short(e));
                continue;
            }

            await session.Gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                session.Spent += result.Cost - estimate;
            }
            finally
            {
                session.Gate.Release();
            }

            var text = result.Text.Trim();
            if (text.Length > MaxTextChars) text = text[..MaxTextChars];
            entry.Outcome = "ok";
            entry.Source = source.Name + (result.Detail is null ? "" : " · " + result.Detail);
            var before = route.Before(Index(source));
            entry.Fallback = before.Count > 0 ? string.Join(" → ", before) + " → " + source.Name : null;
            entry.Cost = Money.ToSpec(result.Cost);
            entry.Milliseconds = (int)Stopwatch.GetElapsedTime(ran).TotalMilliseconds;
            entry.Note = check.Decision == Decision.Warn ? "warn: " + codes : null;
            Log(entry);
            return new JsonObject
            {
                ["ok"] = true,
                ["output"] = new JsonObject { ["text"] = text },
                ["receipt"] = new JsonObject
                {
                    ["cost"] = Money.ToReceipt(result.Cost),
                    ["provider"] = source.ProviderKind == "local-model" ? "local" : "api-key",
                },
            };
        }

        entry.Milliseconds = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        return stopReason is not null
            ? Failed(entry, "limit", stopReason, route.All())
            : Failed(entry, "failed", plan.Steps.Count == 0 ? "no source for this language" : "no source answered", route.All());
    }

    async Task<PaidAnswer> AskPaidAsync(GrantSession session, SiteRecord site, ISpeechSource source, decimal estimate, IReadOnlyList<string> route, CancellationToken ct)
    {
        Origins.TryParse(site.Origin, out var host, out _);
        var why = route.Count > 0 ? "Before it: " + string.Join("; ", route) + "." : "It is the first source in your chain.";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(PaidQuestionTimeout);
        try
        {
            await _prompt.WaitAsync(timeout.Token).ConfigureAwait(false);
            try
            {
                return await _ui.AskPaidAsync(new PaidModel(site.Origin, host, source.Name, estimate, why), timeout.Token).ConfigureAwait(false);
            }
            finally
            {
                _prompt.Release();
            }
        }
        catch (OperationCanceledException)
        {
            return PaidAnswer.No;
        }
    }

    static async Task Unreserve(GrantSession session, DateTimeOffset at, decimal estimate)
    {
        await session.Gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            session.CallTimes.Remove(at);
            session.Spent -= estimate;
        }
        finally
        {
            session.Gate.Release();
        }
    }

    void TellLimit(GrantSession session, SiteRecord site, string kind, string what)
    {
        if (session.LimitNoticeShown) return;
        session.LimitNoticeShown = true;
        Origins.TryParse(site.Origin, out var host, out _);
        _ui.ShowInfo(new InfoModel(host + " reached a limit", "It is limited by " + what + ". Its calls get no answer until you change the limit.", "Detailed settings…", kind == "rate" ? "sites" : "limits"));
    }

    static string Short(Exception e) => e switch
    {
        OperationCanceledException => "took too long",
        _ => e.Message.Length > 80 ? e.Message[..80] : e.Message,
    };

    // ------------------------------------------------------------------------------------------------
    // release and settings actions

    JsonNode? Release(string origin, JsonObject payload)
    {
        var grantId = Str(payload["grantId"]);
        if (grantId is not null && _sessions.TryGetValue(grantId, out var session) && session.Origin == origin)
        {
            _sessions.TryRemove(grantId, out _);
            Log(new ActivityEntry { Origin = origin, Kind = Wire.Release, Outcome = "released" });
        }

        return null;
    }

    /// <summary>Ends every grant of a site and forgets what it was allowed (Sites → Revoke).</summary>
    public void Revoke(string origin)
    {
        DropSessions(origin);
        _store.Update(s =>
        {
            var r = Site(s, origin);
            r.Status = SiteStatus.None;
            r.Skills = [];
            r.AutoDecide = false;
            r.PaidAllowed = false;
        });
        Log(new ActivityEntry { Origin = origin, Kind = Wire.Request, Outcome = "revoked", Note = "revoked in the settings" });
        SitesChanged?.Invoke();
    }

    /// <summary>Blocks a site (no notice ever, null for every request) or unblocks it (it starts again from "not allowed yet").</summary>
    public void Block(string origin, bool blocked)
    {
        if (blocked) DropSessions(origin);
        _store.Update(s =>
        {
            var r = Site(s, origin);
            r.Status = blocked ? SiteStatus.Blocked : SiteStatus.None;
            r.Skills = [];
            r.AutoDecide = false;
            r.PaidAllowed = false;
        });
        SitesChanged?.Invoke();
    }

    public void SetAutoDecide(string origin, bool auto)
    {
        _store.Update(s => Site(s, origin).AutoDecide = auto);
        SitesChanged?.Invoke();
    }

    /// <summary>The visitor opened the wallet and saw the held-back requests: the badge goes (the per-site counts stay in Sites).</summary>
    public void ClearBadge()
    {
        if (Interlocked.Exchange(ref _unseenQuiet, 0) == 0) return;
        SitesChanged?.Invoke();
    }

    public int ActiveSessions(string origin) => _sessions.Values.Count(s => s.Origin == origin && s.ExpiresAt > Now);

    void DropSessions(string origin)
    {
        foreach (var s in _sessions.Values.Where(s => s.Origin == origin).ToList()) _sessions.TryRemove(s.Id, out _);
    }

    // ------------------------------------------------------------------------------------------------
    // helpers

    static SiteRecord Site(WalletSettings s, string origin)
    {
        var r = s.Site(origin);
        if (r is null)
        {
            r = new SiteRecord { Origin = origin };
            s.Sites.Add(r);
        }

        return r;
    }

    static SiteRecord Copy(SiteRecord r) => new()
    {
        Origin = r.Origin, Status = r.Status, AutoDecide = r.AutoDecide, Skills = [.. r.Skills], Budget = r.Budget,
        ExpectedCost = r.ExpectedCost, Summary = r.Summary, Privacy = r.Privacy, Languages = [.. r.Languages], PaidAllowed = r.PaidAllowed,
        PerDay = r.PerDay, CallsPerDay = r.CallsPerDay, QuietRequests = r.QuietRequests, LastRequest = r.LastRequest, DecidedAt = r.DecidedAt,
    };

    static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    JsonNode? Done(ActivityEntry entry, string outcome, string? note, JsonNode? result = null)
    {
        entry.Outcome = outcome;
        if (note is not null) entry.Note = note;
        Log(entry);
        return result;
    }

    JsonNode Failed(ActivityEntry entry, string outcome, string note, IReadOnlyList<string>? route = null)
    {
        entry.Outcome = outcome;
        entry.Note = note;
        if (route is { Count: > 0 }) entry.Fallback = string.Join(" → ", route);
        Log(entry);
        return new JsonObject { ["ok"] = false };
    }

    void Log(ActivityEntry entry)
    {
        entry.At = entry.At == default ? Now : entry.At;
        _activity.Add(entry);
    }

    /// <summary>The notes of one call's route, kept with the position of each source in the chain.</summary>
    sealed class Route
    {
        readonly List<(int Index, string Text)> _items = [];

        public void Add(int index, string text) => _items.Add((index, text));

        public IReadOnlyList<string> All() => _items.OrderBy(i => i.Index).Select(i => i.Text).ToList();

        public IReadOnlyList<string> Before(int index) => _items.Where(i => i.Index < index).OrderBy(i => i.Index).Select(i => i.Text).ToList();
    }

    /// <summary>A text for logs and tests: "12.3 s".</summary>
    public static string Seconds(double s) => s.ToString("0.0", CultureInfo.InvariantCulture) + " s";
}
