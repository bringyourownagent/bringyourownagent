using System.Text.Json.Nodes;
using Byoa.Wallet.Activity;
using Byoa.Wallet.Engine;
using Byoa.Wallet.Protocol;
using Byoa.Wallet.Settings;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Tests;

/// <summary>The text limits: the visitor's defaults, his own for one site, what the site declared, and the ceilings.</summary>
public class TextLimitTests
{
    const string Site = "https://debate.example.org";

    // ------------------------------------------------------------------ the pure rules

    [Fact]
    public void TheDefaultsAreTheOnesTheOwnerChose()
    {
        var limits = new LimitSettings();
        var global = TextLimits.Global(limits);
        Assert.Equal(100_000, global.InputChars);
        Assert.Equal(20_000, global.AnswerTokens);
        Assert.Equal(300, global.CallSeconds);
        Assert.Equal(8, global.ParallelCalls);
        Assert.Equal(300, global.CallsPerMinute);
    }

    [Fact]
    public void ValuesOutsideTheFloorAndTheCeilingAreBroughtBack()
    {
        var wild = new LimitSettings { TextInputChars = 99_000_000, TextAnswerTokens = -5, TextCallSeconds = 100_000, ParallelCalls = 0, CallsPerMinute = 5_000 };
        var global = TextLimits.Global(wild);
        Assert.Equal(TextLimits.MaxInputChars, global.InputChars);
        Assert.Equal(16, global.AnswerTokens);
        Assert.Equal(TextLimits.MaxCallSeconds, global.CallSeconds);
        Assert.Equal(1, global.ParallelCalls);
        Assert.Equal(TextLimits.MaxCallsPerMinute, global.CallsPerMinute);
    }

    [Fact]
    public void TheVisitorsOwnValueForASiteBeatsTheDefault()
    {
        var limits = new LimitSettings();
        var site = new SiteRecord { Origin = Site, TextInputChars = 250_000, ParallelCalls = 3 };
        var set = TextLimits.ForSite(limits, site);
        Assert.Equal(250_000, set.InputChars);
        Assert.Equal(3, set.ParallelCalls);
        Assert.Equal(20_000, set.AnswerTokens);                 // not set for the site: the default
    }

    [Fact]
    public void ASiteCanOnlyHoldItselfBack()
    {
        var limits = new LimitSettings();
        var declared = new RequestedLimits(InputChars: 1_000_000, AnswerTokens: 3_000, CallSeconds: 900, ParallelCalls: 4, CallsPerMinute: 10);
        var set = TextLimits.Effective(limits, null, declared);
        Assert.Equal(3_000, set.AnswerTokens);                  // tighter than the visitor's
        Assert.Equal(300, set.CallSeconds);                     // looser than the visitor's: the visitor's stays
        Assert.Equal(4, set.ParallelCalls);
        Assert.Equal(100_000, set.InputChars);                  // the gate holds the site to its declared input and rate
        Assert.Equal(300, set.CallsPerMinute);
    }

    [Fact]
    public void WhatASiteAsksAboveTheLimitsIsOfferedUpToTheCeiling()
    {
        var current = TextLimits.Defaults;
        var asked = new RequestedLimits(InputChars: 5_000_000, AnswerTokens: 10_000, CallSeconds: 400, ParallelCalls: 8, CallsPerMinute: null);
        var beyond = TextLimits.Beyond(current, asked);
        Assert.NotNull(beyond);
        Assert.Equal(TextLimits.MaxInputChars, beyond.InputChars);   // never above the ceiling
        Assert.Null(beyond.AnswerTokens);                            // below what the visitor allows: nothing to ask
        Assert.Equal(400, beyond.CallSeconds);
        Assert.Null(beyond.ParallelCalls);                           // equal is not above
        Assert.Null(TextLimits.Beyond(current, new RequestedLimits(100, 100, 100, 1, 1)));
        Assert.Null(TextLimits.Beyond(current, null));
    }

    [Fact]
    public void TheManifestsLimitsAreRead()
    {
        var manifest = JsonNode.Parse("""
            {"byoa":"0.1-draft","purpose":{"summary":"x","limits":{"maxTextChars":60000,"maxAnswerTokens":20000,"maxCallSeconds":300,"maxParallelCalls":8,"maxCallsPerMinute":120,"nonsense":5}},
            "skills":{"text.generate":{"required":true}},"privacy":"personal"}
            """)!.AsObject();
        var requested = RequestedManifest.From(manifest);
        Assert.Equal(new RequestedLimits(60_000, 20_000, 300, 8, 120), requested.Limits);
        var bad = JsonNode.Parse("""{"byoa":"0.1-draft","purpose":{"summary":"x","limits":{"maxTextChars":-4,"maxAnswerTokens":"many","maxCallSeconds":1.5}},"skills":{"text.generate":{"required":true}},"privacy":"personal"}""")!.AsObject();
        Assert.Null(RequestedManifest.From(bad).Limits);        // nothing usable is nothing
    }

    // ------------------------------------------------------------------ in the engine

    readonly ManualTime _time = new();
    readonly ScriptedUi _ui = new();
    readonly FakeTextSource _first = new("t-first") { Answers = new Queue<string>(Enumerable.Repeat("Paris", 50)) };
    readonly SettingsStore _store = new(null);
    readonly ActivityLog _log;
    readonly WalletEngine _engine;

    public TextLimitTests()
    {
        _log = new ActivityLog(null, _time.Now);
        _engine = new WalletEngine(_store, [], _log, _ui, _time, [_first]);
        _store.Update(s => Router.EnsureTextChain(s, [_first]));
        // These manifests declare more input than the visitor's default: his answer is that he keeps his own limits.
        _ui.Consent = new ConsentAnswer(true, 0.05m, false, [], UseSiteLimits: false);
    }

    static string Manifest(string limits) => "{\"byoa\":\"0.1-draft\",\"purpose\":{\"summary\":\"Debate judge\",\"limits\":" + limits + "},"
        + "\"skills\":{\"text.generate\":{\"required\":true}},\"privacy\":\"personal\",\"expectedCost\":{\"perSession\":\"0.05 USD\"}}";

    async Task<string> GrantAsync(string limits = "{\"maxTextChars\":500000}")
    {
        var result = Assert.IsType<JsonObject>(await _engine.HandleAsync(Clips.Request(Site, click: true, Manifest(limits))));
        return result["grantId"]!.GetValue<string>();
    }

    static WireMessage TextCall(string grantId, string content, JsonObject? parameters = null) =>
        new(2, Wire.Call, Site, false, new JsonObject
        {
            ["grantId"] = grantId,
            ["skill"] = "text.generate",
            ["input"] = new JsonObject { ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = content }) },
            ["params"] = parameters ?? new JsonObject(),
        });

    static bool Ok(JsonNode? result) => result is JsonObject o && o["ok"]?.GetValue<bool>() == true;

    [Fact]
    public async Task AHundredThousandCharactersPassByDefaultAndMoreDoNotUntilTheVisitorRaisesTheLimitForTheSite()
    {
        var grant = await GrantAsync();
        Assert.True(Ok(await _engine.HandleAsync(TextCall(grant, new string('x', 100_000)))));
        Assert.False(Ok(await _engine.HandleAsync(TextCall(grant, new string('x', 100_001)))));
        Assert.Equal("text too long for the wallet: at most 100000 characters", _log.Recent()[0].Note);

        _store.Update(s => s.Site(Site)!.TextInputChars = 200_000);
        Assert.True(Ok(await _engine.HandleAsync(TextCall(grant, new string('x', 150_000)))));
        Assert.Equal(1 + 1, _first.Calls);                       // the refused call never reached the model
    }

    [Fact]
    public async Task TheDefaultForEverySiteIsChangedInTheLimits()
    {
        var grant = await GrantAsync();
        _store.Update(s => s.Limits.TextInputChars = 5_000);
        Assert.False(Ok(await _engine.HandleAsync(TextCall(grant, new string('x', 5_001)))));
        Assert.True(Ok(await _engine.HandleAsync(TextCall(grant, new string('x', 5_000)))));
    }

    [Fact]
    public async Task ASitesDeclaredInputLimitStillHoldsItBack()
    {
        var grant = await GrantAsync("{\"maxTextChars\":8000}");
        Assert.False(Ok(await _engine.HandleAsync(TextCall(grant, new string('x', 8_001)))));
        Assert.Contains("text-too-long", _log.Recent()[0].Note);   // the gate's reason: the site's own limit
        Assert.True(Ok(await _engine.HandleAsync(TextCall(grant, new string('x', 8_000)))));
    }

    [Fact]
    public async Task TheAnswerLengthFollowsTheLimitsAndWhatTheSiteDeclared()
    {
        var grant = await GrantAsync("{\"maxTextChars\":500000,\"maxAnswerTokens\":3000}");
        await _engine.HandleAsync(TextCall(grant, "hi", new JsonObject { ["maxTokens"] = 30_000 }));
        Assert.Equal(3_000, _first.LastRequest!.MaxTokens);       // the site said 3000 itself

        var open = await GrantAsync("{\"maxTextChars\":500000}");
        await _engine.HandleAsync(TextCall(open, "hi", new JsonObject { ["maxTokens"] = 30_000 }));
        Assert.Equal(20_000, _first.LastRequest!.MaxTokens);      // the visitor's default
        await _engine.HandleAsync(TextCall(open, "hi"));
        Assert.Equal(512, _first.LastRequest!.MaxTokens);         // a page that names no length gets the short default as before
        _store.Update(s => s.Limits.TextAnswerTokens = 700);
        await _engine.HandleAsync(TextCall(open, "hi", new JsonObject { ["maxTokens"] = 30_000 }));
        Assert.Equal(700, _first.LastRequest!.MaxTokens);
    }

    [Fact]
    public async Task TheVisitorsCallsPerMinuteHoldTheSiteEvenWhenItDeclaredMore()
    {
        var grant = await GrantAsync("{\"maxTextChars\":500000,\"maxCallsPerMinute\":1000}");
        _store.Update(s => s.Limits.CallsPerMinute = 2);
        Assert.True(Ok(await _engine.HandleAsync(TextCall(grant, "one"))));
        Assert.True(Ok(await _engine.HandleAsync(TextCall(grant, "two"))));
        Assert.False(Ok(await _engine.HandleAsync(TextCall(grant, "three"))));
        Assert.Contains("rate-exceeded", _log.Recent()[0].Note);
        _time.Now += TimeSpan.FromSeconds(61);
        Assert.True(Ok(await _engine.HandleAsync(TextCall(grant, "four"))));
    }

    [Fact]
    public async Task CallsAtOnceFollowTheSitesLimitForThatSite()
    {
        var grant = await GrantAsync("{\"maxTextChars\":500000,\"maxParallelCalls\":3}");
        var session = _engine.FindGrant(grant)!;
        Assert.Equal(3, _engine.ParallelCalls(session));           // the site declared fewer than the visitor's 8
        var open = _engine.FindGrant(await GrantAsync("{\"maxTextChars\":500000}"))!;
        Assert.Equal(8, _engine.ParallelCalls(open));
        _store.Update(s => s.Site(Site)!.ParallelCalls = 12);
        Assert.Equal(12, _engine.ParallelCalls(open));
    }
}
