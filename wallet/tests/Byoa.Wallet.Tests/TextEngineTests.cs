using System.Text.Json.Nodes;
using Byoa.Wallet.Activity;
using Byoa.Wallet.Engine;
using Byoa.Wallet.Protocol;
using Byoa.Wallet.Settings;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Tests;

/// <summary>text.generate end to end inside the engine: the caps, the gate's textChars, choices, hidden
/// reasoning, routing with fallbacks, the paid question and the budget.</summary>
public class TextEngineTests
{
    const string Site = "https://quiz.example.org";

    readonly ManualTime _time = new();
    readonly ScriptedUi _ui = new();
    readonly FakeTextSource _first = new("t-first") { Answers = new Queue<string>(["Paris"]) };
    readonly FakeTextSource _second = new("t-second") { Answers = new Queue<string>(["Lyon"]) };
    readonly FakeTextSource _paid = new("t-paid", Runs.ApiKey, costPerCall: 0.004m) { Answers = new Queue<string>(["Paris, clearly"]) };
    readonly SettingsStore _store = new(null);
    readonly ActivityLog _log;
    readonly WalletEngine _engine;

    public TextEngineTests()
    {
        _log = new ActivityLog(null, _time.Now);
        _engine = new WalletEngine(_store, [], _log, _ui, _time, [_first, _second, _paid]);
        _store.Update(s => Router.EnsureTextChain(s, [_first, _second, _paid]));
    }

    async Task<string> GrantAsync(string manifest = QuizManifest)
    {
        var result = Assert.IsType<JsonObject>(await _engine.HandleAsync(Clips.Request(Site, click: true, manifest)));
        return result["grantId"]!.GetValue<string>();
    }

    const string QuizManifest = """
        {"byoa":"0.1-draft","purpose":{"summary":"Family quiz","limits":{"maxTextChars":8000,"maxCallsPerMinute":40}},
        "skills":{"text.generate":{"required":true}},"privacy":"personal-voice","expectedCost":{"perSession":"0.05 USD"}}
        """;

    static bool Ok(JsonNode? result) => result is JsonObject o && o["ok"]?.GetValue<bool>() == true;

    static WireMessage TextCall(string origin, string grantId, JsonObject? parameters = null, JsonArray? messages = null) =>
        new(2, Wire.Call, origin, false, new JsonObject
        {
            ["grantId"] = grantId,
            ["skill"] = "text.generate",
            ["input"] = new JsonObject
            {
                ["messages"] = messages ?? new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "What is the capital of France?" }),
            },
            ["params"] = parameters ?? new JsonObject(),
        });

    static JsonArray Messages(params (string Role, string Content)[] items)
    {
        var array = new JsonArray();
        foreach (var (role, content) in items) array.Add(new JsonObject { ["role"] = role, ["content"] = content });
        return array;
    }

    // ------------------------------------------------------------------ the happy path

    [Fact]
    public async Task ATextCallIsAnsweredWithAReceiptAndLogged()
    {
        var grant = await GrantAsync();
        var result = Assert.IsType<JsonObject>(await _engine.HandleAsync(TextCall(Site, grant)));
        Assert.Equal("""{"ok":true,"output":{"text":"Paris"},"receipt":{"cost":"0.000 USD","provider":"local"}}""", result.ToJsonString());
        Assert.Equal("What is the capital of France?", _first.LastRequest!.Messages.Single().Content);
        Assert.Equal(512, _first.LastRequest.MaxTokens);          // the default
        Assert.Equal(0.7, _first.LastRequest.Temperature);
        var entry = _log.Recent()[0];
        Assert.Equal("call", entry.Kind);
        Assert.Equal("text.generate", entry.Skill);
        Assert.Equal("ok", entry.Outcome);
        Assert.Null(entry.Fallback);
        Assert.Null(entry.ClipSeconds);
        Assert.Equal(0, _ui.PaidQuestions);                       // a local source never asks
    }

    [Fact]
    public async Task AWalletOwnedTextTestUsesTheEngineWithoutCreatingASite()
    {
        var result = Assert.IsType<JsonObject>(await _engine.TestSourceAsync(_first.Id, Vocabulary.Generate));

        Assert.True(Ok(result), result.ToJsonString() + " / " + _log.Recent()[0].Note);
        Assert.Equal("Paris", result["output"]!["text"]!.GetValue<string>());
        Assert.Equal(1, _first.Calls);
        Assert.Null(_engine.FindSite("wallet"));
        Assert.Equal("test", _log.Recent()[0].Kind);
        Assert.Equal("wallet", _log.Recent()[0].Via);
    }

    [Fact]
    public async Task ARemoteTextTestCannotAuthorizeAPaidSource()
    {
        var refused = Assert.IsType<JsonObject>(await _engine.TestSourceAsync(_paid.Id, Vocabulary.Generate));

        Assert.False(Ok(refused));
        Assert.Contains("press Test in the wallet", refused["error"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(0, _paid.Calls);

        var allowed = Assert.IsType<JsonObject>(await _engine.TestSourceAsync(_paid.Id, Vocabulary.Generate, visitorInitiated: true));
        Assert.True(Ok(allowed), allowed.ToJsonString() + " / " + _log.Recent()[0].Note);
        Assert.Equal(1, _paid.Calls);
    }

    [Fact]
    public async Task ALanguageSpecificTextChainWinsOverTheDefaultChain()
    {
        _store.Update(s => s.Chain(Vocabulary.Generate, "de").Stops =
            [new ChainStop { SourceId = _second.Id }, new ChainStop { SourceId = _first.Id }]);
        var grant = await GrantAsync();

        var result = await _engine.HandleAsync(TextCall(Site, grant,
            new JsonObject { ["language"] = "de-DE" }));

        Assert.Equal("Lyon", result!["output"]!["text"]!.GetValue<string>());
        Assert.Equal(0, _first.Calls);
        Assert.Equal(1, _second.Calls);
    }

    [Fact]
    public async Task ARemoteCompatibleSourceIsShownAsAHomeServerInConsent()
    {
        var remote = new FakeTextSource("remote", "another computer (home.example)") { ProviderKind = "home-server" };
        var log = new ActivityLog(null, _time.Now);
        var engine = new WalletEngine(_store, [], log, _ui, _time, [remote]);
        _store.Update(s => s.Chain(Vocabulary.Generate).Stops = [new ChainStop { SourceId = remote.Id }]);
        const string manifest = """
            {"byoa":"0.1-draft","purpose":{"summary":"Remote model test","limits":{"maxTextChars":8000,"maxCallsPerMinute":40}},
            "skills":{"text.generate":{"required":true}},"privacy":"local-only","expectedCost":{"perSession":"0.05 USD"}}
            """;

        var result = await engine.HandleAsync(Clips.Request(Site, click: true, manifest));
        Assert.NotNull(result);
        var row = Assert.Single(_ui.Consents[^1].Rows);
        var route = Assert.Single(row.Routes);
        Assert.Equal("another computer (home.example)", route.Where);
        Assert.True(row.CanServe);
    }

    [Fact]
    public async Task ThePageParamsReachTheSource()
    {
        var grant = await GrantAsync();
        await _engine.HandleAsync(TextCall(Site, grant, new JsonObject
        {
            ["maxTokens"] = 64,
            ["temperature"] = 0.2,
            ["language"] = "de-DE",
        }));
        Assert.Equal(64, _first.LastRequest!.MaxTokens);
        Assert.Equal(0.2, _first.LastRequest.Temperature);
        Assert.Equal("de", _first.LastRequest.Language);          // the primary tag
        Assert.Equal("de", _log.Recent()[0].Language);
    }

    [Fact]
    public async Task ThinkingIsOnUnlessThePageOrTheVisitorTurnsItOff()
    {
        var grant = await GrantAsync();
        await _engine.HandleAsync(TextCall(Site, grant));
        Assert.True(_first.LastRequest!.Thinking);                                          // the default: the model decides

        foreach (var off in new JsonNode[] { false, "off", "NO", "none" })
        {
            _first.Answers.Enqueue("Paris");
            await _engine.HandleAsync(TextCall(Site, grant, new JsonObject { ["thinking"] = off.DeepClone() }));
            Assert.False(_first.LastRequest.Thinking);                                      // the page asked for an answer at once
        }

        _first.Answers.Enqueue("Paris");
        await _engine.HandleAsync(TextCall(Site, grant, new JsonObject { ["thinking"] = true }));
        Assert.True(_first.LastRequest.Thinking);

        _store.Update(s => s.Source(_first.Id).Thinking = false);
        _first.Answers.Enqueue("Paris");
        await _engine.HandleAsync(TextCall(Site, grant, new JsonObject { ["thinking"] = true }));
        Assert.False(_first.LastRequest.Thinking);                                          // the visitor's switch wins over a page that wants thinking

        _first.Answers.Enqueue("Paris");
        await _engine.HandleAsync(TextCall(Site, grant));
        Assert.False(_first.LastRequest.Thinking);
        Assert.True(new SourceSetting().Thinking);                                          // a new or an old setting thinks by default
    }

    // ------------------------------------------------------------------ the caps

    [Fact]
    public async Task BadInputsAreRefusedBeforeAnySourceRuns()
    {
        var grant = await GrantAsync();

        Assert.False(Ok(await _engine.HandleAsync(TextCall(Site, grant, messages: []))));
        Assert.Equal("no messages in the input", _log.Recent()[0].Note);

        var many = Messages(Enumerable.Range(0, 129).Select(i => ("user", "hi " + i)).ToArray());
        Assert.False(Ok(await _engine.HandleAsync(TextCall(Site, grant, messages: many))));
        Assert.Equal("too many messages: the wallet takes at most 128", _log.Recent()[0].Note);

        Assert.False(Ok(await _engine.HandleAsync(TextCall(Site, grant, messages: Messages(("tool", "hi"))))));
        Assert.Contains("message role not allowed", _log.Recent()[0].Note);

        Assert.False(Ok(await _engine.HandleAsync(TextCall(Site, grant, messages: Messages(("user", ""))))));
        Assert.Equal("the messages are empty", _log.Recent()[0].Note);

        var long_ = new string('x', 100_001);
        Assert.False(Ok(await _engine.HandleAsync(TextCall(Site, grant, messages: Messages(("user", long_))))));
        Assert.Equal("text too long for the wallet: at most 100000 characters", _log.Recent()[0].Note);

        Assert.Equal(0, _first.Calls);                            // the sources never saw any of it
        Assert.All(_log.Recent().Take(5), e => Assert.Equal("invalid", e.Outcome));
    }

    [Fact]
    public async Task ChoiceParamsAreCapped()
    {
        var grant = await GrantAsync();

        Assert.False(Ok(await _engine.HandleAsync(TextCall(Site, grant, new JsonObject
        {
            ["choices"] = new JsonArray(Enumerable.Range(0, 21).Select(i => (JsonNode)("c" + i)).ToArray()),
        }))));
        Assert.Equal("too many choices: at most 20", _log.Recent()[0].Note);

        Assert.False(Ok(await _engine.HandleAsync(TextCall(Site, grant, new JsonObject { ["choices"] = new JsonArray() }))));
        Assert.Equal("no usable choices in the params", _log.Recent()[0].Note);

        Assert.False(Ok(await _engine.HandleAsync(TextCall(Site, grant, new JsonObject { ["choices"] = new JsonArray("") }))));
        Assert.Equal("a choice is empty or longer than 100 characters", _log.Recent()[0].Note);

        Assert.False(Ok(await _engine.HandleAsync(TextCall(Site, grant, new JsonObject
        {
            ["choices"] = new JsonArray(new string('x', 101)),
        }))));
        Assert.Equal("a choice is empty or longer than 100 characters", _log.Recent()[0].Note);

        var fat = Enumerable.Range(0, 10).Select(i => new string('x', 60));
        Assert.False(Ok(await _engine.HandleAsync(TextCall(Site, grant, new JsonObject
        {
            ["choices"] = new JsonArray(fat.Select(f => (JsonNode)f).ToArray()),
        }))));
        Assert.Equal("the choices together are longer than 500 characters", _log.Recent()[0].Note);
        Assert.Equal(0, _first.Calls);
    }

    [Fact]
    public async Task TheGateLimitsWhatTheWalletForwards()
    {
        const string tight = """
            {"byoa":"0.1-draft","purpose":{"summary":"Family quiz","limits":{"maxTextChars":10}},
            "skills":{"text.generate":{"required":true}},"privacy":"personal-voice","expectedCost":{"perSession":"0.05 USD"}}
            """;
        var grant = await GrantAsync(tight);
        Assert.False(Ok(await _engine.HandleAsync(TextCall(Site, grant))));
        Assert.Equal("text-too-long", _log.Recent()[0].Note);
        Assert.Equal(0, _first.Calls);
    }

    [Fact]
    public async Task OutOfRangeParamsAreClampedNotRefused()
    {
        var grant = await GrantAsync();
        await _engine.HandleAsync(TextCall(Site, grant, new JsonObject
        {
            ["maxTokens"] = 100_000,
            ["temperature"] = 5.5,
        }));
        Assert.Equal(20_000, _first.LastRequest!.MaxTokens);
        Assert.Equal(1.0, _first.LastRequest.Temperature);        // both end at the wallet's own bounds
    }

    // ------------------------------------------------------------------ choices

    [Fact]
    public async Task MessyChoiceAnswersAreMatchedToThePageChoices()
    {
        _first.Answers = new Queue<string>(["  \"YES\"! "]);      // quotes, spaces, punctuation, upper case
        var grant = await GrantAsync();
        var result = await _engine.HandleAsync(TextCall(Site, grant, new JsonObject
        {
            ["choices"] = new JsonArray("yes", "no"),
        }));
        Assert.Equal("yes", result!["output"]!["text"]!.GetValue<string>());
        Assert.Equal(1, _first.Calls);
    }

    [Fact]
    public async Task AChoiceMissGetsOneRetryWithTheInstructionThenTheNextSource()
    {
        _first.Answers = new Queue<string>(["maybe", " NO. "]);   // first: no match; retry: "no" once the dressing is off
        var grant = await GrantAsync();
        var result = await _engine.HandleAsync(TextCall(Site, grant, new JsonObject
        {
            ["choices"] = new JsonArray("yes", "no"),
        }));
        Assert.Equal("no", result!["output"]!["text"]!.GetValue<string>());
        Assert.Equal(2, _first.Calls);
        var retry = _first.LastRequest!;
        var instruction = Assert.Single(retry.Messages.Skip(1));  // the original messages plus one system line
        Assert.Equal("system", instruction.Role);
        Assert.Contains("\"yes\" | \"no\"", instruction.Content);
        Assert.Equal("""{"ok":true,"output":{"text":"no"},"receipt":{"cost":"0.000 USD","provider":"local"}}""", result.ToJsonString());
    }

    [Fact]
    public async Task AChoiceMissChargesBothPaidAttemptsAndMovesTheDailyTotals()
    {
        _store.Update(s => s.Chain(Vocabulary.Generate).Stops = [new ChainStop { SourceId = _paid.Id, AskBeforePaid = false }]);
        _paid.Answers = new Queue<string>(["maybe", "still maybe"]);
        var grant = await GrantAsync();

        var result = await _engine.HandleAsync(TextCall(Site, grant, new JsonObject
        {
            ["choices"] = new JsonArray("yes", "no"),
        }));

        Assert.False(Ok(result));
        Assert.Equal(2, _paid.Calls);
        var session = _engine.FindGrant(grant)!;
        Assert.Equal(0.008m, session.Spent);
        Assert.Equal(2, session.CallTimes.Count);
        var entry = _log.Recent().Single(e => e.Kind == "call");
        Assert.Equal(2, entry.ProviderCalls);
        Assert.Equal("0.008 USD", entry.Cost);
        Assert.Equal((0.008m, 2), _log.Today(_time.Now, Site));
    }

    [Fact]
    public async Task AChoiceRetryIsIncludedInThePaidQuestionQuote()
    {
        _store.Update(s => s.Chain(Vocabulary.Generate).Stops = [new ChainStop { SourceId = _paid.Id, AskBeforePaid = true }]);
        _paid.Answers = new Queue<string>(["maybe", "yes"]);
        _ui.Paid = PaidAnswer.Once;
        var grant = await GrantAsync();

        var result = await _engine.HandleAsync(TextCall(Site, grant, new JsonObject
        {
            ["choices"] = new JsonArray("yes", "no"),
        }));

        Assert.True(Ok(result));
        Assert.Equal(2, _paid.Calls);
        Assert.Equal(0.008m, _ui.PaidModels.Single().Estimate);
    }

    [Fact]
    public async Task AReportedCostOnAnEmptyPaidAnswerIsSettled()
    {
        _store.Update(s => s.Chain(Vocabulary.Generate).Stops = [new ChainStop { SourceId = _paid.Id, AskBeforePaid = false }]);
        _paid.Outcomes = new Queue<Func<TextRequest, TextAnswer>>([_ => throw new TextSourceException(
            "provider returned an empty answer", cost: 0.006m, responseReceived: true)]);
        var grant = await GrantAsync();

        Assert.False(Ok(await _engine.HandleAsync(TextCall(Site, grant))));
        Assert.Equal(0.006m, _engine.FindGrant(grant)!.Spent);
        Assert.Equal("0.006 USD", _log.Recent().Single(e => e.Kind == "call").Cost);
        Assert.Equal((0.006m, 1), _log.Today(_time.Now, Site));
    }

    [Fact]
    public async Task AnUnknownPaidFailureReturnsMoneyButKeepsItsRateCall()
    {
        _store.Update(s => s.Chain(Vocabulary.Generate).Stops = [new ChainStop { SourceId = _paid.Id, AskBeforePaid = false }]);
        _paid.Outcomes = new Queue<Func<TextRequest, TextAnswer>>([_ => throw new InvalidOperationException("connection failed")]);
        var grant = await GrantAsync();

        Assert.False(Ok(await _engine.HandleAsync(TextCall(Site, grant))));
        var session = _engine.FindGrant(grant)!;
        Assert.Equal(0, session.Spent);
        Assert.Single(session.CallTimes);
        var entry = _log.Recent().Single(e => e.Kind == "call");
        Assert.Equal(1, entry.ProviderCalls);
        Assert.Equal("0 USD", entry.Cost);
        Assert.Equal((0m, 1), _log.Today(_time.Now, Site));
    }

    [Fact]
    public async Task ASettledFailedCallClosesTheDailyMoneyCapBeforeTheNextAttempt()
    {
        _store.Update(s =>
        {
            s.Limits.SitePerDay = "0.005 USD";
            s.Limits.WalletPerDay = "0.005 USD";
            s.Chain(Vocabulary.Generate).Stops = [new ChainStop { SourceId = _paid.Id, AskBeforePaid = false }];
        });
        _paid.Outcomes = new Queue<Func<TextRequest, TextAnswer>>([_ => throw new TextSourceException(
            "empty", cost: 0.006m, responseReceived: true)]);
        var grant = await GrantAsync();

        Assert.False(Ok(await _engine.HandleAsync(TextCall(Site, grant))));
        Assert.False(Ok(await _engine.HandleAsync(TextCall(Site, grant))));
        Assert.Equal(1, _paid.Calls);
        Assert.Equal((0.006m, 1), _log.Today(_time.Now, Site));
        Assert.Equal("money per day", _log.Recent()[0].Note);
    }

    [Fact]
    public async Task NoSourceMatchingTheChoicesIsAPlainError()
    {
        _first.Answers = new Queue<string>(["maybe", "perhaps"]);
        _second.Answers = new Queue<string>(["neither", "nor"]);
        var log = new ActivityLog(null, _time.Now);               // no paid stop here: only the two local ones run
        var engine = new WalletEngine(_store, [], log, _ui, _time, [_first, _second]);
        var request = Assert.IsType<JsonObject>(await engine.HandleAsync(Clips.Request(Site, click: true, QuizManifest)));
        var grant = request["grantId"]!.GetValue<string>();
        Assert.False(Ok(await engine.HandleAsync(TextCall(Site, grant, new JsonObject
        {
            ["choices"] = new JsonArray("yes", "no"),
        }))));
        Assert.Equal("no source answered with one of the choices", log.Recent()[0].Note);
        Assert.Equal(2, _first.Calls);
        Assert.Equal(2, _second.Calls);                           // the second source tried too
        Assert.Contains("FakeText t-first: answered with something else", log.Recent()[0].Fallback);
    }

    [Fact]
    public async Task ATextAnswerLongerThanTheCapIsCut()
    {
        _first.Answers = new Queue<string>([new string('y', 130_000)]);
        var grant = await GrantAsync();
        var result = await _engine.HandleAsync(TextCall(Site, grant));
        Assert.Equal(128_000, result!["output"]!["text"]!.GetValue<string>().Length);
    }

    // ------------------------------------------------------------------ hidden reasoning

    [Fact]
    public async Task HiddenReasoningNeverReachesThePage()
    {
        _first.Answers = new Queue<string>(["<think>let me see, the capital...</think>Paris"]);
        var grant = await GrantAsync();
        var result = await _engine.HandleAsync(TextCall(Site, grant));
        Assert.Equal("Paris", result!["output"]!["text"]!.GetValue<string>());

        _first.Answers = new Queue<string>(["<think>only thinking, never an answer"]);
        var fallback = await _engine.HandleAsync(TextCall(Site, grant));
        Assert.Equal("Lyon", fallback!["output"]!["text"]!.GetValue<string>());
        Assert.Contains("FakeText t-first: answered nothing", _log.Recent()[0].Fallback);
    }

    // ------------------------------------------------------------------ routing

    [Fact]
    public async Task AFailingTextSourceFallsBackToTheNext()
    {
        _first.Fail = true;
        var grant = await GrantAsync();
        var result = await _engine.HandleAsync(TextCall(Site, grant));
        Assert.Equal("Lyon", result!["output"]!["text"]!.GetValue<string>());
        Assert.Equal("FakeText t-first: fake text failure → FakeText t-second", _log.Recent()[0].Fallback);
    }

    [Fact]
    public async Task ATurnedOffTextSourceIsSkipped()
    {
        _store.Update(s => s.Source("t-first").Enabled = false);
        var grant = await GrantAsync();
        await _engine.HandleAsync(TextCall(Site, grant));
        Assert.Equal(0, _first.Calls);
        Assert.Equal("FakeText t-first: turned off → FakeText t-second", _log.Recent()[0].Fallback);
    }

    [Fact]
    public async Task ARequiredSkillWithoutASourceNeverGetsAGrant()
    {
        var log = new ActivityLog(null, _time.Now);
        var engine = new WalletEngine(_store, [], log, _ui, _time, []);
        Assert.Null(await engine.HandleAsync(Clips.Request(Site, click: true, QuizManifest)));
        Assert.True(_ui.Consents[^1].MissingRequired);            // the window says the required skill has no source
        Assert.Equal("declined", log.Recent()[0].Outcome);         // and the page is turned away at the door
    }

    [Fact]
    public async Task ASourceThatKeepsItsAnswerIsSkippedAsBusy()
    {
        _first.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldBusy = WalletEngine.BusyWait;
        WalletEngine.BusyWait = TimeSpan.FromMilliseconds(300);
        try
        {
            var grant = await GrantAsync();
            var held = _engine.HandleAsync(TextCall(Site, grant));
            while (_first.Calls == 0) await Task.Delay(10);
            var second = await _engine.HandleAsync(TextCall(Site, grant));
            Assert.Equal("Lyon", second!["output"]!["text"]!.GetValue<string>());
            Assert.Contains("busy (one call at a time)", _log.Recent()[0].Fallback);
            _first.Hold.SetResult();
            Assert.True((await held)!["ok"]!.GetValue<bool>());
        }
        finally
        {
            WalletEngine.BusyWait = oldBusy;
        }
    }

    [Fact]
    public async Task ASlowTextSourceIsCutOffAfterItsTimeout()
    {
        _first.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var old = WalletEngine.TextSourceTimeout;
        WalletEngine.TextSourceTimeout = TimeSpan.FromMilliseconds(300);
        try
        {
            var grant = await GrantAsync();
            var result = await _engine.HandleAsync(TextCall(Site, grant));
            Assert.Equal("Lyon", result!["output"]!["text"]!.GetValue<string>());
            Assert.Contains("FakeText t-first: took too long", _log.Recent()[0].Fallback);
        }
        finally
        {
            WalletEngine.TextSourceTimeout = old;
        }
    }

    [Fact]
    public async Task CancellingATextCallReturnsItsMoneyAndReleasesTheBusySource()
    {
        _first.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var grant = await GrantAsync();
        using var cancelled = new CancellationTokenSource();
        var call = _engine.HandleAsync(TextCall(Site, grant), cancelled.Token);
        while (_first.Calls == 0) await Task.Delay(10);

        cancelled.Cancel();
        Assert.False(Ok(await call));
        var session = _engine.FindGrant(grant)!;
        Assert.Equal(0, session.Spent);
        Assert.Single(session.CallTimes);                         // the provider was called, so rate limiting remains honest

        _first.Hold = null;
        Assert.True(Ok(await _engine.HandleAsync(TextCall(Site, grant))));
        Assert.Equal(2, _first.Calls);
    }

    // ------------------------------------------------------------------ money

    [Fact]
    public async Task APaidTextFallbackAsksFirst()
    {
        _first.Fail = true;
        _second.Fail = true;
        var grant = await GrantAsync();

        _ui.Paid = PaidAnswer.No;
        Assert.False(Ok(await _engine.HandleAsync(TextCall(Site, grant))));
        Assert.Equal(1, _ui.PaidQuestions);
        Assert.Equal(0, _paid.Calls);
        Assert.Equal("refused", _log.Recent()[0].Outcome);

        _ui.Paid = PaidAnswer.Once;
        var result = Assert.IsType<JsonObject>(await _engine.HandleAsync(TextCall(Site, grant)));
        Assert.Equal("api-key", result["receipt"]!["provider"]!.GetValue<string>());
        Assert.Equal("0.004 USD", result["receipt"]!["cost"]!.GetValue<string>());
        Assert.Equal("0.004 USD", _log.Recent()[0].Cost);
        Assert.Equal(2, _ui.PaidQuestions);

        _ui.Paid = PaidAnswer.Always;
        var withAlways = await _engine.HandleAsync(TextCall(Site, grant));
        Assert.True(Ok(withAlways));
        Assert.Equal(3, _ui.PaidQuestions);                       // "always" starts with one last question…
        await _engine.HandleAsync(TextCall(Site, grant));
        Assert.Equal(3, _ui.PaidQuestions);                       // …and is then remembered for the site
    }

    [Fact]
    public async Task TheSessionBudgetStopsAPaidSourceWithoutAskingAgain()
    {
        var expensive = new FakeTextSource("t-expensive", Runs.ApiKey, costPerCall: 0.03m) { Answers = new Queue<string>(["Paris"]) };
        var log = new ActivityLog(null, _time.Now);
        var engine = new WalletEngine(_store, [], log, _ui, _time, [expensive]);
        _store.Update(s => Router.EnsureTextChain(s, [expensive]));
        var request = Assert.IsType<JsonObject>(await engine.HandleAsync(Clips.Request(Site, click: true, QuizManifest)));
        var grant = request["grantId"]!.GetValue<string>();

        _ui.Paid = PaidAnswer.Always;
        Assert.True(Ok(await engine.HandleAsync(TextCall(Site, grant))));
        Assert.False(Ok(await engine.HandleAsync(TextCall(Site, grant))));
        Assert.Equal("limit", log.Recent()[0].Outcome);           // the wallet never spends past the budget silently
        Assert.Equal(1, _ui.PaidQuestions);
    }
}
