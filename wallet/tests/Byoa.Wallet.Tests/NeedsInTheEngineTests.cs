using System.Text.Json.Nodes;
using Byoa.Wallet.Activity;
using Byoa.Wallet.Engine;
using Byoa.Wallet.Protocol;
using Byoa.Wallet.Settings;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Tests;

/// <summary>A public list of models with the modalities of each, for the tests that ask what a model can do.</summary>
sealed class ListedCatalog(params CatalogModel[] models) : IModelCatalog
{
    public bool Loaded => true;

    public CatalogModel? Find(string? name) => models.FirstOrDefault(m => m.Canonical == ModelNames.Canon(name));

    public IReadOnlyList<CatalogModel> Search(string? query, CatalogFilter filter = CatalogFilter.All, int limit = 40) =>
        models.Where(m => m.OffersText).Take(limit).ToList();
}

/// <summary>The engine with a site that asks for models by what they can do: the list the window shows, the grant, a stored choice, and every call.</summary>
public class NeedsInTheEngineTests
{
    const string Site = "https://icons.example.org";

    readonly ManualTime _time = new();
    readonly ScriptedUi _ui = new();
    readonly FakeTextSource _gemma = new("lm:gemma") { Answers = new Queue<string>(Enumerable.Repeat("gemma says 60", 50)) };
    readonly FakeTextSource _gpt = new("openrouter:openai/gpt-5-mini", Runs.ApiKey, costPerCall: 0.004m) { Answers = new Queue<string>(Enumerable.Repeat("gpt says 80", 50)) };
    readonly FakeTextSource _small = new("openrouter:mistralai/small", Runs.ApiKey, costPerCall: 0.001m) { Answers = new Queue<string>(Enumerable.Repeat("small says 70", 50)) };
    readonly FakeTextSource _pic = new("openrouter:google/gemini-3.1-flash-image", Runs.ApiKey) { Answers = new Queue<string>(Enumerable.Repeat("pic says 90", 50)) };
    readonly SettingsStore _store = new(null);
    readonly ActivityLog _log;
    readonly WalletEngine _engine;

    public NeedsInTheEngineTests()
    {
        _log = new ActivityLog(null, _time.Now);
        _engine = new WalletEngine(_store, [], _log, _ui, _time, [_gemma, _gpt, _small, _pic])
        {
            Catalog = new ListedCatalog(
                new CatalogModel("openai/gpt-5-mini", "OpenAI: GPT-5 Mini", 400_000, null, null, 0, ["text", "image"], ["text"]),
                new CatalogModel("mistralai/small", "Mistral: Small", 32_768, null, null, 0, ["text"], ["text"]),
                new CatalogModel("google/gemini-3.1-flash-image", "Google: Gemini Image", 32_768, null, null, 0, ["text", "image"], ["text", "image"])),
        };
        _store.Update(s =>
        {
            s.Sources.Add(new SourceSetting { Id = "lm:gemma", Kind = SourceKinds.LmStudio, Model = "google/gemma-3-1b-it", Skills = [Vocabulary.Generate] });
            s.Sources.Add(new SourceSetting { Id = "openrouter:openai/gpt-5-mini", Kind = SourceKinds.OpenRouter, Model = "openai/gpt-5-mini", Skills = [Vocabulary.Generate], Offer = false });
            s.Sources.Add(new SourceSetting { Id = "openrouter:mistralai/small", Kind = SourceKinds.OpenRouter, Model = "mistralai/small", Skills = [Vocabulary.Generate], Offer = false });
            s.Sources.Add(new SourceSetting { Id = "openrouter:google/gemini-3.1-flash-image", Kind = SourceKinds.OpenRouter, Model = "google/gemini-3.1-flash-image", Skills = [Vocabulary.Generate] });
            Router.EnsureTextChain(s, [_gemma, _gpt, _small, _pic]);
            var stops = s.Chain(Vocabulary.Generate).Stops;
            stops.RemoveAll(x => x.SourceId == _pic.Id);
            stops.Insert(0, new ChainStop { SourceId = _pic.Id });         // the picture model is first on his route, as a visitor who just added it would have it
        });
    }

    static string Manifest(string? models, string summary = "Icon designer") =>
        "{\"byoa\":\"0.1-draft\",\"purpose\":{\"summary\":\"" + summary + "\"},"
        + "\"skills\":{\"text.generate\":{\"required\":true" + (models is null ? "" : ",\"models\":" + models) + "}},\"privacy\":\"personal\",\"expectedCost\":{\"perSession\":\"0.05 USD\"}}";

    async Task<JsonObject?> RequestAsync(string manifest) =>
        await _engine.HandleAsync(Clips.Request(Site, click: true, manifest)) as JsonObject;

    ModelSection LastSection() => Assert.IsType<ModelSection>(_ui.Consents.Last().Rows.Single().Models);

    static string[] Keys(IEnumerable<ModelChoice> choices) => choices.Select(c => c.Key).ToArray();

    static WireMessage Call(string grant, string? model, string text = "Draw a cat") =>
        new(2, Wire.Call, Site, false, new JsonObject
        {
            ["grantId"] = grant,
            ["skill"] = "text.generate",
            ["input"] = new JsonObject { ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = text }) },
            ["params"] = model is null ? new JsonObject() : new JsonObject { ["model"] = model },
        });

    static bool Ok(JsonNode? result) => result is JsonObject o && o["ok"]?.GetValue<bool>() == true;

    // ------------------------------------------------------------------ the primary-role rule

    [Fact]
    public async Task AModelThatDrawsPicturesIsNotInTheTextListOfAnySiteAndTheWindowCountsItWithItsReason()
    {
        await RequestAsync(Manifest("{\"count\":{\"min\":1,\"max\":3}}"));
        var section = LastSection();
        Assert.DoesNotContain("openrouter:google/gemini-3.1-flash-image", Keys(section.Choices));
        var miss = Assert.Single(section.Misses);
        Assert.Equal("Gemini Image", miss.Name);
        Assert.Contains("draws pictures", miss.Why);
        Assert.Contains("Also use as a text model", miss.Why);                  // and says what would change it
    }

    [Fact]
    public async Task TheVisitorsTickBringsItBackAsAnOrdinaryTextModelAndTheCallReachesIt()
    {
        _store.Update(s => s.Source("openrouter:google/gemini-3.1-flash-image").AlsoText = true);
        var granted = await RequestAsync(Manifest("{\"count\":1,\"require\":[\"google/gemini-3.1-flash-image\"]}"));
        var section = LastSection();
        var choice = section.Choices.Single(c => c.Key == "openrouter:google/gemini-3.1-flash-image");
        Assert.True(choice.Checked);
        Assert.Equal(FitState.Fits, choice.Fit);
        Assert.Empty(section.Misses);
        Assert.True(Ok(await _engine.HandleAsync(Call(granted!["grantId"]!.GetValue<string>(), "m1"))));
        Assert.Equal(1, _pic.Calls);
    }

    [Fact]
    public async Task ASiteThatNamedNoModelsNeverReachesAPictureModelEitherAndSeesItAgainWhenTheVisitorTicks()
    {
        var granted = await RequestAsync(Manifest(null));                       // the old kind of site: the skill and the wallet's route
        var grant = granted!["grantId"]!.GetValue<string>();
        var first = Assert.IsType<JsonObject>(await _engine.HandleAsync(Call(grant, null)));
        Assert.True(Ok(first));
        Assert.Equal("gemma says 60", first["output"]!["text"]!.GetValue<string>());       // the picture model is first on the route and is passed over
        Assert.Equal(0, _pic.Calls);

        _store.Update(s => s.Source("openrouter:google/gemini-3.1-flash-image").AlsoText = true);
        var second = Assert.IsType<JsonObject>(await _engine.HandleAsync(Call(grant, null)));
        Assert.Equal("pic says 90", second["output"]!["text"]!.GetValue<string>());        // the tick counts at once, at the next call
    }

    [Fact]
    public async Task ASitesNeedsCannotBringAPictureModelBackIntoATextList()
    {
        await RequestAsync(Manifest("{\"count\":1,\"kind\":\"any\",\"onMismatch\":\"show\",\"needs\":{\"output\":[\"text\",\"image\"]}}"));
        var section = LastSection();
        Assert.DoesNotContain("openrouter:google/gemini-3.1-flash-image", Keys(section.Choices));
        Assert.Contains(section.Misses, m => m.Name == "Gemini Image");
    }

    // ------------------------------------------------------------------ unknown properties and mismatches

    [Fact]
    public async Task AModelWhosePropertyIsNotKnownIsOfferedMarkedAndNeverTickedByTheWallet()
    {
        await RequestAsync(Manifest("{\"count\":1,\"kind\":\"any\",\"needs\":{\"input\":[\"text\",\"image\"],\"output\":[\"text\"]}}"));
        var section = LastSection();
        var gemma = section.Choices.Single(c => c.Key == "lm:gemma");
        Assert.Equal(FitState.Unknown, gemma.Fit);                              // nothing is known of a local model's input
        Assert.False(gemma.Checked);
        Assert.Equal(["not known: what it reads"], gemma.Marks);               // (that it writes text is no secret: it was added to write text)
        var gpt = section.Choices.Single(c => c.Key == "openrouter:openai/gpt-5-mini");
        Assert.Equal(FitState.Fits, gpt.Fit);
        Assert.True(gpt.Checked);                                               // the one that surely fits
        Assert.Null(gpt.Marks);
        Assert.Equal(["openrouter:openai/gpt-5-mini"], Keys(section.Choices.Where(c => c.Checked)));
    }

    [Fact]
    public async Task AKnownMismatchIsDroppedByDefaultAndShownWhenTheSiteAsksForThat()
    {
        const string Needs = "\"needs\":{\"input\":[\"text\",\"image\"]}";
        await RequestAsync(Manifest("{\"count\":1," + Needs + "}"));
        var dropped = LastSection();
        Assert.DoesNotContain("openrouter:mistralai/small", Keys(dropped.Choices));
        Assert.Equal("cannot read pictures", dropped.Misses.Single(m => m.Name == "Small").Why);

        _store.Update(s => s.Sites.Clear());
        await RequestAsync(Manifest("{\"count\":1,\"onMismatch\":\"show\"," + Needs + "}"));
        var shown = LastSection();
        var small = shown.Choices.Single(c => c.Key == "openrouter:mistralai/small");
        Assert.Equal(FitState.Mismatch, small.Fit);
        Assert.False(small.Checked);                                            // shown, never ticked
        Assert.Equal(["cannot read pictures"], small.Marks);
        Assert.DoesNotContain(shown.Misses, m => m.Name == "Small");
    }

    [Fact]
    public async Task ARequiredModelWhosePropertyIsNotKnownIsNotMissingAndIsLeftForTheVisitorToTick()
    {
        await RequestAsync(Manifest("{\"count\":1,\"require\":[\"google/gemma-3-1b-it\"],\"needs\":{\"input\":[\"image\"]}}"));
        var shown = Assert.Single(_ui.Consents);
        var section = LastSection();
        Assert.Empty(section.MissingRequired);
        Assert.True(section.CanServe);
        Assert.False(section.Choices.Single(c => c.Key == "lm:gemma").Checked);
        Assert.False(shown.MissingRequired);                                    // the window can offer Allow once he has ticked it
    }

    [Fact]
    public async Task ASiteThatNeedsWhatNoModelOfTheVisitorsHasCannotBeGivenWhatItNeeds()
    {
        await _engine.HandleAsync(Clips.Request(Site, click: true, Manifest("{\"count\":1,\"require\":[\"mistralai/small\"],\"needs\":{\"input\":[\"image\"]}}")));
        var shown = Assert.Single(_ui.Consents);
        Assert.True(shown.MissingRequired);                                     // the only model that matches the name cannot read pictures, and the site wants it dropped
        Assert.Equal(["mistralai/small"], shown.Rows.Single().Models!.MissingRequired);
    }

    // ------------------------------------------------------------------ a stored choice, a grant and a call look again

    [Fact]
    public async Task AChangedNeedsIsTheSiteAskingForMoreAndIsAskedAboutEvenWhenTheSiteIsDecidedOnItsOwn()
    {
        _ui.Consent = new ConsentAnswer(true, 0.05m, AutoDecide: true, []);
        await RequestAsync(Manifest("{\"count\":1,\"needs\":{\"input\":[\"text\"]}}"));
        Assert.Single(_ui.Consents);
        await RequestAsync(Manifest("{\"count\":1,\"needs\":{\"input\":[\"text\"]}}"));
        Assert.Single(_ui.Consents);                                            // the same ask: granted quietly

        await RequestAsync(Manifest("{\"count\":1,\"needs\":{\"input\":[\"text\"],\"context\":64}}"));
        Assert.Equal(2, _ui.Consents.Count);                                    // a new key
        await RequestAsync(Manifest("{\"count\":1,\"needs\":{\"input\":[\"text\"],\"context\":65}}"));
        Assert.Equal(3, _ui.Consents.Count);                                    // a raised minimum
        await RequestAsync(Manifest("{\"count\":1,\"needs\":{\"input\":[\"text\"],\"context\":65},\"kind\":\"any\"}"));
        Assert.Equal(4, _ui.Consents.Count);                                    // a new kind
    }

    [Fact]
    public async Task ASiteTheVisitorAnsweredBeforeWalletZeroThirtySixStillGetsItsGrantWithoutNeeds()
    {
        // A site stored by an older wallet has no stored ask; a manifest that asks nothing more than names and a count equals it.
        _ui.Consent = new ConsentAnswer(true, 0.05m, AutoDecide: true, []);
        await RequestAsync(Manifest("{\"count\":1,\"recommend\":[\"openai/gpt-5-mini\"]}"));
        Assert.Null(_store.Read(s => s.Site(Site)!.ModelsFor("text.generate")!.Ask));
        await RequestAsync(Manifest("{\"count\":1,\"recommend\":[\"openai/gpt-5-mini\"]}"));
        Assert.Single(_ui.Consents);
    }

    [Fact]
    public async Task AStoredChoiceThatNoLongerFitsIsDroppedFromTheGrantAndTheVisitorIsAskedAgain()
    {
        _ui.Consent = new ConsentAnswer(true, 0.05m, AutoDecide: true, [], new Dictionary<string, IReadOnlyList<string>>
        {
            ["text.generate"] = ["openrouter:openai/gpt-5-mini"],
        });
        const string Models = "{\"count\":1,\"names\":true,\"needs\":{\"input\":[\"text\",\"image\"]}}";
        var granted = await RequestAsync(Manifest(Models));
        Assert.True(granted is not null, string.Join(" | ", _log.Recent().Select(e => e.Outcome + ": " + e.Note)));
        Assert.Equal("openai/gpt-5-mini", granted!["models"]!["text.generate"]![0]!["id"]!.GetValue<string>());

        // The visitor says this model reads text only: it does not fit any more.
        _store.Update(s => s.Source("openrouter:openai/gpt-5-mini").Inputs = ["text"]);
        _ui.Consent = null;
        var again = await RequestAsync(Manifest(Models));
        Assert.Equal(2, _ui.Consents.Count);                                    // asked again, not decided on its own
        var section = LastSection();
        Assert.DoesNotContain("openrouter:openai/gpt-5-mini", Keys(section.Choices));
        Assert.NotNull(again);
    }

    [Fact]
    public async Task ALiveGrantLosesAModelWhenTheVisitorChangesWhatItCanDoAndACallToItIsRefused()
    {
        _ui.Consent = new ConsentAnswer(true, 0.05m, false, [], new Dictionary<string, IReadOnlyList<string>>
        {
            ["text.generate"] = ["openrouter:openai/gpt-5-mini", "lm:gemma"],
        });
        var granted = await RequestAsync(Manifest("{\"count\":{\"min\":1,\"max\":2},\"needs\":{\"input\":[\"text\",\"image\"]},\"onMismatch\":\"drop\"}"));
        var grant = granted!["grantId"]!.GetValue<string>();
        Assert.Equal(2, granted["models"]!["text.generate"]!.AsArray().Count);        // (gemma's input is unknown: given, with nothing said about it)

        _store.Update(s => s.Source("openrouter:openai/gpt-5-mini").Inputs = ["text"]);
        Assert.False(Ok(await _engine.HandleAsync(Call(grant, "m1"))));            // the call looks again: gpt-5-mini cannot read pictures now
        Assert.Equal(0, _gpt.Calls);
        Assert.True(Ok(await _engine.HandleAsync(Call(grant, "m2"))));             // the other is untouched
        var nameless = Assert.IsType<JsonObject>(await _engine.HandleAsync(Call(grant, null)));
        Assert.True(Ok(nameless));
        Assert.Equal("gemma says 60", nameless["output"]!["text"]!.GetValue<string>());     // without a name only the models that still fit are tried
        Assert.Equal(0, _gpt.Calls);
    }

    [Fact]
    public async Task APictureModelTheVisitorTickedForTextStopsAnsweringTextTheMomentHeUnticksItEvenInAnOpenGrant()
    {
        _store.Update(s => s.Source("openrouter:google/gemini-3.1-flash-image").AlsoText = true);
        _ui.Consent = new ConsentAnswer(true, 0.05m, false, [], new Dictionary<string, IReadOnlyList<string>>
        {
            ["text.generate"] = ["openrouter:google/gemini-3.1-flash-image"],
        });
        var granted = await RequestAsync(Manifest("{\"count\":1}"));
        var grant = granted!["grantId"]!.GetValue<string>();
        Assert.True(Ok(await _engine.HandleAsync(Call(grant, "m1"))));
        Assert.Equal(1, _pic.Calls);

        _store.Update(s => s.Source("openrouter:google/gemini-3.1-flash-image").AlsoText = false);
        Assert.False(Ok(await _engine.HandleAsync(Call(grant, "m1"))));
        Assert.Equal(1, _pic.Calls);
    }

    // ------------------------------------------------------------------ what the window and the Sites page can say

    [Fact]
    public async Task TheNamesTheSiteLikesThatNothingOfTheVisitorsAnswersToAreExamplesAndTheOnesHeHasOrWasDroppedAreNot()
    {
        // gpt-5-mini is his (ticked or not), gemini-image is his but left out for text, the others are nothing of his.
        await RequestAsync(Manifest("{\"count\":1,\"recommend\":[\"openai/gpt-5-mini\",\"google/gemini-3.1-flash-image\",\"anthropic/claude-sonnet-5.5\",\"x-ai/grok-*\"]}"));
        var section = LastSection();
        Assert.Equal(["anthropic/claude-sonnet-5.5", "x-ai/grok-*"], section.ExampleNames);
        Assert.Single(section.Misses);
    }

    [Fact]
    public async Task TheSitesPageIsToldWhatIsAmissWithTheModelsASiteWasGiven()
    {
        _ui.Consent = new ConsentAnswer(true, 0.05m, false, [], new Dictionary<string, IReadOnlyList<string>>
        {
            ["text.generate"] = ["openrouter:openai/gpt-5-mini", "lm:gemma"],
        });
        await RequestAsync(Manifest("{\"count\":{\"min\":1,\"max\":2},\"needs\":{\"input\":[\"text\",\"image\"]},\"onMismatch\":\"show\"}"));
        Assert.Equal(["lm:gemma"], _engine.SiteModelNotes(Site).Keys);                            // gemma's input is not known; gpt-5-mini reads pictures
        Assert.Equal("not known: what it reads", _engine.SiteModelNotes(Site)["lm:gemma"]);

        _store.Update(s => s.Source("openrouter:openai/gpt-5-mini").Inputs = ["text"]);
        Assert.Equal("cannot read pictures", _engine.SiteModelNotes(Site)["openrouter:openai/gpt-5-mini"]);       // shown to the site, with the mismatch

        _store.Update(s => s.Source("openrouter:openai/gpt-5-mini").Inputs = null);
        _store.Update(s => s.Source("openrouter:google/gemini-3.1-flash-image").AlsoText = true);
        _engine.SetSiteModels(Site, Vocabulary.Generate, ["openrouter:google/gemini-3.1-flash-image"]);
        _store.Update(s => s.Source("openrouter:google/gemini-3.1-flash-image").AlsoText = false);
        Assert.StartsWith("not given to the site: draws pictures", _engine.SiteModelNotes(Site)["openrouter:google/gemini-3.1-flash-image"]);
    }

    [Fact]
    public void WhatTheWalletKnowsOfASourceIsAskedOfTheEngine()
    {
        var facts = _engine.FactsOfSource("openrouter:openai/gpt-5-mini")!;
        Assert.Equal(["text", "image"], facts.Input);
        Assert.Equal(400, facts.ContextThousands);
        Assert.Equal("api-key", facts.Runs);
        Assert.Null(_engine.FactsOfSource("lm:gemma")!.Input);                                  // a local copy is not looked up in the list
        Assert.Equal("local", _engine.FactsOfSource("lm:gemma")!.Runs);
        Assert.Null(_engine.FactsOfSource("no:such"));
    }

    [Fact]
    public void WherePickingAModelForATextJobAModelThatDrawsPicturesIsOfferedOnlyWhenTheVisitorSaidItMayAnswerText()
    {
        Assert.True(_engine.ServesText("lm:gemma"));
        Assert.True(_engine.ServesText("openrouter:openai/gpt-5-mini"));
        Assert.False(_engine.ServesText("openrouter:google/gemini-3.1-flash-image"));
        Assert.False(_engine.ServesText("no:such"));
        _store.Update(s => s.Source("openrouter:google/gemini-3.1-flash-image").AlsoText = true);
        Assert.True(_engine.ServesText("openrouter:google/gemini-3.1-flash-image"));
    }

    // ------------------------------------------------------------------ what the site is told

    [Fact]
    public async Task TheGrantTellsEachModelTheVerdictsOnTheConditionsTheSiteAskedAboutAndNeverAValue()
    {
        _ui.Consent = new ConsentAnswer(true, 0.05m, false, [], new Dictionary<string, IReadOnlyList<string>>
        {
            ["text.generate"] = ["openrouter:openai/gpt-5-mini", "lm:gemma"],
        });
        var granted = await RequestAsync(Manifest("{\"count\":{\"min\":1,\"max\":2},\"names\":true,\"kind\":\"any\",\"onMismatch\":\"show\","
            + "\"needs\":{\"input\":[\"text\",\"image\"],\"output\":[\"text\"],\"context\":64,\"runs\":[\"api-key\"]}}"));
        Assert.Equal(["needs", "can", "kind", "cancel"], granted!["capabilities"]!.AsArray().Select(c => c!.GetValue<string>()));
        var list = granted["models"]!["text.generate"]!.AsArray();
        Assert.Equal("{\"input\":true,\"output\":true,\"context\":true,\"runs\":true}", list[0]!["can"]!.ToJsonString());
        // A local model: what it reads and how much it reads are not known, so those keys are not there; it writes text (it was added for that); where it runs is known, and is not what was asked.
        Assert.Equal("{\"output\":true,\"runs\":false}", list[1]!["can"]!.ToJsonString());
        foreach (var entry in list)
        {
            var can = entry!["can"]!.AsObject();
            Assert.True(can.Count <= 4);
            Assert.All(can, pair => Assert.True(pair.Value is JsonValue v && v.TryGetValue<bool>(out _)));          // verdicts, never a number or a word
        }
    }

    [Fact]
    public async Task ASiteThatAskedNothingAboutPropertiesIsToldNothingButTheFeaturesOfTheWallet()
    {
        var granted = await RequestAsync(Manifest("{\"count\":1,\"names\":true}"));
        Assert.Equal(["needs", "can", "kind", "cancel"], granted!["capabilities"]!.AsArray().Select(c => c!.GetValue<string>()));
        var entry = granted["models"]!["text.generate"]![0]!.AsObject();
        Assert.False(entry.ContainsKey("can"));
        Assert.False(entry.ContainsKey("abilities"));
    }

    [Fact]
    public async Task ASiteWithNoModelsBlockStillLearnsTheFeaturesOfTheWallet()
    {
        var granted = await RequestAsync(Manifest(null));
        Assert.Equal(["needs", "can", "kind", "cancel"], granted!["capabilities"]!.AsArray().Select(c => c!.GetValue<string>()));
        Assert.Null(granted["models"]);
    }

    [Fact]
    public async Task EveryNoOfACallCarriesOneOfTheCoarseReasonsAndNeverTheVisitorsSetup()
    {
        _ui.Consent = new ConsentAnswer(true, 0.05m, false, [], new Dictionary<string, IReadOnlyList<string>> { ["text.generate"] = ["lm:gemma"] });
        var granted = await RequestAsync(Manifest("{\"count\":1,\"names\":true}"));
        var grant = granted!["grantId"]!.GetValue<string>();
        static string? Reason(JsonNode? result) => result is JsonObject { } o && o["ok"]?.GetValue<bool>() == false ? o["reason"]?.GetValue<string>() : "ok";

        _gemma.Fail = true;
        Assert.Equal("failed", Reason(await _engine.HandleAsync(Call(grant, "m1"))));            // the model could not answer: the site is not told so
        _gemma.Fail = false;
        Assert.Equal("failed", Reason(await _engine.HandleAsync(Call(grant, "m9"))));            // no such model
        Assert.Equal("failed", Reason(await _engine.HandleAsync(new WireMessage(3, Wire.Call, Site, false, new JsonObject { ["grantId"] = grant, ["skill"] = "audio.transcribe" }))));
        Assert.Equal("failed", Reason(await _engine.HandleAsync(new WireMessage(4, Wire.Call, Site, false, new JsonObject { ["grantId"] = "no-such-grant", ["skill"] = "text.generate" }))));
        Assert.Equal("ok", Reason(await _engine.HandleAsync(Call(grant, "m1"))));

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Equal("cancelled", Reason(await _engine.HandleAsync(Call(grant, "m1"), cancelled.Token)));
    }

    [Fact]
    public async Task ATextOverTheSitesOwnLimitAndACallOverTheRateAreLimits()
    {
        var manifest = "{\"byoa\":\"0.1-draft\",\"purpose\":{\"summary\":\"Icon designer\",\"limits\":{\"maxTextChars\":30,\"maxCallsPerMinute\":2}},"
            + "\"skills\":{\"text.generate\":{\"required\":true,\"models\":{\"count\":1}}},\"privacy\":\"personal\",\"expectedCost\":{\"perSession\":\"0.05 USD\"}}";
        _ui.Consent = new ConsentAnswer(true, 0.05m, false, [], new Dictionary<string, IReadOnlyList<string>> { ["text.generate"] = ["lm:gemma"] });
        var granted = await RequestAsync(manifest);
        var grant = granted!["grantId"]!.GetValue<string>();

        var tooLong = Assert.IsType<JsonObject>(await _engine.HandleAsync(Call(grant, "m1", new string('x', 31))));
        Assert.False(Ok(tooLong));
        Assert.Equal("limit", tooLong["reason"]!.GetValue<string>());

        Assert.True(Ok(await _engine.HandleAsync(Call(grant, "m1", "one"))));
        Assert.True(Ok(await _engine.HandleAsync(Call(grant, "m1", "two"))));
        var third = Assert.IsType<JsonObject>(await _engine.HandleAsync(Call(grant, "m1", "three")));
        Assert.False(Ok(third));
        Assert.Equal("limit", third["reason"]!.GetValue<string>());
    }

    [Fact]
    public void TheWireFallbackAnswersACallWithAReasonAndARequestWithNothing()
    {
        Assert.Equal("{\"ok\":false,\"reason\":\"failed\"}", Wire.Fallback(Wire.Call)!.ToJsonString());
        Assert.Equal("{\"ok\":false,\"reason\":\"cancelled\"}", Wire.Fallback(Wire.Call, CallReason.Cancelled)!.ToJsonString());
        Assert.Null(Wire.Fallback(Wire.Request));
        Assert.Equal(["timeout", "cancelled", "limit", "failed"], new[] { CallReason.Timeout, CallReason.Cancelled, CallReason.Limit, CallReason.Failed });
    }
}
