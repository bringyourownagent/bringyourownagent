using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using BringYourOwnAgent;
using Byoa.Wallet.Activity;
using Byoa.Wallet.Audio;
using Byoa.Wallet.Protocol;
using Byoa.Wallet.Registry;
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
public sealed partial class WalletEngine
{
    /// <summary>How long a request may wait for the visitor (the page's promise resolves null after that).</summary>
    public static TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(120);

    /// <summary>How long a paid-fallback question waits for an answer.</summary>
    public static TimeSpan PaidQuestionTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>How long one source may take for one clip before the next one is tried.</summary>
    public static TimeSpan SourceTimeout { get; set; } = TimeSpan.FromSeconds(45);

    /// <summary>Tests only: how long one text source may take for one answer (the retry included). Null, the default: the call's own limit (<see cref="TextLimitSet.CallSeconds"/>, 300 s unless the visitor or the site set another).</summary>
    public static TimeSpan? TextSourceTimeout { get; set; }

    /// <summary>Tests only: how long the complete text route may take. Null, the default: the call's own limit.</summary>
    public static TimeSpan? TextRouteTimeout { get; set; }

    /// <summary>
    /// Allowing a site waits for the models to do their warm-up job (see <see cref="WalletSettings.WarmUpOnConnect"/>), but never
    /// indefinitely: a cold disk load of a big model can take a couple of minutes, a program that never answers must not hold a site.
    /// </summary>
    public static TimeSpan PreparationTimeout { get; set; } = TimeSpan.FromSeconds(180);

    /// <summary>
    /// A link's one-time code lives 120 seconds (CONTRACT-DIRECT.md): the wait for the models after the link's Allow ends this long
    /// after the link came, so that the wallet still connects with a valid code. The wait is never shorter than
    /// <see cref="MinPreparationWait"/>.
    /// </summary>
    public static TimeSpan LinkPreparationBudget { get; set; } = TimeSpan.FromSeconds(100);

    public static TimeSpan MinPreparationWait { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How long a text call waits for a busy source before it tries the next one.</summary>
    public static TimeSpan BusyWait { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Kept for source compatibility with callers from 0.1; preparation now waits for readiness or cancellation.</summary>
    public static TimeSpan WarmWait { get; set; } = TimeSpan.FromSeconds(30);

    const int MaxAudioBytes = 6 * 1024 * 1024;
    const int MaxManifestChars = 64 * 1024;

    /// <summary>
    /// A page may ask this many times a minute without a click (a request is made once per visit, so a real page asks once or twice). The rest are
    /// answered with nothing and not written down: a loop in a page must not turn into a settings write, an activity line and a
    /// window refresh for every iteration.
    /// </summary>
    public const int RequestsPerMinutePerOrigin = 30;

    readonly ConcurrentDictionary<string, RequestWindow> _requestWindows = new(StringComparer.Ordinal);

    sealed class RequestWindow
    {
        public DateTimeOffset Start;
        public int Count;
        public bool Told;
    }
    const int MaxTextChars = TextLimits.MaxAnswerChars;

    // Hard caps of a text.generate call, whatever the page asks for. The size of the input, the length of the answer, the wait
    // and the number of calls at once are the visitor's to set (see TextLimits); these are fixed by the protocol.
    const int MaxMessages = 128;
    const int MaxChoices = 20;
    const int MaxChoiceChars = 100;
    const int MaxChoiceCharsTotal = 500;
    const int DefaultMaxTokens = TextLimits.DefaultMaxTokens;
    const double DefaultTemperature = 0.7;
    const decimal MaxTextCost = 1000m;

    readonly SettingsStore _store;
    readonly IReadOnlyList<ISpeechSource> _sources;
    readonly IReadOnlyList<ITextSource> _textSources;
    readonly ActivityLog _activity;
    readonly IWalletUi _ui;
    readonly TimeProvider _time;
    readonly ConcurrentDictionary<string, GrantSession> _sessions = new(StringComparer.Ordinal);

    // A link's Allow, held in memory until the site's welcome echoes the link's manifest (keyed by the grant's id).
    readonly ConcurrentDictionary<string, LinkDecision> _linkDecisions = new(StringComparer.Ordinal);
    readonly Dictionary<string, SharedAsk> _asking = new(StringComparer.Ordinal);
    readonly object _askGate = new();

    // One call at a time per text source: a local runtime answers one request at a time.
    readonly ConcurrentDictionary<string, SemaphoreSlim> _busy = new(StringComparer.Ordinal);

    // Daily limits count calls and money that are still running, not only the ones already in the log.
    readonly Dictionary<string, (int Calls, decimal Money)> _inflight = new(StringComparer.Ordinal);
    readonly object _dailyGate = new();
    readonly SemaphoreSlim _prompt = new(1, 1);

    public WalletEngine(SettingsStore store, IReadOnlyList<ISpeechSource> sources, ActivityLog activity, IWalletUi ui,
        TimeProvider? time = null, IReadOnlyList<ITextSource>? textSources = null)
    {
        _store = store;
        _sources = sources;
        _textSources = textSources ?? [];
        _activity = activity;
        _ui = ui;
        _time = time ?? TimeProvider.System;
        _store.Update(Router.EnsureDefaults);
    }

    public IReadOnlyList<ISpeechSource> Sources => _sources;
    public IReadOnlyList<ITextSource> TextSources => _textSources;
    public SettingsStore Store => _store;
    public ActivityLog Activity => _activity;

    const string TestOrigin = "wallet";

    /// <summary>Raised when the sites or the quiet badge change.</summary>
    public event Action? SitesChanged;

    DateTimeOffset Now => _time.GetUtcNow();

    /// <summary>Answers one message from the extension. Never throws for bad input: it answers the fallback.</summary>
    public Task<JsonNode?> HandleAsync(WireMessage message, CancellationToken ct = default) =>
        GuardedAsync(message.Type, message.Origin, null, async () => message.Type switch
        {
            Wire.Request => await RequestAsync(message.Origin, message.UserActivation, message.Payload, ct, null).ConfigureAwait(false),
            Wire.Call => await CallAsync(message.Origin, message.Payload, ct, null).ConfigureAwait(false),
            Wire.Release => Release(message.Origin, message.Payload),
            Wire.Status => Status(),
            Wire.Open => OpenWindow(),
            _ => null,
        });

    /// <summary>The value of <see cref="ActivityEntry.Via"/> for the direct transport.</summary>
    public const string ViaLink = "link";

    /// <summary>
    /// The direct transport (a <c>bringyourownagent://</c> link): a request for a site whose manifest the wallet
    /// fetched itself. The same rules as a page's request (blocked, the notice and the consent, the difference, models
    /// ready before the answer), except that a link always asks, even for a site with "decide on my own"; the answer
    /// is a grant or null.
    /// </summary>
    public Task<JsonNode?> RequestViaLinkAsync(string origin, JsonObject manifest, bool userActivation, CancellationToken ct = default) =>
        GuardedAsync(Wire.Request, origin, ViaLink, () => RequestAsync(origin, userActivation, new JsonObject { ["manifest"] = manifest.DeepClone() }, ct, ViaLink));

    /// <summary>
    /// A reconnect with a stored pairing (no link, no click): the grant of a site that keeps "decide on my own", silently
    /// or not at all. Nothing is shown; a manifest that is not covered is held quietly, as for any request without a click.
    /// </summary>
    public Task<JsonNode?> RenewViaLinkAsync(string origin, JsonObject manifest, CancellationToken ct = default) =>
        GuardedAsync(Wire.Request, origin, ViaLink, () => RequestAsync(origin, false, new JsonObject { ["manifest"] = manifest.DeepClone() }, ct, ViaLink, renewal: true));

    /// <summary>A call that came over the wallet's own connection to the site: exactly the gate, limits and routing of a page's call.</summary>
    /// <param name="onChunk">When the call asked to stream and the source can: gets each piece of the answer as it arrives (the answer that comes back is the whole text).</param>
    public Task<JsonNode?> CallViaLinkAsync(string origin, JsonObject payload, CancellationToken ct = default, Action<string>? onChunk = null) =>
        GuardedAsync(Wire.Call, origin, ViaLink, () => CallAsync(origin, payload, ct, ViaLink, onChunk));

    /// <summary>
    /// The site's welcome echoed the link's manifest, so the site issued the link: the visitor's Allow for it is stored
    /// now. False when no answer waits under this grant any more: the visitor decided about the site meanwhile, so
    /// nothing is stored and the link's connection must end.
    /// </summary>
    public bool ConfirmLinkDecision(string grantId)
    {
        // The answer is taken under the store's lock, with its write: a visitor's decision is either before this (it has
        // dropped the answer) or after it (and overwrites what was stored).
        var stored = false;
        _store.Update(s =>
        {
            if (!_linkDecisions.TryRemove(grantId, out var decision)) return;
            decision.Apply(Site(s, decision.Origin));
            stored = true;
        });
        if (stored) SitesChanged?.Invoke();
        return stored;
    }

    /// <summary>
    /// A link's connection ended before the site's welcome echoed the link's manifest (an error, a timeout, another
    /// manifest, the wallet closing): the visitor's answer to that link is dropped, and its grant ends with it.
    /// </summary>
    public void DropLinkDecision(string grantId)
    {
        _linkDecisions.TryRemove(grantId, out _);
        _sessions.TryRemove(grantId, out _);
    }

    /// <summary>True while a link's answer with this grant waits for the site's welcome.</summary>
    public bool HoldsLinkDecision(string grantId) => _linkDecisions.ContainsKey(grantId);

    async Task<JsonNode?> GuardedAsync(string type, string origin, string? via, Func<Task<JsonNode?>> work)
    {
        try
        {
            return await work().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Wire.Fallback(type);
        }
        catch (Exception e)
        {
            // Whatever went wrong, the page hears the same "unavailable" as for any other no.
            Log(new ActivityEntry { Origin = origin, Kind = type, Via = via, Outcome = "invalid", Note = e.GetType().Name });
            return Wire.Fallback(type);
        }
    }

    /// <summary>The most calls of this grant's site in flight at once now: the visitor's limit, held back by what the site declared.</summary>
    public int ParallelCalls(GrantSession grant) =>
        _store.Read(s => TextLimits.Effective(s.Limits, s.Site(grant.Origin), grant.Manifest.Limits).ParallelCalls);

    /// <summary>A live grant by its id, or null.</summary>
    public GrantSession? FindGrant(string grantId) =>
        _sessions.TryGetValue(grantId, out var s) && s.ExpiresAt > Now ? s : null;

    /// <summary>A copy of what the wallet knows about a site, or null.</summary>
    public SiteRecord? FindSite(string origin) => _store.Read(s => s.Site(origin) is { } r ? Copy(r) : null);

    /// <summary>
    /// True when a manifest asks for nothing beyond what the site is allowed: a valid manifest, the site allowed, no new
    /// skill, no required skill the wallet cannot serve, and an expected cost within the visitor's budget.
    /// </summary>
    public bool CoveredByGrant(string origin, JsonObject manifestNode)
    {
        var text = manifestNode.ToJsonString();
        if (text.Length > MaxManifestChars || !Manifest.Validate(text).Ok) return false;
        var site = FindSite(origin);
        if (site is not { Status: SiteStatus.Allowed }) return false;
        var requested = RequestedManifest.From(manifestNode);
        var diff = Difference(site, requested);
        var settings = _store.Snapshot();
        return diff.NewSkills.Count == 0 && diff.MissingRequired.Count == 0 && !diff.CostRaised
            && !ModelsNeedAsking(site, requested, settings) && !LimitsRaised(site, requested, settings.Limits);
    }

    /// <summary>Adds an entry to the activity log (the direct transport's own events).</summary>
    public void Record(ActivityEntry entry) => Log(entry);

    /// <summary>
    /// Tests one source with the wallet's short built-in sample. The call uses the normal gate, daily holds and
    /// routing code, but it creates no site record, consent window or page grant. A remote caller cannot authorize a
    /// paid source; the desktop Test button calls the overload that marks a visitor action.
    /// </summary>
    public Task<JsonNode?> TestSourceAsync(string sourceId, string skill, CancellationToken ct = default) =>
        TestSourceAsync(sourceId, skill, visitorInitiated: false, ct);

    /// <summary>Runs a source test after the visitor explicitly pressed Test in the wallet window.</summary>
    public async Task<JsonNode?> TestSourceAsync(string sourceId, string skill, bool visitorInitiated, CancellationToken ct = default)
    {
        if (skill is not (Vocabulary.Transcribe or Vocabulary.Generate))
            return TestFailure(skill, "the wallet does not serve that skill");

        if (skill == Vocabulary.Transcribe)
        {
            var source = _sources.FirstOrDefault(s => s.Id == sourceId);
            if (source is null) return TestFailure(skill, "there is no speech source with that id");
            return await TestSpeechSourceAsync(source, visitorInitiated, ct).ConfigureAwait(false);
        }

        var text = _textSources.FirstOrDefault(s => s.Id == sourceId);
        if (text is null) return TestFailure(skill, "there is no text source with that id");
        return await TestTextSourceAsync(text, visitorInitiated, ct).ConfigureAwait(false);
    }

    /// <summary>Tests a not-yet-saved speech source (a model in a program on this computer) materialised by the provider set.</summary>
    public Task<JsonNode?> TestSourceAsync(ISpeechSource source, string skill, bool visitorInitiated, CancellationToken ct = default)
    {
        if (skill != Vocabulary.Transcribe) return Task.FromResult<JsonNode?>(TestFailure(skill, "a draft speech source can only serve audio.transcribe"));
        return TestSpeechSourceAsync(source, visitorInitiated, ct);
    }

    /// <summary>Tests a not-yet-saved text source materialised by the provider set.</summary>
    public Task<JsonNode?> TestSourceAsync(ITextSource source, string skill, bool visitorInitiated, CancellationToken ct = default)
    {
        if (skill != Vocabulary.Generate) return Task.FromResult<JsonNode?>(TestFailure(skill, "a draft source can only serve text.generate"));
        return TestTextSourceAsync(source, visitorInitiated, ct);
    }

    async Task<JsonNode?> TestSpeechSourceAsync(ISpeechSource source, bool visitorInitiated, CancellationToken ct)
    {
        if (source.Paid && !visitorInitiated)
            return TestFailure(Vocabulary.Transcribe, "this is a paid source; press Test in the wallet before making a paid test call");

        var manifest = TestManifest(Vocabulary.Transcribe, maxClipSeconds: 5);
        var requested = RequestedManifest.From(manifest);
        var session = new GrantSession(TestOrigin, requested, [Vocabulary.Transcribe], 1.00m, Now);
        var site = TestSite(visitorInitiated);
        var limits = TestLimits();
        var hold = new DailyHold(TestOrigin);
        if (!HoldCall(hold, limits.CallsPerDay)) return TestFailure(Vocabulary.Transcribe, "the wallet's daily call limit refused the test");
        try
        {
            var clip = new WavClip(TestSample(), Wav.TargetRate, 1, 32);
            var chain = TestChain(Vocabulary.Transcribe, source.Id);
            // A program that has to load its model first (a minute, on a cold disk) must not fail the visitor's Test: the
            // model is loaded before the sample goes in. A refusal here is reported by the test call that follows.
            if (source is IWarmable warmable && !source.Paid)
            {
                try { await warmable.WarmAsync(source.Languages?.FirstOrDefault() ?? "ru").WaitAsync(ct).ConfigureAwait(false); }
                catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException) { }
            }

            var entry = new ActivityEntry
            {
                Origin = TestOrigin, Kind = "test", Via = "wallet", Skill = Vocabulary.Transcribe,
                Note = "wallet-owned source test",
            };
            var result = await RouteAsync(entry, session, site, Vocabulary.Transcribe, clip, clip.Seconds, source.Languages?.FirstOrDefault() ?? "ru", limits.SitePerDay, limits.WalletPerDay, hold,
                Stopwatch.GetTimestamp(), ct, chain, [source]).ConfigureAwait(false);
            return WithReason(result, entry);
        }
        finally
        {
            ReleaseDaily(hold);
        }
    }

    async Task<JsonNode?> TestTextSourceAsync(ITextSource source, bool visitorInitiated, CancellationToken ct)
    {
        if (source.Paid && !visitorInitiated)
            return TestFailure(Vocabulary.Generate, "this is a paid source; press Test in the wallet before making a paid test call");

        var manifest = TestManifest(Vocabulary.Generate, maxTextChars: 4000);
        var requested = RequestedManifest.From(manifest);
        var session = new GrantSession(TestOrigin, requested, [Vocabulary.Generate], 1.00m, Now);
        var site = TestSite(visitorInitiated);
        var limits = TestLimits();
        var hold = new DailyHold(TestOrigin);
        if (!HoldCall(hold, limits.CallsPerDay)) return TestFailure(Vocabulary.Generate, "the wallet's daily call limit refused the test");
        try
        {
            var entry = new ActivityEntry
            {
                Origin = TestOrigin, Kind = "test", Via = "wallet", Skill = Vocabulary.Generate,
                Note = "wallet-owned source test",
            };
            var result = await RouteTextAsync(entry, session, site,
                [new ChatMessage("user", "Reply with exactly: wallet test ok")], 34, 256, 0, null, null,
                limits.CallsPerDay, limits.SitePerDay, limits.WalletPerDay, hold, Stopwatch.GetTimestamp(), ct,
                TestChain(Vocabulary.Generate, source.Id), [source]).ConfigureAwait(false);
            return WithReason(result, entry);
        }
        finally
        {
            ReleaseDaily(hold);
        }
    }

    /// <summary>A failed test says why, in the source's own short words ("Llama answered HTTP 400: audio input is not supported").</summary>
    static JsonNode? WithReason(JsonNode? result, ActivityEntry entry)
    {
        if (result is JsonObject failed && failed["ok"]?.GetValue<bool>() != true && failed["error"] is null && !string.IsNullOrWhiteSpace(entry.Fallback))
            failed["error"] = entry.Fallback;
        return result;
    }

    static JsonObject TestManifest(string skill, int? maxClipSeconds = null, int? maxTextChars = null)
    {
        var limits = new JsonObject { ["maxCallsPerMinute"] = 1 };
        if (maxClipSeconds is { } clip) limits["maxClipSeconds"] = clip;
        if (maxTextChars is { } text) limits["maxTextChars"] = text;
        return new JsonObject
        {
            ["byoa"] = "0.1-draft",
            ["purpose"] = new JsonObject { ["summary"] = "Wallet source test", ["limits"] = limits },
            ["skills"] = new JsonObject { [skill] = new JsonObject { ["required"] = true } },
            ["privacy"] = "personal-voice",
        };
    }

    static SiteRecord TestSite(bool visitorInitiated) => new()
    {
        Origin = TestOrigin, Status = SiteStatus.Allowed, PaidAllowed = visitorInitiated,
        Budget = "1.00 USD", PerDay = "1.00 USD", CallsPerDay = int.MaxValue,
    };

    (int CallsPerDay, decimal SitePerDay, decimal WalletPerDay) TestLimits() => _store.Read(s => (
        s.Limits.SiteCallsPerDay,
        Money.Parse(s.Limits.SitePerDay, 0.20m),
        Money.Parse(s.Limits.WalletPerDay, 1.00m)));

    static SkillChain TestChain(string skill, string sourceId) => new()
    {
        Skill = skill,
        Stops = [new ChainStop { SourceId = sourceId, AskBeforePaid = false }],
    };

    JsonNode TestFailure(string skill, string note)
    {
        Log(new ActivityEntry { Origin = TestOrigin, Kind = "test", Via = "wallet", Skill = skill, Outcome = "failed", Note = note });
        return new JsonObject { ["ok"] = false, ["error"] = note };
    }

    static float[] TestSample()
    {
        var samples = new float[1600];
        for (var i = 0; i < samples.Length; i++) samples[i] = 0.03f * MathF.Sin(i * 0.05f);
        return samples;
    }

    /// <summary>
    /// Raised with the origin when a site is revoked, blocked, paused (by the visitor or for being unused) or declined over
    /// its grant: its connections close and its pairing is forgotten. A held link answer that a decision drops is not this
    /// event; see <see cref="LinkAnswersDropped"/>.
    /// </summary>
    public event Action<string>? SiteClosed;

    /// <summary>
    /// Raised with the grant ids of a site's link answers that a newer decision dropped before their welcomes: only the link
    /// connections that wait for those answers end, the site's stored grant and pairing stay.
    /// </summary>
    public event Action<IReadOnlyList<string>>? LinkAnswersDropped;

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

    async Task<JsonNode?> RequestAsync(string origin, bool userActivation, JsonObject payload, CancellationToken ct, string? via, bool renewal = false)
    {
        var entry = new ActivityEntry { Origin = origin, Kind = Wire.Request, Via = via, Note = userActivation ? "after a click" : "without a click" };
        if (!Origins.TryParse(origin, out var host, out var secure)) return Done(entry, "invalid", "origin not allowed");
        // Only a page's request without a click counts here: a request with a click needs a person and opens a window, a link has its own
        // flood limit (the transport), and a renewal comes from the wallet itself.
        if (via is null && !renewal && !userActivation && !WithinRequestRate(origin, out var tell))
            return tell ? Done(entry, "limit", "more than " + RequestsPerMinutePerOrigin + " requests in a minute: the others are not written down") : null;
        if (payload["manifest"] is not JsonObject manifestNode) return Done(entry, "invalid", "no manifest");
        var manifestText = manifestNode.ToJsonString();
        if (manifestText.Length > MaxManifestChars) return Done(entry, "invalid", "manifest too large");
        var validation = Manifest.Validate(manifestText);
        if (!validation.Ok) return Done(entry, "invalid", "manifest: " + string.Join(", ", validation.Errors.Select(e => e.Code).Distinct()));
        var manifest = RequestedManifest.From(manifestNode);

        PauseIfDue(origin);                             // before this request counts as use: a site unused too long is paused first
        var site = _store.Read(s => s.Site(origin) is { } r ? Copy(r) : null) ?? new SiteRecord { Origin = origin };
        // Written at most once a minute: "last used" is counted in hours, and every write is the whole settings file.
        if (site.LastRequest is not { } lastRequest || Now - lastRequest >= TimeSpan.FromMinutes(1))
            _store.Update(s => Site(s, origin).LastRequest = Now);

        if (site.Status == SiteStatus.Blocked) return Done(entry, "blocked", "blocked site: no notice");
        // A declined origin is intentionally indistinguishable from an absent wallet. Do not run the prompt
        // queue again until a later visit changes the site's state through the normal visitor flow.
        if (site.Status == SiteStatus.Declined) return Done(entry, "declined", "declined site: no notice");

        var diff = Difference(site, manifest);
        var snapshot = _store.Snapshot();
        var covered = site.Status == SiteStatus.Allowed && diff.NewSkills.Count == 0 && diff.MissingRequired.Count == 0 && !diff.CostRaised
            && !ModelsNeedAsking(site, manifest, snapshot) && !LimitsRaised(site, manifest, snapshot.Limits);
        // "Decide on my own" grants a page's request and a reconnect with the stored pairing. A new link always asks: its
        // welcome replaces the stored pairing, so the visitor sees it first.
        if (covered && site.AutoDecide && !site.Paused && (via is null || renewal))
        {
            var auto = Grant(origin, manifest, site.Skills, Money.Parse(site.Budget, 0.20m));
            if (auto is null) return Done(entry, "invalid", "nothing to grant");
            // Silent, but not cold: the routed models load before the page gets its grant.
            var waited = await WarmBeforeResolveAsync(auto, host, showPreparing: false, connectBy: null, ct).ConfigureAwait(false);
            entry.Note = (userActivation ? "after a click" : "without a click") + "; decided on its own" + Ready(waited);
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

        // A second page request from the same origin while the first is still asking gets the same answer. The question
        // has its own token: it is cancelled only when every page waiting for it has gone, not when the first one has.
        // A link never shares: its answer belongs to its own manifest and connection, so each link asks on its own.
        SharedAsk ask;
        lock (_askGate)
        {
            // An answered question stays in the table until its continuation removes it: a request in that moment
            // asks anew instead of taking the old answer.
            if (via is not null || !_asking.TryGetValue(origin, out ask!) || ask.Answer.IsCompleted)
            {
                ask = new SharedAsk();
                if (via is null) _asking[origin] = ask;
                var shared = ask;
                ask.Answer = AskVisitorAsync(origin, host, secure, manifest, via, ask.Cancel.Token);
                ask.Answer.ContinueWith(_ =>
                {
                    lock (_askGate)
                    {
                        if (_asking.TryGetValue(origin, out var current) && ReferenceEquals(current, shared)) _asking.Remove(origin);
                    }
                }, TaskScheduler.Default);
            }

            ask.Waiters++;
        }

        var left = 0;
        using var registration = ct.Register(() => Leave(ask, ref left));
        try
        {
            return await ask.Answer.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            Leave(ask, ref left);
        }
    }

    /// <summary>True while the origin is within its requests for this minute. <paramref name="tell"/> is true once per minute, at the first refusal.</summary>
    bool WithinRequestRate(string origin, out bool tell)
    {
        var now = Now;
        if (_requestWindows.Count > 512)
        {
            foreach (var (key, old) in _requestWindows)
                if (now - old.Start >= TimeSpan.FromMinutes(1)) _requestWindows.TryRemove(key, out _);
        }

        var window = _requestWindows.GetOrAdd(origin, _ => new RequestWindow { Start = now });
        lock (window)
        {
            if (now - window.Start >= TimeSpan.FromMinutes(1))
            {
                window.Start = now;
                window.Count = 0;
                window.Told = false;
            }

            window.Count++;
            if (window.Count <= RequestsPerMinutePerOrigin)
            {
                tell = false;
                return true;
            }

            tell = !window.Told;
            window.Told = true;
            return false;
        }
    }

    /// <summary>One question to the visitor, shared by every request of an origin that arrives while it is open.</summary>
    sealed class SharedAsk
    {
        public CancellationTokenSource Cancel { get; } = new();
        public Task<JsonNode?> Answer { get; set; } = Task.FromResult<JsonNode?>(null);
        public int Waiters { get; set; }
    }

    /// <summary>A link's Allow: what it makes of the site's record, applied only when the site proves the link.</summary>
    sealed record LinkDecision(string Origin, Action<SiteRecord> Apply);

    void Leave(SharedAsk ask, ref int left)
    {
        if (Interlocked.Exchange(ref left, 1) == 1) return;
        lock (_askGate)
        {
            ask.Waiters--;
            if (ask.Waiters == 0 && !ask.Answer.IsCompleted) ask.Cancel.Cancel();
        }
    }

    async Task<JsonNode?> AskVisitorAsync(string origin, string host, bool secure, RequestedManifest manifest, string? via, CancellationToken ct)
    {
        await Task.Yield();
        var began = Now;
        var entry = new ActivityEntry { Origin = origin, Kind = Wire.Request, Via = via, Note = "after a click" };
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

        var released = false;
        try
        {
            var allowedWhenAsked = _store.Read(s => s.Site(origin)?.Status == SiteStatus.Allowed);
            var notice = await _ui.ShowNoticeAsync(new NoticeModel(origin, host, FromLink: via == ViaLink), timeout.Token).ConfigureAwait(false);
            switch (notice)
            {
                // From a link nothing is stored: the site can prove a link only by connecting, and a stranger's link must
                // not be able to switch a real site off. A link's notice has no block: "Block in Sites…" opens the Sites list.
                case NoticeAnswer.Block when via == ViaLink:
                case NoticeAnswer.Decline when via == ViaLink:
                    return Done(entry, "declined", "declined from a link: nothing stored");
                case NoticeAnswer.Block:
                    Block(origin, true);
                    return Done(entry, "blocked", "blocked from the notice");
                case NoticeAnswer.Decline:
                    MarkDeclined(origin, shownAllowed: allowedWhenAsked);
                    return Done(entry, "declined", "declined in the notice");
                case NoticeAnswer.Closed:
                    return Done(entry, "closed", timeout.IsCancellationRequested ? "no answer in time" : "notice closed");
            }

            // The site may have been blocked or changed from the settings while the notice was up.
            var site = _store.Read(s => s.Site(origin) is { } r ? Copy(r) : null) ?? new SiteRecord { Origin = origin };
            if (site.Status == SiteStatus.Blocked) return Done(entry, "blocked", "blocked while asking");
            var model = BuildConsent(origin, host, secure, manifest, site, via);
            WarmWhileAsking(manifest);              // the models start while the visitor reads: less to wait for after Allow
            var answer = await _ui.ShowConsentAsync(model, timeout.Token).ConfigureAwait(false);
            if (!answer.Allow || model.MissingRequired)
            {
                if (timeout.IsCancellationRequested) return Done(entry, "closed", "no answer in time");
                if (via == ViaLink) return Done(entry, "declined", "declined from a link: nothing stored");
                MarkDeclined(origin, shownAllowed: site.Status == SiteStatus.Allowed);
                return Done(entry, "declined", "declined in the consent window");
            }

            // The models he ticked for each skill that asked for several; a skill that cannot be given what it needs is left off, not half given.
            var chosenModels = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var unservedModels = new List<string>();
            foreach (var row in model.Rows.Where(r => r.Models is not null && r.CanServe && !answer.LeftOff.Contains(r.Skill)))
            {
                IReadOnlyList<string>? keys = null;
                if (answer.Models is not null) answer.Models.TryGetValue(row.Skill, out keys);
                if (ChooseModels(row.Models!, keys) is not { } picked)
                {
                    unservedModels.Add(row.Skill);
                    continue;
                }

                var ids = EnsureSources(picked);
                chosenModels[row.Skill] = picked.Select(k => ids[k]).ToList();
            }

            var offered = model.Rows.Where(r => r.CanServe && !answer.LeftOff.Contains(r.Skill) && !unservedModels.Contains(r.Skill)).Select(r => r.Skill).ToList();
            var leftOff = model.Rows.Where(r => !r.CanServe || answer.LeftOff.Contains(r.Skill) || unservedModels.Contains(r.Skill)).Select(r => r.Skill).ToList();
            var decided = Now;
            var wantedLimits = answer.UseSiteLimits ? model.Limits?.Wanted : null;

            // What the visitor's Allow makes of the site's record.
            void Decide(SiteRecord r)
            {
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
                r.DecidedAt = decided;
                r.Paused = false;                       // "Resume" in the consent window of a paused site
                r.PausedAt = null;
                r.PauseReason = null;
                foreach (var (skill, ids) in chosenModels)
                {
                    var entry = r.ModelsFor(skill);
                    if (entry is null)
                    {
                        entry = new SiteModels { Skill = skill };
                        r.Models.Add(entry);
                    }

                    entry.SourceIds = ids;
                }

                r.ShowModelNames = answer.ShowNames;
                if (wantedLimits is { } wanted)
                {
                    r.TextInputChars = wanted.InputChars ?? r.TextInputChars;
                    r.TextAnswerTokens = wanted.AnswerTokens ?? r.TextAnswerTokens;
                    r.TextCallSeconds = wanted.CallSeconds ?? r.TextCallSeconds;
                    r.ParallelCalls = wanted.ParallelCalls ?? r.ParallelCalls;
                    r.CallsPerMinute = wanted.CallsPerMinute ?? r.CallsPerMinute;
                }

                r.Asked = AskOf(manifest.Limits);       // what he has seen: the same ask is not put to him again
            }

            List<string> skills = [];
            if (via == ViaLink)
            {
                // A link's Allow is held in memory until the site's welcome echoes the link's manifest (ConfirmLinkDecision).
                // It is the visitor's newer decision: the answers of the site's other links that still wait are dropped.
                var replaced = DropLinkDecisions(origin);
                if (replaced.Count > 0) LinkAnswersDropped?.Invoke(replaced);
                Decide(site);
                skills = site.Skills;
            }
            else
            {
                IReadOnlyList<string> dropped = [];
                _store.Update(s =>
                {
                    var r = Site(s, origin);
                    Decide(r);
                    skills = r.Skills;
                    dropped = DropLinkDecisions(origin);
                });
                SitesChanged?.Invoke();
                if (dropped.Count > 0) LinkAnswersDropped?.Invoke(dropped);
            }

            var session = Grant(origin, manifest, skills, answer.Budget, via == ViaLink ? site : null);
            if (session is null) return Done(entry, "declined", "no skill left to grant");
            if (via == ViaLink) _linkDecisions[session.Id] = new LinkDecision(origin, Decide);
            _prompt.Release();
            released = true;
            try
            {
                // The page gets its grant once the routed models are loaded, with "Preparing your model…" meanwhile.
                var waited = await WarmBeforeResolveAsync(session, host, showPreparing: true, via == ViaLink ? began + LinkPreparationBudget : null, ct).ConfigureAwait(false);
                // The visitor may have decided about the site while the models loaded: then the link gets no grant.
                if (via == ViaLink && !_linkDecisions.ContainsKey(session.Id)) return Done(entry, "declined", "the visitor decided about the site while the models loaded");
                entry.Note = "after a click" + (model.PausedNote is not null ? "; resumed" : "") + (model.IsDifference ? "; only the difference was shown" : "")
                    + (answer.AutoDecide ? "; decide on my own from now on" : "") + (via == ViaLink ? "; kept once the site confirms the link" : "") + Ready(waited);
                return Done(entry, "allowed", null, session.ToResult());
            }
            catch
            {
                // Any other end of the wait hands out no grant: a link's answer then has no connection to take it, so it goes.
                if (via == ViaLink) DropLinkDecision(session.Id);
                throw;
            }
        }
        catch (OperationCanceledException)
        {
            return Done(entry, "closed", "no answer in time");
        }
        finally
        {
            if (!released) _prompt.Release();
        }
    }

    static string Ready(double? waited) =>
        waited is { } w && w >= 0.05 ? "; models ready after " + w.ToString("0.0", CultureInfo.InvariantCulture) + " s" : "";

    /// <summary>
    /// What a request asks beyond the site's grant: new skills the wallet can serve, required skills it cannot serve
    /// (never covered, so never granted silently), and an expected cost per session above the visitor's budget.
    /// </summary>
    static (List<string> NewSkills, List<string> MissingRequired, bool CostRaised) Difference(SiteRecord site, RequestedManifest manifest)
    {
        var missing = manifest.Skills.Where(s => s.Required && !Vocabulary.Served.Contains(s.Id)).Select(s => s.Id).ToList();
        if (site.Status != SiteStatus.Allowed) return (manifest.Skills.Select(s => s.Id).ToList(), missing, false);
        var newSkills = manifest.Skills.Where(s => !site.Skills.Contains(s.Id) && Vocabulary.Served.Contains(s.Id)).Select(s => s.Id).ToList();
        var costRaised = manifest.ExpectedPerSession is { } expected && expected > Money.Parse(site.Budget, 0);
        return (newSkills, missing, costRaised);
    }

    ConsentModel BuildConsent(string origin, string host, bool secure, RequestedManifest manifest, SiteRecord site, string? via)
    {
        var (newSkills, missingRequired, costRaised) = Difference(site, manifest);
        var settings = _store.Snapshot();
        var modelsAsked = ModelsNeedAsking(site, manifest, settings);
        var limitsAsk = BuildLimitsAsk(site, manifest, settings.Limits);
        var isDifference = site.Status == SiteStatus.Allowed && (newSkills.Count > 0 || missingRequired.Count > 0 || costRaised || modelsAsked || limitsAsk is not null);
        var rows = new List<ConsentRow>();
        foreach (var skill in manifest.Skills)
        {
            if (isDifference && !newSkills.Contains(skill.Id) && !missingRequired.Contains(skill.Id) && !(modelsAsked && skill.Models is not null)) continue;
            rows.Add(Row(skill, settings, site, isNew: isDifference && (newSkills.Contains(skill.Id) || missingRequired.Contains(skill.Id))));
        }

        var defaultBudget = Money.Parse(settings.Limits.DefaultSessionBudget, 0.20m);
        var current = Money.Parse(site.Budget, defaultBudget);
        decimal budget;
        decimal? before = null;
        if (site.Status == SiteStatus.Allowed)
        {
            // "Budget $0.05 -> $0.10": from the visitor's budget to the site's new expected cost, which is above it.
            budget = costRaised && manifest.ExpectedPerSession is { } raised ? raised : current;
            if (costRaised) before = current;
        }
        else
        {
            budget = manifest.ExpectedPerSession is { } expected && expected > 0 ? expected : defaultBudget;
        }

        return new ConsentModel(origin, host, secure, manifest.Summary, rows, budget, before, site.AutoDecide, isDifference,
            site.Status == SiteStatus.Allowed && site.Paused ? PauseText(site) : null, via == ViaLink, limitsAsk);
    }

    ConsentRow Row(RequestedSkill skill, WalletSettings settings, SiteRecord site, bool isNew)
    {
        var name = Vocabulary.SkillName(skill.Id);
        if (!Vocabulary.Served.Contains(skill.Id))
        {
            return new ConsentRow(skill.Id, name, skill.Required, isNew, false, [], skill.Languages,
                "None of your sources can do this yet: this wallet serves speech to text and text generation.");
        }

        if (skill.Id == Vocabulary.Generate) return TextRow(skill, name, settings, site, isNew);

        var languages = skill.Languages.Count > 0 ? skill.Languages.Cast<string?>().ToList() : [null];
        var routes = new List<RouteLine>();
        var whys = new List<string>();
        foreach (var language in languages)
        {
            var chain = ChainFor(settings, skill.Id, language);
            var plan = Router.Plan(chain, _sources, id => IsEnabled(settings, id), language);
            whys.Add(Router.Explain(plan, language));
            if (plan.First is { } first)
            {
                routes.Add(new RouteLine(language, first.Source.Name, Where(first.Source), first.Source.Paid, true,
                    SpeechRouteDetail(first.Source, language)));
            }
            else
            {
                routes.Add(new RouteLine(language, "no source", "", false, false));
            }
        }

        var canServe = routes.Count > 0 && routes.All(r => r.Fits);
        return new ConsentRow(skill.Id, name, skill.Required, isNew, canServe, routes, skill.Languages, string.Join(" ", whys.Distinct()));
    }

    ConsentRow TextRow(RequestedSkill skill, string name, WalletSettings settings, SiteRecord site, bool isNew)
    {
        // A site that asks for several models gets the models to tick instead of one route line per language.
        if (PlanModels(skill, settings, site) is { } section)
        {
            var why = section.MissingRequired.Count > 0
                ? "The site cannot work without " + string.Join(", ", section.MissingRequired) + "."
                : section.Shortfall is { } shortfall ? "The site " + shortfall + ". Add models in Sources, or an OpenRouter key." : "";
            return new ConsentRow(skill.Id, name, skill.Required, isNew, section.CanServe, [], skill.Languages, why, section);
        }

        var textLanguages = skill.Languages.Count > 0 ? skill.Languages.Cast<string?>().ToList() : [null];
        var routes = new List<RouteLine>();
        var whys = new List<string>();
        foreach (var textLanguage in textLanguages)
        {
            var textChain = ChainFor(settings, skill.Id, textLanguage);
            var textPlan = Router.PlanText(textChain, _textSources, id => IsEnabled(settings, id), textLanguage);
            whys.Add(Router.ExplainText(textPlan));
            if (textPlan.First is { } textFirst)
            {
                routes.Add(new RouteLine(textLanguage, textFirst.Source.Name, Where(textFirst.Source), textFirst.Source.Paid, true));
            }
            else
            {
                routes.Add(new RouteLine(textLanguage, "no source", "", false, false));
            }
        }

        var canServe = routes.Count > 0 && routes.All(r => r.Fits);
        return new ConsentRow(skill.Id, name, skill.Required, isNew, canServe, routes, skill.Languages, string.Join(" ", whys.Distinct()));
    }

    public static string Where(ISpeechSource s) => s.ProviderKind switch
    {
        "local-model" => "this PC",
        "home-server" => s.Runs,
        _ => "API key",
    };

    internal static string? SpeechRouteDetail(ISpeechSource source, string? language)
    {
        return source.HasMeasuredSeconds
            ? "about " + source.ApproxSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s a clip"
            : "speed not measured yet";
    }

    public static string Where(ITextSource s) => s.ProviderKind switch
    {
        "local-model" => "this PC",
        "home-server" => s.Runs,
        _ => "API key",
    };

    /// <param name="site">The site's record as the visitor's answer makes it, when it is not stored yet (a link's Allow is held until the site proves the link).</param>
    GrantSession? Grant(string origin, RequestedManifest manifest, IReadOnlyList<string> allowedSkills, decimal budget, SiteRecord? site = null)
    {
        var skills = manifest.Skills.Select(s => s.Id).Where(allowedSkills.Contains).Where(Vocabulary.Served.Contains).ToList();
        if (skills.Count == 0) return null;
        var session = new GrantSession(origin, manifest, skills, budget, Now);
        var settings = _store.Snapshot();
        site ??= settings.Site(origin);
        session.Models = BuildGranted(site, manifest, settings);
        session.ShowNames = ShowNamesFor(site, manifest);
        session.Limits = TextLimits.Effective(settings.Limits, site, manifest.Limits);
        _sessions[session.Id] = session;
        return session;
    }

    static IEnumerable<(string Skill, string? Language)> SkillLanguagesOf(GrantSession session) =>
        session.Manifest.Skills.Where(k => session.Skills.Contains(k.Id))
            .SelectMany(k => (k.Languages.Count > 0 ? k.Languages.Cast<string?>() : [null])
                .Select(language => (k.Id, language)));

    /// <summary>Whether the connection makes each model do its simplest job first (the setting is on by default).</summary>
    bool WarmUpEnabled => _store.Read(s => s.WarmUpOnConnect && !s.NeverStartModels);

    /// <summary>"Never start a model for me": the wallet sends nothing to a local model that is not in memory, so no runtime starts one for it.</summary>
    bool NeverStartModels => _store.Read(s => s.NeverStartModels);

    /// <summary>
    /// With "Never start a model for me" on, keeps out of a route every source whose runtime says its model is not in memory (asked just now with a
    /// request that loads nothing). A runtime that does not say what is in memory is not held back. Without the setting the steps stay as they are.
    /// </summary>
    async Task<List<T>> OnlyLoadedAsync<T>(IReadOnlyList<T> steps, Func<T, object> sourceOf, Action<object, string> skipped, CancellationToken ct)
    {
        if (!NeverStartModels) return [.. steps];
        var kept = new List<T>();
        foreach (var step in steps)
        {
            var source = sourceOf(step);
            if (source is ILoadCheck check && await check.LoadedNowAsync(ct).ConfigureAwait(false) == false)
            {
                skipped(source, "not loaded (you start the model yourself)");
                continue;
            }

            kept.Add(step);
        }

        return kept;
    }

    /// <summary>
    /// Makes the first local source of each of the grant's skills do its simplest job once for real and waits for it, showing
    /// "Preparing your model…" when it takes more than a moment. Returns the seconds waited, or null when nothing had to be waited
    /// for (or the option is off). <paramref name="connectBy"/> is the moment a link's code still allows the connection.
    /// </summary>
    async Task<double?> WarmBeforeResolveAsync(GrantSession session, string host, bool showPreparing, DateTimeOffset? connectBy, CancellationToken ct)
    {
        if (!WarmUpEnabled) return null;
        var (warming, names) = WarmCore(SkillLanguagesOf(session));
        if (warming.IsCompleted) return null;
        var watch = Stopwatch.StartNew();
        if (await Task.WhenAny(warming, Task.Delay(150, ct)).ConfigureAwait(false) != warming && !ct.IsCancellationRequested)
        {
            var limit = PreparationTimeout;
            if (connectBy is { } by)
            {
                var left = by - Now;
                limit = Min(limit, left < MinPreparationWait ? MinPreparationWait : left);
            }

            using var preparing = showPreparing ? _ui.ShowPreparing(new PreparingModel(session.Origin, host, names)) : null;
            using var preparation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (limit > TimeSpan.Zero) preparation.CancelAfter(limit);
            try
            {
                await warming.WaitAsync(preparation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Do not keep this request (or another site) hostage to a model that is still loading. The
                // background preparation continues; the eventual call will route to a ready fallback or say
                // "AI unavailable" rather than waiting again without a bound.
                _ui.ShowInfo(new InfoModel("Preparing your model", "It is taking longer than " + Math.Round(limit.TotalSeconds).ToString(CultureInfo.InvariantCulture)
                    + " seconds. The wallet will use a ready fallback or say AI unavailable."));
                ObservePreparation(warming);
            }
        }

        return watch.Elapsed.TotalSeconds;
    }

    /// <summary>The models of a manifest's skills start their warm-up while the visitor still reads the consent.</summary>
    void WarmWhileAsking(RequestedManifest manifest)
    {
        if (!WarmUpEnabled) return;
        var pairs = manifest.Skills.Where(k => Vocabulary.Served.Contains(k.Id))
            .SelectMany(k => (k.Languages.Count > 0 ? k.Languages.Cast<string?>() : [null]).Select(language => (k.Id, language)));
        ObservePreparation(WarmCore(pairs).Task);
    }

    readonly object _warmGate = new();
    readonly Dictionary<string, Task> _warming = new(StringComparer.Ordinal);

    /// <summary>One warm-up job per source at a time: a second caller (another language, the grant after the consent) shares the running one.</summary>
    Task WarmOnce(string sourceId, Func<Task> warm)
    {
        lock (_warmGate)
        {
            if (_warming.TryGetValue(sourceId, out var running)) return running;
            var task = Task.Run(warm);
            _warming[sourceId] = task;
            _ = task.ContinueWith(_ =>
            {
                lock (_warmGate)
                {
                    if (_warming.TryGetValue(sourceId, out var current) && ReferenceEquals(current, task)) _warming.Remove(sourceId);
                }
            }, TaskScheduler.Default);
            return task;
        }
    }

    static void ObservePreparation(Task warming)
    {
        _ = warming.ContinueWith(task => _ = task.Exception, TaskScheduler.Default);
    }

    /// <summary>
    /// Loads the first local source of each language's route in the background, so that a page's first call is fast.
    /// Called when a grant is given, and at start for the sites already allowed.
    /// </summary>
    public Task Warm(IEnumerable<string?> languages) => WarmCore(languages.Select(language => (Vocabulary.Transcribe, language))).Task;

    (Task Task, IReadOnlyList<string> Names) WarmCore(IEnumerable<(string Skill, string? Language)> skills)
    {
        var settings = _store.Snapshot();
        var tasks = new List<Task>();
        var names = new List<string>();
        foreach (var (skill, language) in skills.Distinct())
        {
            if (skill == Vocabulary.Generate)
            {
                var textSteps = Router.PlanText(ChainFor(settings, skill, language), _textSources,
                        id => IsEnabled(settings, id), language).Steps
                    .Where(s => !s.Source.Paid).ToList();
                if (textSteps.FirstOrDefault() is { } firstText) names.Add(firstText.Source.Name);
                tasks.Add(PrepareTextRouteAsync(textSteps, language));
                continue;
            }

            var steps = Router.Plan(ChainFor(settings, skill, language), _sources, id => IsEnabled(settings, id), language).Steps
                .Where(s => !s.Source.Paid).ToList();
            if (steps.FirstOrDefault() is { } first) names.Add(first.Source.Name);

            // Each language gets its own route task. A failed or unavailable stop falls through to the next local one.
            tasks.Add(PrepareRouteAsync(steps, language));
        }

        return (Task.WhenAll(tasks), names.Distinct(StringComparer.Ordinal).ToList());

        async Task PrepareRouteAsync(IReadOnlyList<RouteStep> steps, string? language)
        {
            foreach (var step in steps)
            {
                if (step.Source.State != SourceState.Ready) continue;
                if (step.Source is not IWarmable warmable) return;
                try
                {
                    await WarmOnce(step.Source.Id, () => warmable.WarmAsync(language)).ConfigureAwait(false);
                    if (step.Source.State == SourceState.Ready) return;
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    // The source reports a broken load; the next route stop gets a chance.
                }
            }
        }

        async Task PrepareTextRouteAsync(IReadOnlyList<TextRouteStep> steps, string? language)
        {
            foreach (var step in steps)
            {
                if (step.Source.State != SourceState.Ready) continue;
                if (step.Source is not ITextSourceWarmable warmable) return;
                try
                {
                    await WarmOnce(step.Source.Id, () => warmable.WarmAsync(language, CancellationToken.None)).ConfigureAwait(false);
                    if (step.Source.State == SourceState.Ready) return;
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    // A broken local model gives the next route stop a chance.
                }
            }
        }
    }

    /// <summary>At start: warm the models the already allowed sites will need.</summary>
    public void WarmAllowedSites()
    {
        if (!WarmUpEnabled) return;
        var languages = _store.Read(s => s.Sites.Where(x => x.Status == SiteStatus.Allowed)
            .SelectMany(x => x.Languages.Count > 0 ? x.Languages.Cast<string?>() : [null]).Distinct().ToList());
        if (languages.Count > 0)
        {
            _ = Warm(languages).ContinueWith(task =>
            {
                // Reading Exception marks a fire-and-forget warm task observed. A later site request still retries
                // the route; startup warming must never become an unobserved task exception.
                _ = task.Exception;
            }, TaskScheduler.Default);
        }
    }

    /// <summary>
    /// The visitor declined a window about the site. A decline of a difference (the window was shown for a site that was
    /// allowed) keeps what the site has. Any other decline is the visitor's answer, also when a link's answer was confirmed
    /// while the window was open: then the site's grants end, as for a revoke.
    /// </summary>
    void MarkDeclined(string origin, bool shownAllowed)
    {
        var ends = false;
        IReadOnlyList<string> dropped = [];
        _store.Update(s =>
        {
            var r = Site(s, origin);
            ends = r.Status == SiteStatus.Allowed && !shownAllowed;
            if (r.Status != SiteStatus.Allowed || !shownAllowed) r.Status = SiteStatus.Declined;
            r.DecidedAt = Now;
            dropped = DropLinkDecisions(origin);
        });
        SitesChanged?.Invoke();
        if (ends)
        {
            DropSessions(origin);
            SiteClosed?.Invoke(origin);
        }
        else if (dropped.Count > 0)
        {
            LinkAnswersDropped?.Invoke(dropped);
        }
    }

    // ------------------------------------------------------------------------------------------------
    // call

    async Task<JsonNode?> CallAsync(string origin, JsonObject payload, CancellationToken ct, string? via, Action<string>? onChunk = null)
    {
        var started = Stopwatch.GetTimestamp();
        var grantId = Str(payload["grantId"]);
        var skill = Str(payload["skill"]);
        var entry = new ActivityEntry { Origin = origin, Kind = Wire.Call, Via = via, Skill = skill };

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

        if (PauseIfDue(origin))
        {
            _sessions.TryRemove(session.Id, out _);
            return Failed(entry, "paused", "the site is paused: its next request, after a click, asks to resume");
        }

        // The limits of this call: the visitor's (defaults, then his own for the site) tightened by what the site declared. The gate
        // sees them as the grant's limits, so the draft rules' "the stricter of the two wins" holds for them too.
        session.Limits = _store.Read(s => TextLimits.Effective(s.Limits, s.Site(origin) ?? site, session.Manifest.Limits));

        if (skill is not (Vocabulary.Transcribe or Vocabulary.Generate))
        {
            return Failed(entry, "failed", "skill not served: " + (skill ?? "none"));
        }

        if (skill == Vocabulary.Generate)
        {
            return await GenerateCallAsync(entry, session, site, payload, started, ct, onChunk).ConfigureAwait(false);
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
        var hold = new DailyHold(origin);
        if (!HoldCall(hold, limits.CallsPerDay))
        {
            TellLimit(session, site, "calls per day", $"{limits.CallsPerDay} calls a day for this site");
            return Failed(entry, "limit", "calls per day for this site");
        }

        try
        {
            return await RouteAsync(entry, session, site, skill, clip, seconds, language, limits.SitePerDay, limits.WalletPerDay, hold, started, ct).ConfigureAwait(false);
        }
        finally
        {
            ReleaseDaily(hold);
        }
    }

    async Task<JsonNode?> RouteAsync(
        ActivityEntry entry, GrantSession session, SiteRecord site, string skill, WavClip clip, double seconds, string? language,
        decimal sitePerDay, decimal walletPerDay, DailyHold hold, long started, CancellationToken ct,
        SkillChain? chainOverride = null, IReadOnlyList<ISpeechSource>? sourceOverride = null)
    {
        var origin = session.Origin;

        var settings = _store.Snapshot();
        var chain = chainOverride ?? ChainFor(settings, skill, language);
        var sources = sourceOverride ?? _sources;
        var plan = Router.Plan(chain, sources, id => IsEnabled(settings, id), language);
        int Index(ISpeechSource s) => chain.Stops.FindIndex(x => x.SourceId == s.Id);

        // The fallback route: sources before the one that answers that the visitor turned off or that failed.
        var route = new Route();
        foreach (var k in plan.Skipped.Where(k => k.Why == "turned off")) route.Add(Index(k.Source), k.Source.Name + ": turned off");
        var callParams = new JsonObject();
        if (language is not null) callParams["language"] = language;
        string? stopReason = null;

        var loadedSteps = await OnlyLoadedAsync(plan.Steps, s => s.Source, (s, why) => route.Add(Index((ISpeechSource)s), ((ISpeechSource)s).Name + ": " + why), ct).ConfigureAwait(false);
        foreach (var step in loadedSteps)
        {
            var source = step.Source;
            var estimate = source.EstimateCost(clip.Seconds);
            if (estimate > 0 && !HoldMoney(hold, estimate, sitePerDay, walletPerDay))
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
                var answer = await AskPaidAsync(session, site, source.Name, estimate, route.All(), ct).ConfigureAwait(false);
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
                session.Spent = Math.Max(0, session.Spent + result.Cost - estimate);
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
                    ["provider"] = ProviderReceipt(source.ProviderKind),
                },
            };
        }

        entry.Milliseconds = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        return stopReason is not null
            ? Failed(entry, "limit", stopReason, route.All())
            : Failed(entry, "failed", plan.Steps.Count == 0 ? "no source for this language" : "no source answered", route.All());
    }

    // ------------------------------------------------------------------------------------------------
    // call: text.generate

    async Task<JsonNode?> GenerateCallAsync(ActivityEntry entry, GrantSession session, SiteRecord site, JsonObject payload, long started, CancellationToken ct,
        Action<string>? onChunk = null)
    {
        // The input: the page's messages, measured here and never trusted from the page.
        if (payload["input"]?["messages"] is not JsonArray array)
        {
            return Failed(entry, "invalid", "no messages in the input");
        }

        var messages = new List<ChatMessage>();
        foreach (var node in array)
        {
            if (node is not JsonObject message) continue;
            if (Str(message["role"]) is not { } role) continue;
            if (role is not ("system" or "user" or "assistant"))
            {
                return Failed(entry, "invalid", "message role not allowed: " + role);
            }

            messages.Add(new ChatMessage(role, Str(message["content"]) ?? ""));
        }

        if (messages.Count == 0) return Failed(entry, "invalid", "no messages in the input");
        if (messages.Count > MaxMessages) return Failed(entry, "invalid", "too many messages: the wallet takes at most " + MaxMessages);
        var chars = messages.Sum(m => m.Content.Length);
        if (chars == 0) return Failed(entry, "invalid", "the messages are empty");
        var textLimits = session.Limits ?? TextLimits.Defaults;
        if (chars > textLimits.InputChars) return Failed(entry, "invalid", "text too long for the wallet: at most " + textLimits.InputChars + " characters");

        var parameters = payload["params"] as JsonObject ?? new JsonObject();
        var maxTokens = Math.Clamp(Int(parameters["maxTokens"]) ?? Math.Min(DefaultMaxTokens, textLimits.AnswerTokens), 1, textLimits.AnswerTokens);
        var temperature = Math.Clamp(Num(parameters["temperature"]) ?? DefaultTemperature, 0, 1);
        var thinking = ThinkingWanted(parameters["thinking"]);
        var language = Vocabulary.PrimaryLanguage(Str(parameters["language"]));
        var declared = session.Manifest.Skill(Vocabulary.Generate)?.Languages ?? [];
        language ??= declared.Count == 1 ? declared[0] : null;
        entry.Language = language;

        List<string>? choices = null;
        if (parameters["choices"] is JsonArray choiceNodes)
        {
            choices = [];
            foreach (var node in choiceNodes)
            {
                if (node is JsonValue v && v.TryGetValue<string>(out var choice)) choices.Add(choice);
            }

            if (choices.Count == 0) return Failed(entry, "invalid", "no usable choices in the params");
            if (choices.Count > MaxChoices) return Failed(entry, "invalid", "too many choices: at most " + MaxChoices);
            if (choices.Any(c => c.Length == 0 || c.Length > MaxChoiceChars))
            {
                return Failed(entry, "invalid", "a choice is empty or longer than " + MaxChoiceChars + " characters");
            }

            if (choices.Sum(c => c.Length) > MaxChoiceCharsTotal) return Failed(entry, "invalid", "the choices together are longer than " + MaxChoiceCharsTotal + " characters");
        }

        // A site that asked for models reaches only the ones it was given: the one it names, or in the visitor's order when it names none.
        SkillChain? chainOverride = null;
        IReadOnlyList<ITextSource>? sourceOverride = null;
        var named = false;
        Func<ITextSource, string?>? refOf = null;
        // (a list that is empty is still a site that asked: it gets no answer, never the wallet's own route)
        if (session.Models.TryGetValue(Vocabulary.Generate, out var granted))
        {
            var wanted = Str(parameters["model"]);
            var pick = wanted is null ? granted.ToList() : granted.Where(g => g.Ref == wanted).ToList();
            if (pick.Count == 0) return Failed(entry, "invalid", "no such model for this site: " + (wanted ?? "none"));
            chainOverride = new SkillChain
            {
                Skill = Vocabulary.Generate,
                Stops = pick.SelectMany(g => g.SourceIds).Distinct(StringComparer.Ordinal)
                    .Select(id => new ChainStop { SourceId = id, AskBeforePaid = false }).ToList(),
            };
            var roadIds = chainOverride.Stops.Select(s => s.SourceId).ToHashSet(StringComparer.Ordinal);
            sourceOverride = _textSources.Where(s => roadIds.Contains(s.Id)).ToList();
            named = wanted is not null;
            refOf = source => pick.FirstOrDefault(g => g.SourceIds.Contains(source.Id))?.Ref;
        }

        // The per-day limits of the wallet: the same for a text call as for a clip.
        var limits = _store.Read(s => (
            SitePerDay: Money.Parse(site.PerDay ?? s.Limits.SitePerDay, 0.20m),
            CallsPerDay: site.CallsPerDay ?? s.Limits.SiteCallsPerDay,
            WalletPerDay: Money.Parse(s.Limits.WalletPerDay, 1.00m)));
        var hold = new DailyHold(session.Origin);
        if (!HoldCall(hold, limits.CallsPerDay))
        {
            TellLimit(session, site, "calls per day", $"{limits.CallsPerDay} calls a day for this site");
            return Failed(entry, "limit", "calls per day for this site");
        }

        try
        {
            return await RouteTextAsync(entry, session, site, messages, chars, maxTokens, temperature, language, choices,
                limits.CallsPerDay, limits.SitePerDay, limits.WalletPerDay, hold, started, ct, chainOverride, sourceOverride, named, refOf, onChunk, thinking).ConfigureAwait(false);
        }
        finally
        {
            ReleaseDaily(hold);
        }
    }

    async Task<JsonNode?> RouteTextAsync(
        ActivityEntry entry, GrantSession session, SiteRecord site, IReadOnlyList<ChatMessage> messages, int chars,
        int maxTokens, double temperature, string? language, IReadOnlyList<string>? choices,
        int callsPerDay, decimal sitePerDay, decimal walletPerDay, DailyHold hold, long started, CancellationToken ct,
        SkillChain? chainOverride = null, IReadOnlyList<ITextSource>? sourceOverride = null, bool waitForBusy = false,
        Func<ITextSource, string?>? refOf = null, Action<string>? onChunk = null, bool thinking = true)
    {
        using var routeTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var routeBudget = TextRouteTimeout ?? TimeSpan.FromSeconds((session.Limits ?? TextLimits.Defaults).CallSeconds);
        var sourceTimeout = TextSourceTimeout ?? routeBudget;
        if (routeBudget > TimeSpan.Zero) routeTimeout.CancelAfter(routeBudget);
        var routeDeadline = routeBudget > TimeSpan.Zero
            ? Stopwatch.GetTimestamp() + (long)(routeBudget.TotalSeconds * Stopwatch.Frequency)
            : long.MaxValue;
        var routeCt = routeTimeout.Token;
        var settings = _store.Snapshot();
        var chain = chainOverride ?? ChainFor(settings, Vocabulary.Generate, language);
        var sources = sourceOverride ?? _textSources;
        var plan = Router.PlanText(chain, sources, id => IsEnabled(settings, id), language);
        int Index(ITextSource s) => chain.Stops.FindIndex(x => x.SourceId == s.Id);

        // The fallback route: sources before the one that answers that the visitor turned off or that failed.
        var route = new Route();
        foreach (var k in plan.Skipped.Where(k => k.Why == "turned off")) route.Add(Index(k.Source), k.Source.Name + ": turned off");

        // What the gate sees: every character the wallet sends the provider, an added instruction included.
        var request = new TextRequest(messages, maxTokens, temperature, language, choices, thinking);
        var textChars = chars + (choices is null ? 0 : Texts.ChoicesInstruction(choices).Length);
        var choiceMiss = false;
        string? stopReason = null;
        decimal totalCost = 0;

        try
        {
        var loadedSteps = await OnlyLoadedAsync(plan.Steps, s => s.Source, (s, why) => route.Add(Index((ITextSource)s), ((ITextSource)s).Name + ": " + why), routeCt).ConfigureAwait(false);
        foreach (var step in loadedSteps)
        {
            if (routeCt.IsCancellationRequested || (routeBudget > TimeSpan.Zero && Remaining(routeDeadline) <= TimeSpan.Zero)) break;
            var source = step.Source;

            if (entry.ProviderCalled && !HoldCall(hold, callsPerDay))
            {
                stopReason ??= "calls per day";
                route.Add(Index(source), source.Name + ": calls per day limit");
                break;
            }

            // One call at a time per local source (a runtime answers one request at a time), a few at once per paid one. A call
            // on a chain waits a moment for a busy source and then tries the next; a call for one named model has nothing to
            // fall back to and waits in line until its own deadline.
            var busy = _busy.GetOrAdd(source.Id, _ => source.Paid ? new SemaphoreSlim(4, 4) : new SemaphoreSlim(1, 1));
            var busyWait = waitForBusy
                ? routeBudget > TimeSpan.Zero ? Remaining(routeDeadline) : TimeSpan.FromHours(1)
                : routeBudget > TimeSpan.Zero ? Min(BusyWait, Remaining(routeDeadline)) : BusyWait;
            if (busyWait <= TimeSpan.Zero || !await busy.WaitAsync(busyWait, routeCt).ConfigureAwait(false))
            {
                if (entry.ProviderCalled) ReleaseHeldCall(hold);
                route.Add(Index(source), source.Name + ": busy (one call at a time)");
                continue;
            }

            decimal estimate = 0;
            DateTimeOffset at = default;
            Decision decision = Decision.Blocked;
            string codes = "";
            try
            {
                try { estimate = SafeEstimate(source.EstimateCost(request), source.Paid); }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    estimate = source.Paid ? FlatTextEstimate : 0;
                }
                if (estimate > 0 && !HoldMoney(hold, estimate, sitePerDay, walletPerDay))
                {
                    route.Add(Index(source), source.Name + ": over the daily limit");
                    stopReason ??= "money per day";
                    continue;
                }

                // The gate (spec/rules-0.1-draft.md) decides before the source runs; the slot and the estimate are reserved.
                at = Now;
                var call = new JsonObject
                {
                    ["skill"] = Vocabulary.Generate,
                    ["at"] = GrantSession.Time(at),
                    ["provider"] = source.ProviderKind,
                    ["estimatedCost"] = Money.ToSpec(estimate),
                    ["input"] = new JsonObject { ["clipSeconds"] = 0, ["textChars"] = textChars },
                    ["params"] = language is null ? new JsonObject() : new JsonObject { ["language"] = language },
                }.ToJsonString();

                await session.Gate.WaitAsync(routeCt).ConfigureAwait(false);
                CheckResult check;
                try
                {
                    check = Gate.CheckCall(session.Manifest.Json, session.GrantJson(), call, session.StateJson(at));
                    if (check.Decision is Decision.Allowed or Decision.Warn)
                    {
                        session.CallTimes.Add(at);
                        session.Spent = SafeAdd(session.Spent, estimate);
                    }
                }
                finally
                {
                    session.Gate.Release();
                }

                decision = check.Decision;
                codes = string.Join(", ", check.Reasons.Select(r => r.Code).Distinct());
                if (decision == Decision.Blocked)
                {
                    if (check.Reasons.All(r => r.Code == "not-local"))
                    {
                        route.Add(Index(source), source.Name + ": not local");
                        continue;
                    }

                    if (check.Reasons.Any(r => r.Code is "rate-exceeded" or "text-too-long" or "grant-expired"))
                    {
                        if (check.Reasons.Any(r => r.Code == "rate-exceeded")) TellLimit(session, site, "rate", "the site's calls per minute");
                        return Failed(entry, "limit", codes, route.All());
                    }

                    return Failed(entry, "failed", codes, route.All());
                }

                if (decision == Decision.Ask)
                {
                    // budget-exceeded: the wallet never spends past the budget without a new yes.
                    TellLimit(session, site, "budget", "the budget of " + Money.Show(session.Budget) + " per session");
                    route.Add(Index(source), source.Name + ": " + codes);
                    stopReason ??= "budget per session";
                    continue;
                }

                if (step.AskFirst && !site.PaidAllowed)
                {
                    // A choice miss gets one more provider attempt. Tell the visitor the maximum amount this
                    // request may reserve, while only reserving the second attempt if the first really misses.
                    var quoted = choices is null ? estimate : SafeAdd(estimate, estimate);
                    var answer = await AskPaidAsync(session, site, source.Name, quoted, route.All(), ct).ConfigureAwait(false);
                    if (answer == PaidAnswer.No)
                    {
                        await Unreserve(session, at, estimate).ConfigureAwait(false);
                        route.Add(Index(source), source.Name + ": you said no");
                        return Failed(entry, "refused", "paid fallback not allowed", route.All());
                    }

                    if (answer == PaidAnswer.Always)
                    {
                        _store.Update(s => Site(s, session.Origin).PaidAllowed = true);
                        site.PaidAllowed = true;
                    }
                }

                var ran = Stopwatch.GetTimestamp();
                var (result, miss, cost) = await AnswerAsync(session, source, request, choices, at, estimate, route, Index(source), hold,
                    sitePerDay, walletPerDay, callsPerDay, textChars, entry, routeCt, routeDeadline, ct, sourceTimeout, onChunk).ConfigureAwait(false);
                totalCost = SafeAdd(totalCost, cost);
                if (result is null)
                {
                    choiceMiss |= miss;
                    continue;
                }

                var text = result.Text.Trim();
                if (text.Length > MaxTextChars) text = text[..MaxTextChars];
                entry.Outcome = "ok";
                entry.Source = source.Name + (result.Detail is null ? "" : " · " + result.Detail);
                var before = route.Before(Index(source));
                entry.Fallback = before.Count > 0 ? string.Join(" → ", before) + " → " + source.Name : null;
                entry.Cost = Money.ToSpec(totalCost);
                entry.Milliseconds = (int)Stopwatch.GetElapsedTime(ran).TotalMilliseconds;
                entry.Note = decision == Decision.Warn ? "warn: " + codes : null;
                Log(entry);
                var receipt = new JsonObject
                {
                    ["cost"] = Money.ToReceipt(totalCost),
                    ["provider"] = ProviderReceipt(source),
                };
                if (refOf?.Invoke(source) is { } modelRef) receipt["model"] = modelRef;
                return new JsonObject
                {
                    ["ok"] = true,
                    ["output"] = new JsonObject { ["text"] = text },
                    ["receipt"] = receipt,
                };
            }
            finally
            {
                busy.Release();
            }
        }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            stopReason ??= "text route timed out";
        }

        entry.Milliseconds = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        entry.Cost = Money.ToSpec(totalCost);
        return stopReason is not null
            ? Failed(entry, "limit", stopReason, route.All())
            : Failed(entry, "failed",
                plan.Steps.Count == 0 ? "no source can do text.generate" : choiceMiss ? "no source answered with one of the choices" : "no source answered",
                route.All());
    }

    /// <summary>
    /// One source answers a text request: the wallet strips any hidden reasoning and, when the page gave choices,
    /// matches the answer against them (one retry with the instruction spelled out). A source that cannot answer
    /// returns null, with its route note added and its reserve given back.
    /// </summary>
    async Task<(TextAnswer? Answer, bool ChoiceMiss, decimal Cost)> AnswerAsync(
        GrantSession session, ITextSource source, TextRequest request, IReadOnlyList<string>? choices,
        DateTimeOffset at, decimal estimate, Route route, int index, DailyHold hold, decimal sitePerDay,
        decimal walletPerDay, int callsPerDay, int textChars, ActivityEntry entry, CancellationToken routeCt,
        long routeDeadline, CancellationToken callerCt, TimeSpan sourceTimeout, Action<string>? onChunk = null)
    {
        TextAttempt first;
        try
        {
            first = await InvokeTextAttemptAsync(source, request, routeCt, routeDeadline, callerCt, entry, sourceTimeout, onChunk).ConfigureAwait(false);
        }
        catch
        {
            await ReleaseTextReservationAsync(session, at, estimate, hold, removeCallTime: false).ConfigureAwait(false);
            throw;
        }
        var firstCost = await SettleTextAttemptAsync(session, source, at, estimate, first, hold).ConfigureAwait(false);
        if (first.Answer is null && (choices is null || !first.ResponseReceived))
        {
            route.Add(index, source.Name + ": " + (first.Note ?? "did not answer"));
            return (null, false, firstCost);
        }

        var answer = first.Answer;
        var text = answer is null ? "" : Texts.CleanAnswer(answer.Text);
        var answerCost = firstCost;
        if (choices is not null)
        {
            // A provider may report a paid response as a typed failure when it returned an empty or invalid
            // choice. It still gets the same one retry as a provider that returned a non-matching TextAnswer.
            var matched = answer is null ? null : Texts.MatchChoice(text, choices);
            if (matched is null)
            {
                var retryRequest = RetryRequest(request, choices);
                var retry = await ReserveTextRetryAsync(session, source, retryRequest, textChars, callsPerDay,
                    sitePerDay, walletPerDay, hold, route, index, routeCt, routeDeadline).ConfigureAwait(false);
                if (retry is null)
                {
                    route.Add(index, source.Name + ": retry was not affordable or allowed");
                    return (null, true, firstCost);
                }

                TextAttempt second;
                try
                {
                    second = await InvokeTextAttemptAsync(source, retryRequest, routeCt, routeDeadline, callerCt, entry, sourceTimeout).ConfigureAwait(false);
                }
                catch
                {
                    await ReleaseTextReservationAsync(session, retry.Value.At, retry.Value.Estimate, hold, removeCallTime: false).ConfigureAwait(false);
                    throw;
                }
                var secondCost = await SettleTextAttemptAsync(session, source, retry.Value.At, retry.Value.Estimate, second, hold).ConfigureAwait(false);
                var total = SafeAdd(firstCost, secondCost);
                if (second.Answer is null)
                {
                    route.Add(index, source.Name + ": " + (second.Note ?? "answered with something else"));
                    return (null, true, total);
                }

                matched = Texts.MatchChoice(Texts.CleanAnswer(second.Answer.Text), choices);
                answer = second.Answer with
                {
                    Text = matched ?? "",
                    Cost = total,
                    InputTokens = (answer?.InputTokens ?? 0) + second.Answer.InputTokens,
                    OutputTokens = (answer?.OutputTokens ?? 0) + second.Answer.OutputTokens,
                };
                answerCost = total;
                if (matched is null)
                {
                    route.Add(index, source.Name + ": answered with something else");
                    return (null, true, total);
                }

                text = matched;
            }
            else
            {
                text = matched;
                answer = answer! with { Text = matched, Cost = firstCost };
            }
        }
        else if (text.Length == 0)
        {
            route.Add(index, source.Name + ": answered nothing");
            return (null, false, firstCost);
        }

        return (answer! with { Text = text, Cost = answerCost }, false, answerCost);
    }

    async Task<TextAttempt> InvokeTextAttemptAsync(ITextSource source, TextRequest request, CancellationToken routeCt,
        long routeDeadline, CancellationToken callerCt, ActivityEntry entry, TimeSpan sourceTimeout, Action<string>? onChunk = null)
    {
        entry.ProviderCalled = true;
        entry.ProviderCalls++;
        request = WithSourceThinking(source, request);
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(routeCt);
            if (sourceTimeout > TimeSpan.Zero) limit.CancelAfter(Min(sourceTimeout, Remaining(routeDeadline)));
            // A streamed answer is a preview for the site; what comes back is still the whole text, and the one the site keeps.
            var answer = onChunk is not null && request.Choices is null && source is IStreamingTextSource streaming
                ? await streaming.GenerateStreamAsync(request, onChunk, limit.Token).ConfigureAwait(false)
                : await source.GenerateAsync(request, limit.Token).ConfigureAwait(false);
            return new TextAttempt(answer, answer.Cost, true, true, null);
        }
        catch (TextSourceException e)
        {
            return new TextAttempt(null, e.Cost, e.ProviderCalled, e.ResponseReceived, e.Message);
        }
        catch (OperationCanceledException) when (!callerCt.IsCancellationRequested)
        {
            return new TextAttempt(null, null, true, false, "took too long");
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return new TextAttempt(null, null, true, false, Short(e));
        }
    }

    async Task<decimal> SettleTextAttemptAsync(GrantSession session, ITextSource source, DateTimeOffset at,
        decimal estimate, TextAttempt attempt, DailyHold hold)
    {
        if (!attempt.ProviderCalled)
        {
            await ReleaseTextReservationAsync(session, at, estimate, hold, removeCallTime: true).ConfigureAwait(false);
            return 0;
        }

        if (attempt.Cost is not { } reported && !attempt.ResponseReceived)
        {
            // A connection failure before a billable response is known: return only money, retain the rate timestamp.
            await ReleaseTextReservationAsync(session, at, estimate, hold, removeCallTime: false).ConfigureAwait(false);
            return 0;
        }

        var cost = NormalizeTextCost(source.Paid, attempt.Cost ?? estimate, estimate);
        await session.Gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try { session.Spent = SafeAdd(session.Spent, cost - estimate); }
        finally { session.Gate.Release(); }
        AdjustHeldMoney(hold, cost - estimate);
        return cost;
    }

    async Task<TextReservation?> ReserveTextRetryAsync(GrantSession session, ITextSource source, TextRequest request,
        int textChars, int callsPerDay, decimal sitePerDay, decimal walletPerDay, DailyHold hold, Route route, int index,
        CancellationToken ct, long routeDeadline)
    {
        if (ct.IsCancellationRequested || Remaining(routeDeadline) <= TimeSpan.Zero || !HoldCall(hold, callsPerDay)) return null;
        decimal estimate;
        try { estimate = SafeEstimate(source.EstimateCost(request), source.Paid); }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            ReleaseHeldCall(hold);
            return null;
        }

        if (estimate > 0 && !HoldMoney(hold, estimate, sitePerDay, walletPerDay))
        {
            ReleaseHeldCall(hold);
            return null;
        }

        var at = Now;
        var call = TextCallJson(source, request.Language, textChars, estimate, at);
        CheckResult check;
        try
        {
            await session.Gate.WaitAsync(ct).ConfigureAwait(false);
            try { check = Gate.CheckCall(session.Manifest.Json, session.GrantJson(), call, session.StateJson(at)); }
            finally { session.Gate.Release(); }
        }
        catch
        {
            ReleaseHeldMoney(hold, estimate);
            ReleaseHeldCall(hold);
            throw;
        }

        if (check.Decision is not (Decision.Allowed or Decision.Warn))
        {
            route.Add(index, source.Name + ": retry blocked (" + string.Join(", ", check.Reasons.Select(r => r.Code).Distinct()) + ")");
            ReleaseHeldMoney(hold, estimate);
            ReleaseHeldCall(hold);
            return null;
        }

        await session.Gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            session.CallTimes.Add(at);
            session.Spent = SafeAdd(session.Spent, estimate);
        }
        finally { session.Gate.Release(); }
        return new TextReservation(at, estimate);
    }

    readonly record struct TextAttempt(TextAnswer? Answer, decimal? Cost, bool ProviderCalled, bool ResponseReceived, string? Note);
    readonly record struct TextReservation(DateTimeOffset At, decimal Estimate);

    static string TextCallJson(ITextSource source, string? language, int textChars, decimal estimate, DateTimeOffset at) =>
        new JsonObject
        {
            ["skill"] = Vocabulary.Generate,
            ["at"] = GrantSession.Time(at),
            ["provider"] = source.ProviderKind,
            ["estimatedCost"] = Money.ToSpec(estimate),
            ["input"] = new JsonObject { ["clipSeconds"] = 0, ["textChars"] = textChars },
            ["params"] = language is null ? new JsonObject() : new JsonObject { ["language"] = language },
        }.ToJsonString();

    async Task ReleaseTextReservationAsync(GrantSession session, DateTimeOffset at, decimal estimate, DailyHold hold,
        bool removeCallTime)
    {
        await session.Gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (removeCallTime) session.CallTimes.Remove(at);
            session.Spent = Math.Max(0, session.Spent - estimate);
        }
        finally { session.Gate.Release(); }
        ReleaseHeldMoney(hold, estimate);
    }

    const decimal FlatTextEstimate = 0.02m;

    static decimal SafeEstimate(decimal value, bool paid) => value is < 0 or > MaxTextCost ? paid ? FlatTextEstimate : 0 : value;

    static decimal NormalizeTextCost(bool paid, decimal reported, decimal estimate)
    {
        if (!paid) return 0;
        // A paid provider's zero/negative/absurd report is not evidence that the call was free. The
        // reservation is the conservative floor when billing details are absent or unusable.
        var safe = reported > 0 ? Math.Min(MaxTextCost, reported) : estimate;
        return Math.Clamp(safe, 0, MaxTextCost);
    }

    static decimal SafeAdd(decimal left, decimal right)
    {
        try { return Math.Clamp(left + right, 0, MaxTextCost); }
        catch (OverflowException) { return MaxTextCost; }
    }

    static TimeSpan Remaining(long deadline)
    {
        if (deadline == long.MaxValue) return TimeSpan.FromHours(1);
        var ticks = deadline - Stopwatch.GetTimestamp();
        return ticks <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds((double)ticks / Stopwatch.Frequency);
    }

    static TimeSpan Min(TimeSpan left, TimeSpan right) => left <= right ? left : right;

    static string ProviderReceipt(ITextSource source) => ProviderReceipt(source.ProviderKind);

    static string ProviderReceipt(string providerKind) => providerKind switch
    {
        "local-model" => "local",
        "home-server" => "home-server",
        _ => "api-key",
    };

    /// <summary>The request again, with the choices spelled out as a last instruction.</summary>
    static TextRequest RetryRequest(TextRequest request, IReadOnlyList<string> choices) =>
        new([.. request.Messages, new ChatMessage("system", Texts.ChoicesInstruction(choices))],
            request.MaxTokens, request.Temperature, request.Language, request.Choices, request.Thinking);

    /// <summary>What the page said about thinking: only an explicit "no" (<c>false</c>, or "off" / "no" / "none" / "false") turns it off.</summary>
    static bool ThinkingWanted(JsonNode? node)
    {
        if (node is not JsonValue value) return true;
        if (value.TryGetValue<bool>(out var flag)) return flag;
        return !(value.TryGetValue<string>(out var word) && word.Trim().ToLowerInvariant() is "off" or "no" or "none" or "false");
    }

    /// <summary>The visitor's own switch for one source: "Let the model think" unticked means it answers straight away, whatever the page asked.</summary>
    TextRequest WithSourceThinking(ITextSource source, TextRequest request) =>
        request.Thinking && _store.Read(s => s.Sources.FirstOrDefault(x => x.Id == source.Id)?.Thinking == false)
            ? request with { Thinking = false }
            : request;

    static int? Int(JsonNode? node) => node is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;

    static double? Num(JsonNode? node) => node is JsonValue v && v.TryGetValue<double>(out var f) && double.IsFinite(f) ? f : null;

    /// <summary>The calls and money one call holds against the daily limits while it runs.</summary>
    sealed class DailyHold(string origin)
    {
        public string Origin { get; } = origin;
        public int Calls { get; set; }
        public decimal Money { get; set; }
    }

    /// <summary>Takes a call slot for today if the site's calls per day (done and running) allow it.</summary>
    bool HoldCall(DailyHold hold, int callsPerDay)
    {
        lock (_dailyGate)
        {
            var (_, done) = _activity.Today(Now, hold.Origin);
            var running = _inflight.GetValueOrDefault(hold.Origin);
            if (done + running.Calls >= callsPerDay) return false;
            _inflight[hold.Origin] = (running.Calls + 1, running.Money);
            hold.Calls++;
            return true;
        }
    }

    /// <summary>Reserves a paid call's estimate if the site's and the wallet's money per day (spent and reserved) allow it.</summary>
    bool HoldMoney(DailyHold hold, decimal estimate, decimal sitePerDay, decimal walletPerDay)
    {
        lock (_dailyGate)
        {
            var (siteSpent, _) = _activity.Today(Now, hold.Origin);
            var (walletSpent, _) = _activity.Today(Now);
            var running = _inflight.GetValueOrDefault(hold.Origin);
            var allRunning = _inflight.Values.Sum(v => v.Money);
            if (siteSpent + running.Money + estimate > sitePerDay || walletSpent + allRunning + estimate > walletPerDay) return false;
            _inflight[hold.Origin] = (running.Calls, running.Money + estimate);
            hold.Money += estimate;
            return true;
        }
    }

    void ReleaseDaily(DailyHold hold)
    {
        lock (_dailyGate)
        {
            if (!_inflight.TryGetValue(hold.Origin, out var running)) return;
            var next = (Calls: running.Calls - hold.Calls, Money: running.Money - hold.Money);
            if (next.Calls <= 0 && next.Money <= 0) _inflight.Remove(hold.Origin);
            else _inflight[hold.Origin] = next;
            hold.Calls = 0;
            hold.Money = 0;
        }
    }

    void ReleaseHeldCall(DailyHold hold)
    {
        lock (_dailyGate)
        {
            if (hold.Calls <= 0) return;
            if (_inflight.TryGetValue(hold.Origin, out var running))
            {
                var next = (Calls: running.Calls - 1, Money: running.Money);
                if (next.Calls <= 0 && next.Money <= 0) _inflight.Remove(hold.Origin);
                else _inflight[hold.Origin] = next;
            }

            hold.Calls--;
        }
    }

    void ReleaseHeldMoney(DailyHold hold, decimal amount)
    {
        if (amount <= 0) return;
        lock (_dailyGate)
        {
            var value = Math.Min(amount, hold.Money);
            if (_inflight.TryGetValue(hold.Origin, out var running))
            {
                var next = (Calls: running.Calls, Money: Math.Max(0, running.Money - value));
                if (next.Calls <= 0 && next.Money <= 0) _inflight.Remove(hold.Origin);
                else _inflight[hold.Origin] = next;
            }

            hold.Money = Math.Max(0, hold.Money - value);
        }
    }

    void AdjustHeldMoney(DailyHold hold, decimal delta)
    {
        if (delta == 0) return;
        lock (_dailyGate)
        {
            var current = _inflight.GetValueOrDefault(hold.Origin);
            var nextMoney = Math.Max(0, current.Money + delta);
            _inflight[hold.Origin] = (current.Calls, nextMoney);
            hold.Money = Math.Max(0, hold.Money + delta);
        }
    }

    async Task<PaidAnswer> AskPaidAsync(GrantSession session, SiteRecord site, string sourceName, decimal estimate, IReadOnlyList<string> route, CancellationToken ct)
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
                return await _ui.AskPaidAsync(new PaidModel(site.Origin, host, sourceName, estimate, why), timeout.Token).ConfigureAwait(false);
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

    /// <summary>Ends every grant of a site and forgets what it was allowed (Sites → Revoke, or the wallet itself when a site broke the rules).</summary>
    public void Revoke(string origin, string note = "revoked in the settings")
    {
        DropSessions(origin);
        _store.Update(s =>
        {
            var r = Site(s, origin);
            r.Status = SiteStatus.None;
            r.Skills = [];
            r.Models = [];
            r.AutoDecide = false;
            r.PaidAllowed = false;
            DropLinkDecisions(origin);
        });
        Log(new ActivityEntry { Origin = origin, Kind = Wire.Request, Outcome = "revoked", Note = note });
        SitesChanged?.Invoke();
        SiteClosed?.Invoke(origin);
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
            if (blocked) DropLinkDecisions(origin);
        });
        SitesChanged?.Invoke();
        if (blocked) SiteClosed?.Invoke(origin);
    }

    /// <summary>
    /// Sites → Pause: the site keeps what it was allowed, but its grants end, it stops reconnecting by itself and gets nothing
    /// on its own; its next request, after a click, shows the notice and asks to resume.
    /// </summary>
    public void Pause(string origin)
    {
        var paused = false;
        _store.Update(s =>
        {
            var r = Site(s, origin);
            if (r.Status != SiteStatus.Allowed || r.Paused) return;
            r.Paused = true;
            r.PausedAt = Now;
            r.PauseReason = "you";
            paused = true;
            DropLinkDecisions(origin);
        });
        if (!paused) return;
        DropSessions(origin);
        Log(new ActivityEntry { Origin = origin, Kind = Wire.Request, Outcome = "paused", Note = "paused in the settings" });
        SitesChanged?.Invoke();
        SiteClosed?.Invoke(origin);
    }

    /// <summary>
    /// Sites → Resume: lifts a pause, the visitor's own or the one for being unused. The "unused" clock starts again from now, so a site
    /// that was paused for a day without use is not paused again the moment it is resumed. Its sessions ended with the pause: it connects
    /// again with its next request (a link asks as always; "decide on my own" works again for a reconnect).
    /// </summary>
    public void Resume(string origin)
    {
        var site = FindSite(origin);
        if (site is not { Status: SiteStatus.Allowed } || (!site.Paused && UnusedSince(site) is null)) return;
        _store.Update(s =>
        {
            var r = Site(s, origin);
            r.Paused = false;
            r.PausedAt = null;
            r.PauseReason = null;
            r.LastRequest = Now;
        });
        Log(new ActivityEntry { Origin = origin, Kind = Wire.Request, Outcome = "resumed", Note = "resumed in the settings" });
        SitesChanged?.Invoke();
    }

    /// <summary>An allowed site that may reconnect and get a page's grant without asking: "decide on my own" on, and not paused. A link always asks.</summary>
    public bool DecidesOnItsOwn(string origin) => FindSite(origin) is { Status: SiteStatus.Allowed, AutoDecide: true, Paused: false };

    /// <summary>Applies an unused-site pause before a transport makes a reconnect/resume decision.</summary>
    public bool PauseSiteIfDue(string origin) => PauseIfDue(origin);

    /// <summary>When the site was last heard from: its newest entry in the activity log, its last request or the last decision.</summary>
    public DateTimeOffset? LastSeen(SiteRecord site) =>
        new[] { _activity.LastAt(site.Origin), site.LastRequest, site.DecidedAt }.Where(t => t is not null).Max();

    /// <summary>The pause that applies to an allowed site now: the visitor's own, or one that is due because the site went unused
    /// (shown in Sites even before the wallet stores it on the site's next request or call). Null when it is not paused.</summary>
    public string? PausedNow(SiteRecord site)
    {
        if (site.Status != SiteStatus.Allowed) return null;
        if (site.Paused) return PauseText(site);
        return UnusedSince(site) is { } since ? PauseText(new SiteRecord { PauseReason = "unused", PausedAt = since }) : null;
    }

    /// <summary>When an allowed site's unused time ran out (the moment it counts as paused), or null while it is in use.</summary>
    DateTimeOffset? UnusedSince(SiteRecord site)
    {
        var hours = _store.Read(s => s.Limits.PauseUnusedAfterHours);
        if (hours <= 0 || LastSeen(site) is not { } last) return null;
        var due = last.AddHours(hours);
        return due <= Now ? due : null;
    }

    /// <summary>Stores a pause that is due (the site went unused too long): its grants end and it is logged. True when the site is paused.</summary>
    bool PauseIfDue(string origin)
    {
        var site = FindSite(origin);
        if (site is not { Status: SiteStatus.Allowed }) return false;
        if (site.Paused) return true;
        if (UnusedSince(site) is not { } since) return false;
        var hours = _store.Read(s => s.Limits.PauseUnusedAfterHours);
        _store.Update(s =>
        {
            var r = Site(s, origin);
            r.Paused = true;
            r.PausedAt = since;
            r.PauseReason = "unused";
            DropLinkDecisions(origin);
        });
        DropSessions(origin);
        Log(new ActivityEntry { Origin = origin, Kind = Wire.Request, Outcome = "paused", Note = "paused after " + Hours(hours) + " without use" });
        SitesChanged?.Invoke();
        SiteClosed?.Invoke(origin);
        return true;
    }

    /// <summary>"Paused by you on 12 October" / "Paused after a day without use".</summary>
    string PauseText(SiteRecord site) => site.PauseReason == "unused"
        ? "Paused after " + Hours(_store.Read(s => s.Limits.PauseUnusedAfterHours)) + " without use"
        : "Paused by you" + (site.PausedAt is { } at ? " on " + at.ToLocalTime().ToString("d MMMM, HH:mm", CultureInfo.InvariantCulture) : "");

    public static string Hours(int hours) => hours switch
    {
        1 => "an hour",
        24 => "a day",
        168 => "a week",
        _ when hours % 24 == 0 => (hours / 24).ToString(CultureInfo.InvariantCulture) + " days",
        _ => hours.ToString(CultureInfo.InvariantCulture) + " hours",
    };

    public void SetAutoDecide(string origin, bool auto)
    {
        IReadOnlyList<string> dropped = [];
        _store.Update(s =>
        {
            var r = Site(s, origin);
            var changed = r.AutoDecide != auto;
            r.AutoDecide = auto;
            if (changed) dropped = DropLinkDecisions(origin);
        });
        SitesChanged?.Invoke();
        if (dropped.Count > 0) LinkAnswersDropped?.Invoke(dropped);
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

    /// <summary>
    /// Forgets the answers to a site's links that wait for the site's welcome, with their grants, and returns their grant ids.
    /// The visitor's writes call it under the store's lock, in the same update, so that a welcome either finds its answer
    /// before the decision (and stores it first) or finds it gone.
    /// </summary>
    IReadOnlyList<string> DropLinkDecisions(string origin)
    {
        var held = _linkDecisions.Where(d => d.Value.Origin == origin).Select(d => d.Key).ToList();
        foreach (var id in held)
        {
            _linkDecisions.TryRemove(id, out _);
            _sessions.TryRemove(id, out _);
        }

        return held;
    }

    // ------------------------------------------------------------------------------------------------
    // helpers

    /// <summary>Uses an explicitly saved primary-language route before the skill's default route.</summary>
    static SkillChain ChainFor(WalletSettings settings, string skill, string? language)
    {
        var primary = Vocabulary.PrimaryLanguage(language);
        return settings.Chains.FirstOrDefault(c => c.Skill == skill && c.Language == primary)
            ?? settings.Chains.FirstOrDefault(c => c.Skill == skill)
            ?? new SkillChain { Skill = skill, Language = primary };
    }

    static bool IsEnabled(WalletSettings settings, string sourceId) =>
        settings.Sources.FirstOrDefault(s => s.Id == sourceId)?.Enabled ?? true;

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
        Paused = r.Paused, PausedAt = r.PausedAt, PauseReason = r.PauseReason,
        TextInputChars = r.TextInputChars, TextAnswerTokens = r.TextAnswerTokens, TextCallSeconds = r.TextCallSeconds,
        ParallelCalls = r.ParallelCalls, CallsPerMinute = r.CallsPerMinute,
        Asked = r.Asked is null ? null : new SiteAsk
        {
            InputChars = r.Asked.InputChars, AnswerTokens = r.Asked.AnswerTokens, CallSeconds = r.Asked.CallSeconds,
            ParallelCalls = r.Asked.ParallelCalls, CallsPerMinute = r.Asked.CallsPerMinute,
        },
        Models = r.Models.Select(m => new SiteModels { Skill = m.Skill, SourceIds = [.. m.SourceIds] }).ToList(),
        ShowModelNames = r.ShowModelNames,
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
