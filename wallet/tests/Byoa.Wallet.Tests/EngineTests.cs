using System.Diagnostics;
using System.Text.Json.Nodes;
using Byoa.Wallet.Activity;
using Byoa.Wallet.Engine;
using Byoa.Wallet.Protocol;
using Byoa.Wallet.Settings;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Tests;

/// <summary>The wallet's decisions, end to end inside the engine: the click rule, decline/block/auto, the
/// difference, origin binding, the gate (spec/rules-0.1-draft.md through the BringYourOwnAgent library), the
/// per-day limits, routing with fallbacks and the paid question.</summary>
public class EngineTests
{
    const string Site = "https://geo.example.org";
    const string Other = "https://other.example.org";

    readonly ManualTime _time = new();
    readonly ScriptedUi _ui = new();
    readonly FakeSource _giga = new("gigaam-v3", languages: ["ru"]) { Answer = "francia-giga" };
    readonly FakeSource _whisper = new("whisper") { Answer = "francia-whisper" };
    readonly FakeSource _paid = new("openrouter-scribe", Runs.ApiKey, pricePerSecond: 0.00004m) { Answer = "francia-paid" };
    readonly SettingsStore _store = new(null);
    readonly ActivityLog _log;
    readonly WalletEngine _engine;

    public EngineTests()
    {
        _log = new ActivityLog(null, _time.Now);
        _engine = new WalletEngine(_store, [_giga, _whisper, _paid], _log, _ui, _time);
    }

    async Task<string> GrantAsync(string origin = Site, string manifest = Clips.GeoManifest)
    {
        var result = await _engine.HandleAsync(Clips.Request(origin, click: true, manifest));
        return Assert.IsType<JsonObject>(result)["grantId"]!.GetValue<string>();
    }

    static bool Ok(JsonNode? result) => result is JsonObject o && o["ok"]?.GetValue<bool>() == true;

    static string Manifest(string limits = "\"maxClipSeconds\": 15, \"maxCallsPerMinute\": 40", string cost = "0.05 USD", string extraSkill = "") =>
        "{\"byoa\":\"0.1-draft\",\"purpose\":{\"summary\":\"Family geography game\",\"limits\":{" + limits + "}},"
        + "\"skills\":{\"audio.transcribe\":{\"languages\":[\"ru\",\"sk\"],\"required\":true}" + extraSkill + "},"
        + "\"privacy\":\"personal-voice\",\"expectedCost\":{\"perSession\":\"" + cost + "\"}}";

    // ------------------------------------------------------------------ requests

    [Fact]
    public async Task AllowGivesAGrantInTheContractShape()
    {
        var result = Assert.IsType<JsonObject>(await _engine.HandleAsync(Clips.Request(Site, click: true)));
        Assert.Equal(["grantId", "skills", "expiresAt"], result.Select(p => p.Key));
        Assert.Equal("[\"audio.transcribe\"]", result["skills"]!.ToJsonString());
        Assert.Equal(1, _ui.Notices);
        var consent = Assert.Single(_ui.Consents);
        Assert.Equal("geo.example.org", consent.Host);
        Assert.True(consent.Secure);
        Assert.Equal(0.05m, consent.Budget);                       // the site's expected cost is proposed
        var row = Assert.Single(consent.Rows);
        Assert.Equal(["ru", "sk"], row.Routes.Select(r => r.Language));
        Assert.Equal(["Fake gigaam-v3", "Fake whisper"], row.Routes.Select(r => r.SourceName));
        Assert.Contains("Manager suggests", row.Why);
        Assert.Equal(SiteStatus.Allowed, _store.Read(s => s.Site(Site)!.Status));
    }

    [Fact]
    public async Task APageThatAsksInALoopIsCutAndTheRestIsNotWrittenDown()
    {
        var changes = 0;
        _store.Changed += () => changes++;
        for (var i = 0; i < WalletEngine.RequestsPerMinutePerOrigin; i++)
            Assert.Null(await _engine.HandleAsync(Clips.Request(Site, click: false)));
        Assert.Equal(WalletEngine.RequestsPerMinutePerOrigin, _store.Read(s => s.Site(Site)!.QuietRequests));
        Assert.True(changes <= WalletEngine.RequestsPerMinutePerOrigin + 1, $"{changes} settings writes for {WalletEngine.RequestsPerMinutePerOrigin} requests");   // LastRequest once, not every time

        var before = changes;
        var quietBefore = _ui.Quiet;
        for (var i = 0; i < 500; i++) Assert.Null(await _engine.HandleAsync(Clips.Request(Site, click: false)));
        Assert.Equal(before, changes);                                                       // the loop writes no settings
        Assert.Equal(WalletEngine.RequestsPerMinutePerOrigin, _store.Read(s => s.Site(Site)!.QuietRequests));
        Assert.Single(_log.Recent(), e => e.Outcome == "limit");                             // and one line in the activity
        Assert.Equal(quietBefore, _ui.Quiet);                                                // no badge refresh either

        Assert.Null(await _engine.HandleAsync(Clips.Request(Other, click: false)));          // another site is not cut
        Assert.Equal(1, _store.Read(s => s.Site(Other)!.QuietRequests));

        _time.Now += TimeSpan.FromMinutes(1);                                                // a minute later the page may ask again
        Assert.Null(await _engine.HandleAsync(Clips.Request(Site, click: false)));
        Assert.Equal(WalletEngine.RequestsPerMinutePerOrigin + 1, _store.Read(s => s.Site(Site)!.QuietRequests));
    }

    [Fact]
    public async Task NoClickGivesNullAQuietBadgeAndNoNotice()
    {
        Assert.Null(await _engine.HandleAsync(Clips.Request(Site, click: false)));
        Assert.Equal(0, _ui.Notices);
        Assert.Equal(1, _ui.Quiet);
        Assert.Equal(1, _engine.UnseenQuiet);
        Assert.Equal(1, _store.Read(s => s.Site(Site)!.QuietRequests));
        Assert.Equal("held", _log.Recent()[0].Outcome);
        _engine.ClearBadge();
        Assert.Equal(0, _engine.UnseenQuiet);
    }

    [Fact]
    public async Task DeclineAndBlockAreTheSameNullForThePage()
    {
        _ui.Notice = NoticeAnswer.Decline;
        Assert.Null(await _engine.HandleAsync(Clips.Request(Site, click: true)));
        Assert.Equal(SiteStatus.Declined, _store.Read(s => s.Site(Site)!.Status));

        // Declined is a quiet door, just like blocked. Revoke is the explicit visitor action that lets the
        // origin ask again; a page request must not reopen the prompt by itself.
        _ui.Notice = NoticeAnswer.Show;
        _ui.Consent = ConsentAnswer.Declined;
        Assert.Null(await _engine.HandleAsync(Clips.Request(Site, click: true)));
        _engine.Revoke(Site);

        _ui.Notice = NoticeAnswer.Block;
        Assert.Null(await _engine.HandleAsync(Clips.Request(Site, click: true)));
        Assert.Equal(SiteStatus.Blocked, _store.Read(s => s.Site(Site)!.Status));

        // Blocked: no notice any more, with or without a click.
        var notices = _ui.Notices;
        Assert.Null(await _engine.HandleAsync(Clips.Request(Site, click: true)));
        Assert.Null(await _engine.HandleAsync(Clips.Request(Site, click: false)));
        Assert.Equal(notices, _ui.Notices);
        Assert.Equal(0, _ui.Quiet);
    }

    [Fact]
    public async Task APreviouslyDeclinedOriginGetsNullWithoutAskingAgain()
    {
        _ui.Notice = NoticeAnswer.Decline;
        Assert.Null(await _engine.HandleAsync(Clips.Request(Site, click: true)));
        var notices = _ui.Notices;
        var started = Stopwatch.GetTimestamp();

        _ui.Notice = NoticeAnswer.Show;
        _ui.Consent = new ConsentAnswer(true, 0.05m, false, []);
        Assert.Null(await _engine.HandleAsync(Clips.Request(Site, click: true)));

        Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(1));
        Assert.Equal(notices, _ui.Notices);
        Assert.Equal("declined", _log.Recent()[0].Outcome);
    }

    [Fact]
    public async Task DecliningTheConsentWindowAnswersThePageImmediately()
    {
        _ui.Notice = NoticeAnswer.Show;
        _ui.Consent = ConsentAnswer.Declined;
        var started = Stopwatch.GetTimestamp();

        Assert.Null(await _engine.HandleAsync(Clips.Request(Site, click: true)));

        Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(1));
        Assert.Equal(1, _ui.Notices);
        Assert.Single(_ui.Consents);
        Assert.Equal(SiteStatus.Declined, _store.Read(s => s.Site(Site)!.Status));
    }

    [Fact]
    public async Task DecideOnMyOwnGrantsSilentlyEvenWithoutAClick()
    {
        _ui.Consent = new ConsentAnswer(true, 0.05m, AutoDecide: true, []);
        var first = await GrantAsync();
        var notices = _ui.Notices;
        var again = Assert.IsType<JsonObject>(await _engine.HandleAsync(Clips.Request(Site, click: false)));
        Assert.NotEqual(first, again["grantId"]!.GetValue<string>());
        Assert.Equal(notices, _ui.Notices);
        Assert.Equal("auto", _log.Recent()[0].Outcome);
    }

    [Fact]
    public async Task WithoutDecideOnMyOwnANewVisitAsksAgainAfterAClick()
    {
        await GrantAsync();
        Assert.Null(await _engine.HandleAsync(Clips.Request(Site, click: false)));
        await GrantAsync();
        Assert.Equal(2, _ui.Notices);
    }

    [Fact]
    public async Task AskingForMoreShowsOnlyTheDifference()
    {
        _ui.Consent = new ConsentAnswer(true, 0.05m, AutoDecide: true, []);
        await GrantAsync();
        _ui.Consent = null;
        // A higher expected cost is more than the grant covers: a notice even though "decide on my own" is on.
        var grant = await _engine.HandleAsync(Clips.Request(Site, click: true, Manifest(cost: "0.10 USD")));
        Assert.NotNull(grant);
        var diff = _ui.Consents[^1];
        Assert.True(diff.IsDifference);
        Assert.Empty(diff.Rows);
        Assert.Equal(0.05m, diff.BudgetBefore);
        Assert.Equal(0.10m, diff.Budget);
        Assert.Equal("0.1 USD", _store.Read(s => s.Site(Site)!.Budget));
        // A skill the wallet cannot serve is not "more": it does not keep asking.
        await _engine.HandleAsync(Clips.Request(Site, click: true, Manifest(cost: "0.10 USD", extraSkill: ",\"text.understand\":{\"required\":false}")));
        Assert.False(_ui.Consents[^1].IsDifference);
    }

    [Fact]
    public async Task ARequiredSkillNoSourceCanServeCannotBeAllowed()
    {
        var result = await _engine.HandleAsync(Clips.Request(Site, click: true, Manifest(extraSkill: ",\"text.embed\":{\"required\":true}")));
        Assert.Null(result);
        var consent = _ui.Consents[^1];
        Assert.True(consent.MissingRequired);
        Assert.False(consent.Rows.Single(r => r.Skill == "text.embed").CanServe);
    }

    [Theory]
    [InlineData("http://geo.example.org")]
    [InlineData("https://geo.example.org/path")]
    [InlineData("chrome-extension://abc")]
    [InlineData("")]
    public async Task OnlyHttpsAndLocalhostOriginsAreServed(string origin)
    {
        Assert.Null(await _engine.HandleAsync(Clips.Request(origin, click: true)));
        Assert.Equal(0, _ui.Notices);
    }

    [Fact]
    public async Task AnInvalidManifestIsNull()
    {
        Assert.Null(await _engine.HandleAsync(Clips.Request(Site, click: true, "{\"byoa\":\"0.1-draft\"}")));
        Assert.Equal(0, _ui.Notices);
        Assert.StartsWith("manifest:", _log.Recent()[0].Note);
    }

    // ------------------------------------------------------------------ calls

    [Fact]
    public async Task RussianIsAnsweredByGigaAmSlovakByWhisper()
    {
        var grant = await GrantAsync();
        var ru = Assert.IsType<JsonObject>(await _engine.HandleAsync(Clips.Call(Site, grant, language: "ru")));
        Assert.Equal("{\"ok\":true,\"output\":{\"text\":\"francia-giga\"},\"receipt\":{\"cost\":\"0.000 USD\",\"provider\":\"local\"}}", ru.ToJsonString());
        var sk = await _engine.HandleAsync(Clips.Call(Site, grant, language: "sk-SK"));
        Assert.Equal("francia-whisper", sk!["output"]!["text"]!.GetValue<string>());
        Assert.Equal("sk", _whisper.LastLanguage);
        var entry = _log.Recent()[0];
        Assert.Equal("call", entry.Kind);
        Assert.Equal("ok", entry.Outcome);
        Assert.Equal(2.0, entry.ClipSeconds);
        Assert.Null(entry.Fallback);
    }

    [Fact]
    public async Task AGrantIsBoundToItsOrigin()
    {
        var grant = await GrantAsync();
        Assert.False(Ok(await _engine.HandleAsync(Clips.Call(Other, grant))));
        Assert.False(Ok(await _engine.HandleAsync(Clips.Call(Site, "made-up-grant"))));
        Assert.True(Ok(await _engine.HandleAsync(Clips.Call(Site, grant))));
    }

    [Fact]
    public async Task TheClipLengthIsMeasuredAndLimitedByTheGate()
    {
        var grant = await GrantAsync();
        Assert.True(Ok(await _engine.HandleAsync(Clips.Call(Site, grant, seconds: 15))));
        Assert.False(Ok(await _engine.HandleAsync(Clips.Call(Site, grant, seconds: 15.5))));
        Assert.Equal("clip-too-long", _log.Recent()[0].Note);
        Assert.Equal(1, _giga.Calls);                             // the source never ran for the long clip
    }

    [Fact]
    public async Task TheRateLimitOfTheManifestIsEnforced()
    {
        var grant = await GrantAsync(manifest: Manifest(limits: "\"maxCallsPerMinute\": 2"));
        Assert.True(Ok(await _engine.HandleAsync(Clips.Call(Site, grant))));
        Assert.True(Ok(await _engine.HandleAsync(Clips.Call(Site, grant))));
        Assert.False(Ok(await _engine.HandleAsync(Clips.Call(Site, grant))));
        Assert.Equal("rate-exceeded", _log.Recent()[0].Note);
        _time.Now += TimeSpan.FromSeconds(61);
        Assert.True(Ok(await _engine.HandleAsync(Clips.Call(Site, grant))));
    }

    [Fact]
    public async Task AnExpiredGrantIsRefusedByTheGate()
    {
        var grant = await GrantAsync();
        _time.Now += GrantSession.Lifetime + TimeSpan.FromSeconds(1);
        Assert.False(Ok(await _engine.HandleAsync(Clips.Call(Site, grant))));
        Assert.Equal("grant-expired", _log.Recent()[0].Note);
    }

    [Fact]
    public async Task ASkillOutsideTheGrantIsRefused()
    {
        var grant = await GrantAsync();
        var call = Clips.Call(Site, grant);
        call.Payload["skill"] = "text.generate";
        Assert.False(Ok(await _engine.HandleAsync(call)));
    }

    [Fact]
    public async Task BadAudioIsRefused()
    {
        var grant = await GrantAsync();
        var call = Clips.Call(Site, grant);
        call.Payload["input"]!["audio"]!["base64"] = Convert.ToBase64String("not a wav"u8.ToArray());
        Assert.False(Ok(await _engine.HandleAsync(call)));
        call.Payload["input"]!["audio"]!["mime"] = "audio/mpeg";
        Assert.False(Ok(await _engine.HandleAsync(call)));
        call.Payload.Remove("input");
        Assert.False(Ok(await _engine.HandleAsync(call)));
        Assert.Equal(0, _giga.Calls);
    }

    [Fact]
    public async Task TheDailyCallLimitOfASiteIsEnforced()
    {
        _store.Update(s => s.Limits.SiteCallsPerDay = 2);
        var grant = await GrantAsync();
        Assert.True(Ok(await _engine.HandleAsync(Clips.Call(Site, grant))));
        Assert.True(Ok(await _engine.HandleAsync(Clips.Call(Site, grant))));
        Assert.False(Ok(await _engine.HandleAsync(Clips.Call(Site, grant))));
        Assert.Equal("limit", _log.Recent()[0].Outcome);
        Assert.Single(_ui.Infos);
    }

    [Fact]
    public async Task AFailingSourceFallsBackToTheNextAndTheRouteIsLogged()
    {
        _giga.Fail = true;
        var grant = await GrantAsync();
        var result = await _engine.HandleAsync(Clips.Call(Site, grant));
        Assert.Equal("francia-whisper", result!["output"]!["text"]!.GetValue<string>());
        Assert.Equal("Fake gigaam-v3: fake failure → Fake whisper", _log.Recent()[0].Fallback);
    }

    [Fact]
    public async Task ATurnedOffSourceFallsBackToTheNext()
    {
        _store.Update(s => s.Source("gigaam-v3").Enabled = false);
        var grant = await GrantAsync();
        var result = await _engine.HandleAsync(Clips.Call(Site, grant));
        Assert.Equal("francia-whisper", result!["output"]!["text"]!.GetValue<string>());
        Assert.Equal("Fake gigaam-v3: turned off → Fake whisper", _log.Recent()[0].Fallback);
    }

    [Fact]
    public async Task APaidFallbackAsksFirstAndNoMeansOkFalse()
    {
        _giga.Fail = true;
        _whisper.Fail = true;
        var grant = await GrantAsync();
        _ui.Paid = PaidAnswer.No;
        Assert.False(Ok(await _engine.HandleAsync(Clips.Call(Site, grant))));
        Assert.Equal(1, _ui.PaidQuestions);
        Assert.Equal(0, _paid.Calls);
        Assert.Equal("refused", _log.Recent()[0].Outcome);

        _ui.Paid = PaidAnswer.Once;
        var result = Assert.IsType<JsonObject>(await _engine.HandleAsync(Clips.Call(Site, grant)));
        Assert.Equal("api-key", result["receipt"]!["provider"]!.GetValue<string>());
        Assert.Equal("0.00008 USD", _log.Recent()[0].Cost);
        Assert.Equal("0.00008 USD", result["receipt"]!["cost"]!.GetValue<string>());
        Assert.Equal(2, _ui.PaidQuestions);

        _ui.Paid = PaidAnswer.Always;
        Assert.True(Ok(await _engine.HandleAsync(Clips.Call(Site, grant))));
        Assert.True(Ok(await _engine.HandleAsync(Clips.Call(Site, grant))));
        Assert.Equal(3, _ui.PaidQuestions);                       // "always for this site" asks no more
    }

    [Fact]
    public async Task OverTheBudgetPerSessionNothingIsPaid()
    {
        _giga.Fail = true;
        _whisper.Fail = true;
        _ui.Paid = PaidAnswer.Once;
        var grant = await GrantAsync(manifest: Manifest(cost: "0.0001 USD"));
        Assert.True(Ok(await _engine.HandleAsync(Clips.Call(Site, grant))));    // 0.00008 of 0.0001
        Assert.False(Ok(await _engine.HandleAsync(Clips.Call(Site, grant))));   // would be 0.00016
        Assert.Equal(1, _paid.Calls);
        Assert.Equal("budget per session", _log.Recent()[0].Note);
    }

    [Fact]
    public async Task TheDailyMoneyLimitStopsPaidCalls()
    {
        _giga.Fail = true;
        _whisper.Fail = true;
        _ui.Paid = PaidAnswer.Always;
        _store.Update(s => s.Limits.SitePerDay = "0.0001 USD");
        var grant = await GrantAsync();
        Assert.True(Ok(await _engine.HandleAsync(Clips.Call(Site, grant))));
        Assert.False(Ok(await _engine.HandleAsync(Clips.Call(Site, grant))));
        Assert.Equal("money per day", _log.Recent()[0].Note);
    }

    [Fact]
    public async Task ReleaseRevokeAndBlockEndTheGrant()
    {
        var a = await GrantAsync();
        await _engine.HandleAsync(new WireMessage(5, Wire.Release, Site, false, new JsonObject { ["grantId"] = a }));
        Assert.False(Ok(await _engine.HandleAsync(Clips.Call(Site, a))));

        var b = await GrantAsync();
        _engine.Revoke(Site);
        Assert.False(Ok(await _engine.HandleAsync(Clips.Call(Site, b))));

        var c = await GrantAsync();
        _engine.Block(Site, true);
        Assert.False(Ok(await _engine.HandleAsync(Clips.Call(Site, c))));
        Assert.Null(await _engine.HandleAsync(Clips.Request(Site, click: true)));
        _engine.Block(Site, false);
        Assert.NotNull(await _engine.HandleAsync(Clips.Request(Site, click: true)));
    }

    [Fact]
    public async Task WithoutALanguageAndOneDeclaredLanguageThatOneIsUsed()
    {
        var grant = await GrantAsync(manifest: Clips.GeoManifest.Replace("[\"ru\", \"sk\"]", "[\"ru\"]"));
        var result = await _engine.HandleAsync(Clips.Call(Site, grant, language: null));
        Assert.Equal("francia-giga", result!["output"]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task StatusReportsTheWalletAndTheBadge()
    {
        await _engine.HandleAsync(Clips.Request(Site, click: false));
        var status = await _engine.HandleAsync(new WireMessage(9, Wire.Status, "", false, new JsonObject()));
        Assert.Equal("{\"running\":true,\"version\":\"0.1.0\",\"quiet\":1}", status!.ToJsonString());
    }

    // ------------------------------------------------------------------ pause

    [Fact]
    public async Task APausedSiteGetsNothingOnItsOwnAndItsNextClickAsksToResume()
    {
        _ui.Consent = new ConsentAnswer(true, 0.05m, AutoDecide: true, []);
        var grant = await GrantAsync();
        _engine.Pause(Site);

        Assert.True(_store.Read(s => s.Site(Site)!.Paused));
        Assert.Null(_engine.FindGrant(grant));                                     // its open session ended
        Assert.False(_engine.DecidesOnItsOwn(Site));                               // no reconnecting by itself
        Assert.Null(await _engine.HandleAsync(Clips.Request(Site, click: false))); // "decide on my own" does not apply while paused
        Assert.Equal("held", _log.Recent()[0].Outcome);

        var resumed = await _engine.HandleAsync(Clips.Request(Site, click: true));
        Assert.NotNull(resumed);
        Assert.StartsWith("Paused by you", _ui.Consents[^1].PausedNote);
        Assert.False(_store.Read(s => s.Site(Site)!.Paused));
        Assert.Contains("resumed", _log.Recent()[0].Note);
        Assert.True(_engine.DecidesOnItsOwn(Site));
    }

    [Fact]
    public async Task ASiteUnusedLongerThanTheSettingIsPausedAndACallOnItsOldGrantIsRefused()
    {
        _store.Update(s => s.Limits.PauseUnusedAfterHours = 1);
        _ui.Consent = new ConsentAnswer(true, 0.05m, AutoDecide: true, []);
        var grant = await GrantAsync();
        _time.Now += TimeSpan.FromMinutes(50);
        Assert.True(Ok(await _engine.HandleAsync(Clips.Call(Site, grant))));      // in use: the hour starts again
        _time.Now += TimeSpan.FromMinutes(50);
        Assert.True(Ok(await _engine.HandleAsync(Clips.Call(Site, grant))));

        _time.Now += TimeSpan.FromMinutes(61);
        Assert.Equal("Paused after an hour without use", _engine.PausedNow(_engine.FindSite(Site)!));   // shown before it is stored
        Assert.False(Ok(await _engine.HandleAsync(Clips.Call(Site, grant))));
        Assert.Equal("paused", _log.Recent()[0].Outcome);
        Assert.Equal("unused", _store.Read(s => s.Site(Site)!.PauseReason));
        Assert.Contains(_log.Recent(), e => e.Outcome == "paused" && e.Note == "paused after an hour without use");
        Assert.Null(_engine.FindGrant(grant));

        await _engine.HandleAsync(Clips.Request(Site, click: true));
        Assert.Equal("Paused after an hour without use", _ui.Consents[^1].PausedNote);
    }

    [Fact]
    public async Task NeverPausesWhenTheSettingIsOff()
    {
        _store.Update(s => s.Limits.PauseUnusedAfterHours = 0);
        _ui.Consent = new ConsentAnswer(true, 0.05m, AutoDecide: true, []);
        await GrantAsync();
        _time.Now += TimeSpan.FromDays(30);
        Assert.NotNull(await _engine.HandleAsync(Clips.Request(Site, click: false)));
        Assert.Equal("auto", _log.Recent()[0].Outcome);
        Assert.Null(_engine.PausedNow(_engine.FindSite(Site)!));
    }

    [Fact]
    public async Task CoveredByGrantIsAQueryAndDoesNotPauseOrWrite()
    {
        await GrantAsync();
        _time.Now = _time.Now.AddHours(25);
        var before = System.Text.Json.JsonSerializer.Serialize(_store.Snapshot());
        var closed = false;
        _engine.SiteClosed += _ => closed = true;

        var manifest = JsonNode.Parse(Clips.GeoManifest)!.AsObject();
        Assert.True(_engine.CoveredByGrant(Site, manifest));
        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(_store.Snapshot()));
        Assert.False(closed);
    }

    [Fact]
    public async Task OnlyAnAllowedSiteCanBePaused()
    {
        _ui.Notice = NoticeAnswer.Decline;
        await _engine.HandleAsync(Clips.Request(Site, click: true));
        _engine.Pause(Site);
        Assert.False(_store.Read(s => s.Site(Site)!.Paused));
        Assert.Null(_engine.PausedNow(_engine.FindSite(Site)!));
    }
}

/// <summary>The Activity page shows the days before today too; the daily limits still count today only.</summary>
public class ActivityLogTests
{
    static ActivityEntry Call(DateTimeOffset at, string cost = "0.01 USD") =>
        new() { At = at, Origin = "https://geo.example.org", Kind = "call", Skill = "audio.transcribe", Outcome = "ok", Cost = cost };

    [Fact]
    public void EarlierDaysAreReadBackNewestFirstButOnlyTodayCountsForTheLimits()
    {
        var folder = Path.Combine(Path.GetTempPath(), "byoa-activity-" + Guid.NewGuid().ToString("N"));
        var today = new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
        var writer = new ActivityLog(folder, today.AddDays(-2));
        writer.Add(Call(today.AddDays(-2)));
        writer.Add(Call(today.AddDays(-1)));
        writer.Add(Call(today.AddDays(-1).AddMinutes(1)));
        writer.Add(Call(today.AddMinutes(-5), "0.02 USD"));
        writer.Add(Call(today.AddDays(1), "0.50 USD"));        // a file dated after today (a clock that was wrong) is not read back

        var log = new ActivityLog(folder, today);

        var all = log.Recent(int.MaxValue);
        Assert.Equal(4, all.Count);
        Assert.Equal(today.AddMinutes(-5), all[0].At);
        Assert.Equal(today.AddDays(-2), all[^1].At);
        var (spent, calls) = log.Today(today);
        Assert.Equal(0.02m, spent);
        Assert.Equal(1, calls);
    }
}
