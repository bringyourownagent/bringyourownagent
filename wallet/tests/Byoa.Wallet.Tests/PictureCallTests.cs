using System.Text.Json.Nodes;
using Byoa.Wallet.Activity;
using Byoa.Wallet.Engine;
using Byoa.Wallet.Protocol;
using Byoa.Wallet.Providers;
using Byoa.Wallet.Settings;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Tests;

/// <summary>Serving image.generate (PROPOSAL-v3 3.1, 3.2, 4.4 and ADDENDUM-0370-1): what a picture call is checked against, how it is paid for, and what the answer is.</summary>
public class PictureCallTests
{
    const string Site = "https://icons.example.org";
    const int Port = 5396;

    readonly ManualTime _time = new();
    readonly ScriptedUi _ui = new();
    readonly FakeTextSource _gemma = new("lm:gemma") { Answers = new Queue<string>(Enumerable.Repeat("gemma says", 20)) };
    readonly FakeImageSource _nano = new("openrouter:google/nano");                               // writes and draws, reads pictures, 0.004 a request
    readonly FakeImageSource _imagine = new("openrouter:someone/imagine");                        // only draws, reads text only
    readonly FakeImageSource _free = new("openrouter:free/draw", costPerCall: 0);                 // draws and is free
    readonly SettingsStore _store = new(null);
    readonly ActivityLog _log;
    readonly WalletEngine _engine;

    public PictureCallTests()
    {
        _ui.Paid = PaidAnswer.Always;                                // the paid model is allowed unless a test says otherwise
        _log = new ActivityLog(null, _time.Now);
        _engine = new WalletEngine(_store, [], _log, _ui, _time, [_gemma, _nano, _imagine, _free])
        {
            Catalog = new ListedCatalog(
                new CatalogModel("google/nano", "Google: Nano", 32_768, null, null, 0, ["text", "image"], ["text", "image"]),
                new CatalogModel("someone/imagine", "Someone: Imagine", 32_768, null, null, 0, ["text"], ["image"]),
                new CatalogModel("free/draw", "Free: Draw", 32_768, 0m, 0m, 0, ["text", "image"], ["image"])),
        };
        _store.Update(s =>
        {
            s.Sources.Add(new SourceSetting { Id = "lm:gemma", Kind = SourceKinds.LmStudio, Model = "google/gemma-3-1b-it", Skills = [Vocabulary.Generate] });
            foreach (var model in new[] { "google/nano", "someone/imagine", "free/draw" })
                s.Sources.Add(new SourceSetting { Id = "openrouter:" + model, Kind = SourceKinds.OpenRouter, Model = model, Skills = [Vocabulary.Generate], Offer = false });
            Router.EnsureTextChain(s, [_gemma, _nano, _imagine, _free]);
        });
    }

    const string Manifest = """
        {"byoa":"0.1-draft","purpose":{"summary":"Icon designer"},"privacy":"personal","expectedCost":{"perSession":"0.05 USD"},
         "skills":{"text.generate":{"required":true,"models":{"count":1}},
                   "image.generate":{"required":false,"models":{"count":{"min":1,"max":4}}}}}
        """;

    /// <summary>Allows the site with the text model and the three picture models: refs m1 (gemma), m2 (Nano), m3 (Imagine), m4 (Free).</summary>
    async Task<string> GrantAsync(decimal budget = 0.05m, string manifest = Manifest)
    {
        _ui.Consent = new ConsentAnswer(true, budget, false, [], new Dictionary<string, IReadOnlyList<string>>
        {
            ["text.generate"] = ["lm:gemma"],
            ["image.generate"] = ["openrouter:google/nano", "openrouter:someone/imagine", "openrouter:free/draw"],
        });
        var granted = await _engine.HandleAsync(Clips.Request(Site, click: true, manifest)) as JsonObject;
        Assert.NotNull(granted);
        return granted!["grantId"]!.GetValue<string>();
    }

    static JsonObject Input(string prompt = "Draw a fox", int? count = null, string? aspect = null, JsonArray? references = null, int? maxSide = null, int? maxBytes = null)
    {
        var input = new JsonObject { ["prompt"] = prompt };
        if (count is { } c) input["count"] = c;
        if (aspect is not null) input["aspect"] = aspect;
        if (references is not null) input["references"] = references;
        if (maxSide is { } s) input["maxSide"] = s;
        if (maxBytes is { } b) input["maxBytes"] = b;
        return input;
    }

    static WireMessage Call(string grant, string? model, JsonObject input, string skill = "image.generate") =>
        new(2, Wire.Call, Site, false, new JsonObject
        {
            ["grantId"] = grant,
            ["skill"] = skill,
            ["input"] = input,
            ["params"] = model is null ? new JsonObject() : new JsonObject { ["model"] = model },
        });

    async Task<(JsonObject Result, List<byte[]> Pictures)> DrawAsync(string grant, string? model, JsonObject input)
    {
        var pictures = new List<byte[]>();
        var result = Assert.IsType<JsonObject>(await _engine.HandleAsync(Call(grant, model, input), default, pictures));
        return (result, pictures);
    }

    static void AssertFailed(JsonObject result, string reason)
    {
        Assert.False(result["ok"]!.GetValue<bool>());
        Assert.Equal(reason, result["reason"]!.GetValue<string>());
    }

    static JsonArray References(params byte[][] pictures) =>
        new(pictures.Select(p => (JsonNode)new JsonObject { ["mime"] = "image/png", ["data"] = Convert.ToBase64String(p) }).ToArray());

    // ------------------------------------------------------------------ the answer

    [Fact]
    public async Task APictureCallIsAnsweredWithTheDescriptionOfEachPictureAndTheBytesBesideIt()
    {
        var grant = await GrantAsync();
        var png = TestPictures.Png(16, 16);
        _nano.Pictures.Enqueue(_ => new ImageAnswer([png], null));
        var (result, pictures) = await DrawAsync(grant, "m2", Input());

        Assert.True(result["ok"]!.GetValue<bool>());
        var image = Assert.Single(result["output"]!["images"]!.AsArray())!;
        Assert.Equal("image/png", image["mime"]!.GetValue<string>());
        Assert.Equal(png.Length, image["size"]!.GetValue<int>());
        Assert.Equal(TestPictures.Sha256(png), image["sha256"]!.GetValue<string>());
        Assert.Null(image["reduced"]);                                                      // the wallet changed nothing
        Assert.Equal([png], pictures);                                                      // byte for byte
        Assert.Equal("m2", result["receipt"]!["model"]!.GetValue<string>());
        Assert.Equal("api-key", result["receipt"]!["provider"]!.GetValue<string>());
        Assert.Equal("unknown", result["receipt"]!["cost"]!.GetValue<string>());           // the provider said nothing of the cost
        Assert.Null(result["text"]);
        Assert.Equal(1, _nano.ImageCalls);
        Assert.Equal(0, _gemma.Calls);
    }

    [Fact]
    public async Task TheTypeIsFoundInTheBytesAndNeverInWhatTheProviderOrTheSiteSaid()
    {
        var grant = await GrantAsync();
        _free.Pictures.Enqueue(_ => new ImageAnswer([TestPictures.JpegHead(), Enumerable.Repeat((byte)7, 200).ToArray(), TestPictures.Gif(80), TestPictures.Png(4, 4)], null));
        var (result, pictures) = await DrawAsync(grant, "m4", Input(count: 4));
        Assert.True(result["ok"]!.GetValue<bool>());
        // the bytes that are no picture of the four types are dropped; the others go as the types they are
        Assert.Equal(["image/jpeg", "image/gif", "image/png"], result["output"]!["images"]!.AsArray().Select(i => i!["mime"]!.GetValue<string>()));
        Assert.Equal(3, pictures.Count);
    }

    [Fact]
    public async Task AnAnswerWithNoPictureThatIsOneOfTheFourTypesIsAFailure()
    {
        var grant = await GrantAsync();
        _free.Pictures.Enqueue(_ => new ImageAnswer([Enumerable.Repeat((byte)1, 100).ToArray()], null));
        var (result, pictures) = await DrawAsync(grant, "m4", Input());
        AssertFailed(result, "failed");
        Assert.Empty(pictures);
    }

    [Fact]
    public async Task CountMeansUpToThatManyRequestsOneAfterAnotherAndTheReceiptSumsWhatWasReported()
    {
        var grant = await GrantAsync();
        _nano.ReportedCost = 0.01m;
        var (result, pictures) = await DrawAsync(grant, "m2", Input(count: 3));
        Assert.True(result["ok"]!.GetValue<bool>());
        Assert.Equal(3, result["output"]!["images"]!.AsArray().Count);
        Assert.Equal(3, pictures.Count);
        Assert.Equal(3, _nano.ImageCalls);                                                  // one picture a request
        Assert.Equal("0.030 USD", result["receipt"]!["cost"]!.GetValue<string>());
        Assert.All(_nano.ImageRequests, r => Assert.Equal(("Draw a fox", null), (r.Prompt, r.Aspect)));
    }

    [Fact]
    public async Task ARequestThatFailsStopsTheCountAndWhatCameIsTheAnswer()
    {
        var grant = await GrantAsync();
        var ok = TestPictures.Png(4, 4);
        _nano.Pictures.Enqueue(_ => new ImageAnswer([ok], 0.01m));
        _nano.Pictures.Enqueue(_ => throw new TextSourceException("the model gave up", 0.01m, providerCalled: true, responseReceived: true));
        _nano.Pictures.Enqueue(_ => new ImageAnswer([ok], 0.01m));
        var (result, pictures) = await DrawAsync(grant, "m2", Input(count: 3));
        Assert.True(result["ok"]!.GetValue<bool>());                                        // some came: ok with those
        Assert.Single(pictures);
        Assert.Equal(2, _nano.ImageCalls);                                                  // and no third request after the failure
        Assert.Equal("0.020 USD", result["receipt"]!["cost"]!.GetValue<string>());         // the failed try is paid for too
    }

    [Fact]
    public async Task WhenNoPictureCameTheCallFailsAndTheVisitorPaysNothingForARequestThatNeverAnswered()
    {
        var grant = await GrantAsync();
        _nano.Fail = true;
        var session = _engine.FindGrant(grant)!;
        var (result, pictures) = await DrawAsync(grant, "m2", Input());
        AssertFailed(result, "failed");
        Assert.Empty(pictures);
        Assert.Equal(0m, session.Spent);                                                    // the reservation went back
    }

    [Fact]
    public async Task AReportedCostIsWhatTheSessionSpendsAndAnUnreportedOneIsTheEstimate()
    {
        var grant = await GrantAsync();
        var session = _engine.FindGrant(grant)!;
        _nano.ReportedCost = 0.0123m;
        await DrawAsync(grant, "m2", Input());
        Assert.Equal(0.0123m, session.Spent);

        _nano.ReportedCost = null;
        await DrawAsync(grant, "m2", Input());
        Assert.Equal(0.0123m + 0.004m, session.Spent);                                      // nothing reported: the estimate (a paid source's floor)
    }

    // ------------------------------------------------------------------ what a call must be

    [Fact]
    public async Task ACallNeedsAModelOfItsOwnSkillNamedByRef()
    {
        var grant = await GrantAsync();
        AssertFailed((await DrawAsync(grant, null, Input())).Result, "failed");              // the wallet has no picture route of its own
        AssertFailed((await DrawAsync(grant, "m9", Input())).Result, "failed");
        AssertFailed((await DrawAsync(grant, "m1", Input())).Result, "failed");              // m1 is the text model: not in the picture list
        Assert.Equal(0, _nano.ImageCalls + _imagine.ImageCalls + _free.ImageCalls);
    }

    [Fact]
    public async Task ATextModelIsNotReachedByAPictureRefAndAPictureRefIsNotReachedByATextCall()
    {
        var grant = await GrantAsync();
        var text = new WireMessage(2, Wire.Call, Site, false, new JsonObject
        {
            ["grantId"] = grant, ["skill"] = "text.generate",
            ["input"] = new JsonObject { ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hi" }) },
            ["params"] = new JsonObject { ["model"] = "m2" },                                 // a picture ref in a text call
        });
        AssertFailed(Assert.IsType<JsonObject>(await _engine.HandleAsync(text)), "failed");
        Assert.Equal(0, _nano.Calls);
    }

    [Fact]
    public async Task ASiteThatWasNotGivenPicturesCannotAskForThem()
    {
        _ui.Consent = new ConsentAnswer(true, 0.05m, false, ["image.generate"], new Dictionary<string, IReadOnlyList<string>> { ["text.generate"] = ["lm:gemma"] });
        var granted = (JsonObject)(await _engine.HandleAsync(Clips.Request(Site, click: true, Manifest)))!;
        var (result, _) = await DrawAsync(granted["grantId"]!.GetValue<string>(), "m1", Input());
        AssertFailed(result, "failed");
        Assert.Equal(0, _nano.ImageCalls);
    }

    [Fact]
    public async Task ACarrierThatCannotCarryPicturesGetsOkFalseBeforeAnythingIsSpent()
    {
        var grant = await GrantAsync();
        var result = Assert.IsType<JsonObject>(await _engine.HandleAsync(Call(grant, "m2", Input())));        // no collector
        AssertFailed(result, "failed");
        Assert.Equal(0, _nano.ImageCalls);
        Assert.Equal(0m, _engine.FindGrant(grant)!.Spent);
    }

    [Fact]
    public async Task APromptAndTheOtherInputsAreCheckedBeforeAnythingIsSpent()
    {
        var grant = await GrantAsync();
        AssertFailed((await DrawAsync(grant, "m2", new JsonObject())).Result, "failed");                                // no prompt
        AssertFailed((await DrawAsync(grant, "m2", Input(prompt: "  "))).Result, "failed");
        AssertFailed((await DrawAsync(grant, "m2", Input(count: 0))).Result, "failed");
        AssertFailed((await DrawAsync(grant, "m2", Input(count: 5))).Result, "failed");
        AssertFailed((await DrawAsync(grant, "m2", Input(aspect: "2:1"))).Result, "failed");
        AssertFailed((await DrawAsync(grant, "m2", Input(references: References(Enumerable.Repeat((byte)5, 50).ToArray())))).Result, "failed");   // no picture
        var huge = Input(prompt: new string('x', 200_000));
        AssertFailed((await DrawAsync(grant, "m2", huge)).Result, "limit");                                             // over maxTextChars
        Assert.Equal(0, _nano.ImageCalls);
        Assert.Equal(0m, _engine.FindGrant(grant)!.Spent);
    }

    [Theory]
    [InlineData("1:1")]
    [InlineData("4:3")]
    [InlineData("3:4")]
    [InlineData("16:9")]
    [InlineData("9:16")]
    public async Task EachOfTheFiveAspectsGoesToTheModel(string aspect)
    {
        var grant = await GrantAsync();
        var (result, _) = await DrawAsync(grant, "m4", Input(aspect: aspect));
        Assert.True(result["ok"]!.GetValue<bool>());
        Assert.Equal(aspect, Assert.Single(_free.ImageRequests).Aspect);
    }

    // ------------------------------------------------------------------ references

    [Fact]
    public async Task ReferencesGoOnlyToAModelThatReadsPicturesAndAreGivenToItWithTheTypeFoundInTheirBytes()
    {
        var grant = await GrantAsync();
        var reference = TestPictures.Png(4, 4);
        var (result, _) = await DrawAsync(grant, "m2", Input(references: References(reference)));
        Assert.True(result["ok"]!.GetValue<bool>());
        var given = Assert.Single(Assert.Single(_nano.ImageRequests).References);
        Assert.Equal("image/png", given.Mime);
        Assert.Equal(reference, given.Data);

        // Imagine reads text only: the call is refused and the model is never asked
        AssertFailed((await DrawAsync(grant, "m3", Input(references: References(reference)))).Result, "failed");
        Assert.Equal(0, _imagine.ImageCalls);
        var (plain, _) = await DrawAsync(grant, "m3", Input());
        Assert.True(plain["ok"]!.GetValue<bool>());                                         // without references the same model draws
    }

    [Fact]
    public async Task TheLimitsOnReferencesAreFourOfAMillionBytesAndThreeMillionTogether()
    {
        var grant = await GrantAsync();
        var small = TestPictures.Png(4, 4);
        AssertFailed((await DrawAsync(grant, "m2", Input(references: References(small, small, small, small, small)))).Result, "limit");     // five
        Assert.True((await DrawAsync(grant, "m2", Input(references: References(small, small, small, small)))).Result["ok"]!.GetValue<bool>());     // four

        byte[] Sized(int size) => TestPictures.Png(4, 4).Concat(new byte[size - TestPictures.Png(4, 4).Length]).ToArray();
        AssertFailed((await DrawAsync(grant, "m2", Input(references: References(Sized(1_000_001))))).Result, "limit");     // one over a million
        Assert.True((await DrawAsync(grant, "m2", Input(references: References(Sized(1_000_000))))).Result["ok"]!.GetValue<bool>());
        AssertFailed((await DrawAsync(grant, "m2", Input(references: References(Sized(1_000_000), Sized(1_000_000), Sized(1_000_000), Sized(200))))).Result, "limit");     // over three together
        Assert.Equal(2, _nano.ImageCalls);                                                  // only the four small ones and the one of a million bytes were allowed
    }

    // ------------------------------------------------------------------ what the wallet does to a picture

    [Fact]
    public async Task APictureOverTheByteLimitIsDroppedWithoutAShrinkerAndTheCallFailsWithLimit()
    {
        var grant = await GrantAsync();
        var big = TestPictures.Png(400, 400, noise: true);                                // well over the smallest limit a site can ask for
        Assert.True(big.Length > Pictures.MinBytes);
        _free.Pictures.Enqueue(_ => new ImageAnswer([big], null));
        var (result, pictures) = await DrawAsync(grant, "m4", Input(maxBytes: Pictures.MinBytes));
        AssertFailed(result, "limit");
        Assert.Empty(pictures);                                                           // never sent as it is
    }

    [Fact]
    public async Task APictureWithinTheLimitsIsSentByteForByteWithoutAShrinkerWhenTheSiteNamedNoSide()
    {
        var grant = await GrantAsync();
        var png = TestPictures.Png(64, 64);
        _free.Pictures.Enqueue(_ => new ImageAnswer([png], null));
        var (result, pictures) = await DrawAsync(grant, "m4", Input());
        Assert.True(result["ok"]!.GetValue<bool>());
        Assert.Equal(png, Assert.Single(pictures));
        // a site that names a side gets nothing from a wallet that cannot look at the picture
        _free.Pictures.Enqueue(_ => new ImageAnswer([png], null));
        AssertFailed((await DrawAsync(grant, "m4", Input(maxSide: 512))).Result, "limit");
    }

    sealed class ScriptedShrinker(Func<byte[], string, int?, int, ShrunkPicture?> shrink) : IPictureShrinker
    {
        public List<(string Mime, int? MaxSide, int MaxBytes)> Asked { get; } = [];

        public ShrunkPicture? Shrink(byte[] picture, string mime, int? maxSide, int maxBytes)
        {
            Asked.Add((mime, maxSide, maxBytes));
            return shrink(picture, mime, maxSide, maxBytes);
        }
    }

    [Fact]
    public async Task TheShrinkersPictureIsWhatIsSentAndIsMarkedReducedWhileItsSizeAndHashDescribeTheBytesSent()
    {
        var grant = await GrantAsync();
        var big = TestPictures.Png(64, 64);
        var small = TestPictures.Png(16, 16);
        var shrinker = new ScriptedShrinker((_, _, _, _) => new ShrunkPicture(small, true));
        _engine.PictureShrinker = shrinker;
        _free.Pictures.Enqueue(_ => new ImageAnswer([big], null));
        var (result, pictures) = await DrawAsync(grant, "m4", Input(maxSide: 5000, maxBytes: 200_000));       // clamped: 4096 and 200,000
        var image = Assert.Single(result["output"]!["images"]!.AsArray())!;
        Assert.True(image["reduced"]!.GetValue<bool>());
        Assert.Equal(small.Length, image["size"]!.GetValue<int>());
        Assert.Equal(TestPictures.Sha256(small), image["sha256"]!.GetValue<string>());
        Assert.Equal(small, Assert.Single(pictures));
        Assert.Equal(("image/png", 4096, 200_000), Assert.Single(shrinker.Asked));
    }

    [Fact]
    public async Task TheLimitsAreClampedAndTheByteLimitDefaultsToAMillion()
    {
        var grant = await GrantAsync();
        var shrinker = new ScriptedShrinker((p, _, _, _) => new ShrunkPicture(p, false));
        _engine.PictureShrinker = shrinker;
        await DrawAsync(grant, "m4", Input());
        await DrawAsync(grant, "m4", Input(maxSide: 10, maxBytes: 10));
        await DrawAsync(grant, "m4", Input(maxSide: 100_000, maxBytes: 100_000_000));
        Assert.Equal([(null, 1_000_000), (64, 100_000), (4096, 3_000_000)], shrinker.Asked.Select(a => (a.MaxSide, a.MaxBytes)).ToArray());
    }

    [Fact]
    public async Task APictureThatCannotBeMadeToFitIsDroppedAndWhenNoneIsLeftTheCallFailsWithLimit()
    {
        var grant = await GrantAsync();
        _engine.PictureShrinker = new ScriptedShrinker((_, _, _, _) => null);
        var (result, pictures) = await DrawAsync(grant, "m4", Input());
        AssertFailed(result, "limit");
        Assert.Empty(pictures);

        // one of two is dropped: the call is ok with the other
        var keep = TestPictures.Png(8, 8);
        var calls = 0;
        _engine.PictureShrinker = new ScriptedShrinker((p, _, _, _) => ++calls == 1 ? null : new ShrunkPicture(p, false));
        _free.Pictures.Enqueue(_ => new ImageAnswer([TestPictures.Png(9, 9), keep], null));
        var (partial, kept) = await DrawAsync(grant, "m4", Input(count: 2));
        Assert.True(partial["ok"]!.GetValue<bool>());
        Assert.Equal(keep, Assert.Single(kept));
    }

    [Fact]
    public async Task AShrinkerThatCannotReadAPictureDropsItAndTheCallStillAnswers()
    {
        var grant = await GrantAsync();
        _engine.PictureShrinker = new ScriptedShrinker((_, _, _, _) => throw new InvalidDataException("not a picture"));
        _free.Pictures.Enqueue(_ => new ImageAnswer([TestPictures.Png(8, 8)], null));
        AssertFailed((await DrawAsync(grant, "m4", Input())).Result, "failed");
    }

    [Fact]
    public async Task TheAnswerIsAtMostEightMillionBytesAndWhatDoesNotFitIsLeftOut()
    {
        var grant = await GrantAsync();
        _engine.PictureShrinker = new ScriptedShrinker((p, _, _, _) => new ShrunkPicture(p, false));
        byte[] Big(int size) => TestPictures.Png(4, 4).Concat(new byte[size - TestPictures.Png(4, 4).Length]).ToArray();
        _free.Pictures.Enqueue(_ => new ImageAnswer([Big(3_000_000), Big(3_000_000), Big(3_000_000)], null));
        var (result, pictures) = await DrawAsync(grant, "m4", Input(count: 3, maxBytes: 3_000_000));
        Assert.True(result["ok"]!.GetValue<bool>());
        Assert.Equal(2, pictures.Count);                                                  // 6,000,000; the third would make 9,000,000
        Assert.True(pictures.Sum(p => p.Length) <= Pictures.MaxAnswerBytes);
    }

    // ------------------------------------------------------------------ money, rate, time

    [Fact]
    public async Task AtMostTwentyPicturesAMinutePerSiteAndPicturesThatNeverCameAreGivenBack()
    {
        var grant = await GrantAsync();
        for (var i = 0; i < 5; i++) Assert.True((await DrawAsync(grant, "m4", Input(count: 4))).Result["ok"]!.GetValue<bool>());          // 20 pictures
        AssertFailed((await DrawAsync(grant, "m4", Input())).Result, "limit");
        Assert.Equal(20, _free.ImageCalls);
        _time.Now = _time.Now.AddSeconds(61);
        Assert.True((await DrawAsync(grant, "m4", Input())).Result["ok"]!.GetValue<bool>());                                          // a minute later

        // a call that asked for four and got none gave its places back
        _time.Now = _time.Now.AddSeconds(61);
        _free.Fail = true;
        AssertFailed((await DrawAsync(grant, "m4", Input(count: 4))).Result, "failed");
        _free.Fail = false;
        for (var i = 0; i < 5; i++) Assert.True((await DrawAsync(grant, "m4", Input(count: 4))).Result["ok"]!.GetValue<bool>());
    }

    [Fact]
    public async Task APaidModelAsksFirstAndANoStopsTheCallBeforeTheModelIsAsked()
    {
        var grant = await GrantAsync();
        _ui.Paid = PaidAnswer.No;
        var (result, _) = await DrawAsync(grant, "m2", Input(count: 2));
        AssertFailed(result, "failed");
        Assert.Equal(1, _ui.PaidQuestions);
        Assert.Equal(0, _nano.ImageCalls);
        Assert.Equal(0m, _engine.FindGrant(grant)!.Spent);
        var asked = Assert.Single(_ui.PaidModels);
        Assert.Equal(0.008m, asked.Estimate);                                              // two requests at the round figure
    }

    [Fact]
    public async Task AlwaysIsRememberedForTheSiteAndAFreeModelNeverAsks()
    {
        var grant = await GrantAsync();
        _ui.Paid = PaidAnswer.Always;
        Assert.True((await DrawAsync(grant, "m2", Input())).Result["ok"]!.GetValue<bool>());
        Assert.True((await DrawAsync(grant, "m2", Input())).Result["ok"]!.GetValue<bool>());
        Assert.Equal(1, _ui.PaidQuestions);                                                // asked once, then remembered

        _ui.Paid = PaidAnswer.No;
        var other = new FakeImageSource("openrouter:x/other", costPerCall: 0);
        Assert.True((await DrawAsync(grant, "m4", Input())).Result["ok"]!.GetValue<bool>());       // the free model needs no question
        Assert.Equal(1, _ui.PaidQuestions);
    }

    [Fact]
    public async Task APictureThatWouldPassTheBudgetAsksToRaiseItAndStopsAtItWhenTheAnswerIsNo()
    {
        var grant = await GrantAsync(budget: 0.01m);
        _ui.Paid = PaidAnswer.Always;
        _ui.RaisePick = null;
        var (result, pictures) = await DrawAsync(grant, "m2", Input(count: 4));              // 4 x 0.004 = 0.016 > 0.01
        AssertFailed(result, "limit");
        Assert.Empty(pictures);
        Assert.Single(_ui.RaiseModels);
        Assert.Equal(0, _nano.ImageCalls);
    }

    [Fact]
    public async Task TheCallsOwnTimeLimitEndsTheCallWithTimeoutAndTakesBackWhatItHeld()
    {
        var grant = await GrantAsync();
        _ui.Paid = PaidAnswer.Always;
        WalletEngine.TextRouteTimeout = TimeSpan.FromMilliseconds(400);
        try
        {
            _nano.HoldPicture = new TaskCompletionSource();
            var (result, pictures) = await DrawAsync(grant, "m2", Input());
            AssertFailed(result, "timeout");
            Assert.Empty(pictures);
            Assert.Equal(0m, _engine.FindGrant(grant)!.Spent);
        }
        finally
        {
            WalletEngine.TextRouteTimeout = null;
        }
    }

    [Fact]
    public async Task ACancelEndsTheCallAndTheSiteIsToldCancelled()
    {
        var grant = await GrantAsync();
        _ui.Paid = PaidAnswer.Always;
        _nano.HoldPicture = new TaskCompletionSource();
        using var cancel = new CancellationTokenSource();
        var pictures = new List<byte[]>();
        var running = _engine.HandleAsync(Call(grant, "m2", Input()), cancel.Token, pictures);
        await Task.Delay(100);
        cancel.Cancel();
        AssertFailed(Assert.IsType<JsonObject>(await running), "cancelled");
        Assert.Empty(pictures);
        Assert.Equal(0m, _engine.FindGrant(grant)!.Spent);
    }

    [Fact]
    public async Task APictureCallIsLoggedWithItsSkillItsModelAndWhatItCost()
    {
        var grant = await GrantAsync();
        _ui.Paid = PaidAnswer.Always;
        _nano.ReportedCost = 0.02m;
        await DrawAsync(grant, "m2", Input());
        var logged = _log.Recent().First(e => e.Skill == "image.generate");
        Assert.Equal("ok", logged.Outcome);
        Assert.Equal("0.02 USD", logged.Cost);
        Assert.Contains("FakeText openrouter:google/nano", logged.Source);
    }

    // ------------------------------------------------------------------ the real source against a stand-in OpenRouter

    [Fact]
    public async Task ThroughTheKeySourceAndAStandInOpenRouterThePictureArrivesWithTheSameHash()
    {
        var picture = TestPictures.Png(24, 24);
        using var server = new FakeHttp(Port);
        server.Handler = _ => (200, new JsonObject
        {
            ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["images"] = new JsonArray(new JsonObject { ["image_url"] = new JsonObject { ["url"] = TestPictures.DataUrl(picture) } }) } }),
            ["usage"] = new JsonObject { ["cost"] = 0.068 },
        }.ToJsonString());
        var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false });
        var real = new OpenRouterTextSource("google/nano", null, null, http, () => "sk-test", () => true, new Uri(server.Url));
        var store = new SettingsStore(null);
        store.Update(s => s.Sources.Add(new SourceSetting { Id = real.Id, Kind = SourceKinds.OpenRouter, Model = "google/nano", Skills = [Vocabulary.Generate], Offer = false }));
        var engine = new WalletEngine(store, [], _log, _ui, _time, [real])
        {
            Catalog = new ListedCatalog(new CatalogModel("google/nano", "Google: Nano", 32_768, null, null, 0, ["text", "image"], ["text", "image"])),
        };
        _ui.Paid = PaidAnswer.Always;
        _ui.Consent = new ConsentAnswer(true, 0.5m, false, [], new Dictionary<string, IReadOnlyList<string>> { ["image.generate"] = [real.Id] });
        var granted = (JsonObject)(await engine.HandleAsync(Clips.Request(Site, click: true,
            """{"byoa":"0.1-draft","purpose":{"summary":"Icons"},"privacy":"personal","skills":{"image.generate":{"required":true,"models":{"count":1}}}}""")))!;
        var pictures = new List<byte[]>();
        var result = (JsonObject)(await engine.HandleAsync(Call(granted["grantId"]!.GetValue<string>(), "m1", Input(aspect: "1:1")), default, pictures))!;

        Assert.True(result["ok"]!.GetValue<bool>());
        var image = Assert.Single(result["output"]!["images"]!.AsArray())!;
        Assert.Equal(TestPictures.Sha256(picture), image["sha256"]!.GetValue<string>());
        Assert.Equal(picture, Assert.Single(pictures));
        Assert.Equal("0.068 USD", result["receipt"]!["cost"]!.GetValue<string>());
        var sent = JsonNode.Parse(Assert.Single(server.Requests).Body)!;
        Assert.Equal("1:1", sent["image_config"]!["aspect_ratio"]!.GetValue<string>());
    }
}
