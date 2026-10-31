using System.Text.Json.Nodes;
using Byoa.Wallet.Activity;
using Byoa.Wallet.Engine;
using Byoa.Wallet.Protocol;
using Byoa.Wallet.Settings;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Tests;

/// <summary>Names of models, the request of a site for several models, the planner that ticks them, and what the site is given.</summary>
public class ModelNamesTests
{
    [Theory]
    [InlineData("anthropic/claude-opus-4.6", "anthropic/claude-opus-4-6")]
    [InlineData("Meta-Llama/Llama-4-Scout:free", "meta-llama/llama-4-scout")]
    [InlineData("claude-sonnet-4-5-20250929", "claude-sonnet-4-5")]
    [InlineData("openai/gpt-5_mini", "openai/gpt-5-mini")]
    [InlineData("  ", "")]
    public void TwoSpellingsOfOneNameCompareEqual(string name, string canon) => Assert.Equal(canon, ModelNames.Canon(name));

    [Theory]
    [InlineData("openai/gpt-5*", "openai/gpt-5-mini", true)]
    [InlineData("openai/gpt-5*", "openai/gpt-5-4", true)]
    [InlineData("openai/gpt-5*", "openai/gpt-4o", false)]
    [InlineData("anthropic/*", "anthropic/claude-opus-4-6", true)]
    [InlineData("anthropic/*", "openai/gpt-5", false)]
    [InlineData("google/gemma-3-1b-it", "gemma-3-1b-it", true)]           // a local model whose maker is not known: only the model part counts
    [InlineData("google/gemma-3-1b-it", "ggml/gemma-3-1b-it", true)]      // an unknown maker on the model's side does not contradict
    [InlineData("google/gemma-3-1b-it", "qwen/gemma-3-1b-it", false)]      // a known, different maker does
    [InlineData("gemma-3-1b-it", "google/gemma-3-1b-it", true)]
    [InlineData("x-ai/grok-4.1-fast", "x-ai/grok-4-1-fast:free", true)]
    [InlineData("xai/grok-4.1-fast", "x-ai/grok-4.1-fast", true)]
    [InlineData("google/gemma-3-1b-it", "google/gemma-3-12b-it", false)]
    public void ASitesNameOrMaskFindsTheModel(string pattern, string canonical, bool expected) =>
        Assert.Equal(expected, ModelNames.Matches(pattern, canonical));

    [Theory]
    [InlineData("ggml-org/gemma-3-1b-it-GGUF:Q4_K_M", "gemma-3-1b-it")]
    [InlineData("qwen/qwen3.5-4b", "qwen/qwen3-5-4b")]
    [InlineData("unsloth/Qwen3-8B-GGUF:Q8_0", "qwen3-8b")]
    [InlineData("llama3.2:3b", "llama3-2-3b")]
    [InlineData("mistralai/ministral-8b-instruct-2410", "mistralai/ministral-8b-instruct-2410")]
    public void ALocalProgramsIdBecomesTheModelsName(string model, string expected) => Assert.Equal(expected, ModelNames.FromLocal(model));

    [Theory]
    [InlineData("google/gemma-3-1b-it", "google")]
    [InlineData("gemma-3-1b-it", "google")]
    [InlineData("x-ai/grok-4-1-fast", "x-ai")]
    [InlineData("deepseek/deepseek-v4-flash", "deepseek")]
    [InlineData("thedrummer/unslopnemo", "thedrummer")]
    public void EveryModelHasACompany(string canonical, string vendor) => Assert.Equal(vendor, ModelNames.Vendor(canonical));

    [Fact]
    public void TheCatalogNameLosesItsVendorPrefix()
    {
        Assert.Equal("Gemma 3 12B", ModelNames.Display("google/gemma-3-12b-it", "Google: Gemma 3 12B"));
        Assert.Equal("gemma-3-1b-it", ModelNames.Display("ggml-org/gemma-3-1b-it-GGUF:Q4_K_M"));
    }
}

public class RequestedModelsTests
{
    static RequestedModels? Parse(string json) => RequestedModels.From(JsonNode.Parse(json));

    [Fact]
    public void ABlockIsReadWithItsCountsNamesAndWords()
    {
        var spec = Parse("""
            {"count":{"min":2,"max":5},"distinct":"vendor","require":["Openai/gpt-5*",["a/b","c/d"]],
             "recommend":["x-ai/grok-4.1-fast","google/*"],"why":"Different AIs judge each side.","whyLong":"Longer.","names":true}
            """)!;
        Assert.Equal((2, 5), (spec.Min, spec.Max));
        Assert.True(spec.ByVendor);
        Assert.Equal(["openai/gpt-5*"], spec.Require[0].AnyOf);
        Assert.Equal(["a/b", "c/d"], spec.Require[1].AnyOf);
        Assert.Equal(["x-ai/grok-4.1-fast", "google/*"], spec.Recommend);
        Assert.Equal("Different AIs judge each side.", spec.Why);
        Assert.Equal("Longer.", spec.WhyLong);
        Assert.True(spec.Names);
    }

    [Fact]
    public void ACountMayBeOneNumberAndIsKeptInRange()
    {
        var one = Parse("""{"count":3}""")!;
        Assert.Equal((3, 3), (one.Min, one.Max));
        var wild = Parse("""{"count":{"min":0,"max":1000}}""")!;
        Assert.Equal((1, RequestedModels.MaxModels), (wild.Min, wild.Max));
        var backwards = Parse("""{"count":{"min":4,"max":2}}""")!;
        Assert.Equal((4, 4), (backwards.Min, backwards.Max));
    }

    [Fact]
    public void WhatIsNotUsableIsDroppedNotTrusted()
    {
        // A block that says nothing about the count still asks (one to five), so its needs, kind and names are not lost; a block
        // that is not an object asks for nothing the wallet could plan. The old rule ("no count" asked for nothing) is replaced by
        // PROPOSAL-v2 3.1.
        Assert.Equal((1, 5), (Parse("""{"why":"no count"}""")!.Min, Parse("""{"why":"no count"}""")!.Max));
        Assert.Null(Parse("""[1,2]"""));
        var spec = Parse("""{"count":2,"require":["has space","","ok/model",5],"recommend":["ok/model","ok/model","UPPER/Case"],"why":"  \u0007 hi  "}""")!;
        Assert.Single(spec.Require);
        Assert.Equal(["ok/model", "upper/case"], spec.Recommend);
        Assert.Equal("hi", spec.Why);
        Assert.False(spec.Names);
        Assert.Equal("model", spec.Distinct);
        var long_ = Parse("{\"count\":1,\"why\":\"" + new string('x', 500) + "\",\"whyLong\":\"" + new string('y', 5000) + "\"}")!;
        Assert.Equal(RequestedModels.MaxWhy, long_.Why!.Length);
        Assert.Equal(RequestedModels.MaxWhyLong, long_.WhyLong!.Length);
    }

    [Fact]
    public void ABlockIsReadWithItsNeedsKindMismatchRuleAndAbilities()
    {
        var spec = Parse("""
            {"count":1,"kind":"any","onMismatch":"show","needs":{"input":["text","image"],"output":["text"],"context":64,"runs":["api-key"]},
             "abilities":["Web.Search","image.read","x-thing"],"x-future":{"anything":1}}
            """)!;
        Assert.Equal(RequestedModels.AnyKind, spec.Kind);
        Assert.Equal("show", spec.OnMismatch);
        Assert.Equal(["text", "image"], spec.Needs!.Input);
        Assert.Equal(["text"], spec.Needs.Output);
        Assert.Equal(64, spec.Needs.ContextThousands);
        Assert.Equal(["api-key"], spec.Needs.Runs);
        Assert.Equal(["web.search", "image.read", "x-thing"], spec.Abilities);
        Assert.Contains("needs:input=text+image;output=text;context=64;runs=api-key;", spec.Ask);
        Assert.Contains("kind:any", spec.Ask);
        Assert.Contains("onMismatch:show", spec.Ask);
        Assert.Contains("abilities:web.search,image.read,x-thing", spec.Ask);

        // The defaults of a plain block, and the long forms of every condition.
        var plain = Parse("""{"count":1}""")!;
        Assert.Equal(RequestedModels.ModelKind, plain.Kind);
        Assert.Null(plain.OnMismatch);
        Assert.Empty(plain.Abilities);
        Assert.False(plain.Needs?.Any ?? false);
        var longForm = Parse("""{"count":1,"needs":{"input":{"includes":["TEXT"]},"context":{"min":32},"runs":{"oneOf":["cloud","local"]}}}""")!;
        Assert.Equal(["text"], longForm.Needs!.Input);
        Assert.Equal(32, longForm.Needs.ContextThousands);
        Assert.Null(longForm.Needs.Output);
        Assert.Equal(["local"], longForm.Needs.Runs);                    // the grant's words only; an unknown word is no condition

        // Words the wallet does not know are not conditions, and neither is a `needs` that is not an object.
        var unknown = Parse("""{"count":1,"needs":{"input":["vibes"],"output":"text","context":0,"runs":"local","level":5},"kind":"robot","onMismatch":"maybe"}""")!;
        Assert.False(unknown.Needs!.Any);
        Assert.Equal(RequestedModels.ModelKind, unknown.Kind);
        Assert.Null(unknown.OnMismatch);
        Assert.DoesNotContain("needs", unknown.Ask);
    }

    [Fact]
    public void AChangedAskIsAChangedLine()
    {
        string Ask(string needs) => Parse("""{"count":1,"needs":""" + needs + "}")!.Ask;
        var text = Ask("""{"input":["text"]}""");
        Assert.Equal(text, Ask("""{"input":{"includes":["text"]}}"""));        // the same condition in its long form
        Assert.NotEqual(text, Ask("""{"input":["text","image"]}"""));          // a new value asks again
        Assert.NotEqual(text, Ask("""{"input":["text"],"context":32}"""));     // so does a new key
        Assert.NotEqual(Ask("""{"context":64}"""), Ask("""{"context":65}""")); // and a raised minimum
    }

    [Fact]
    public void MoreRequiredModelsThanTheSiteWillUseRaiseTheMaximum()
    {
        var spec = Parse("""{"count":{"min":1,"max":1},"require":["a/b","c/d","e/f"]}""")!;
        Assert.Equal(3, spec.Max);
    }
}

public class ModelPlannerTests
{
    static ShelfModel Shelf(string id, string modelId, bool offer = true, string? notReady = null, bool paid = false) =>
        new(id, ModelNames.Display(modelId), modelId, paid ? "OpenRouter key" : "this PC", paid, paid ? "about $0.002 an answer" : "free", offer, notReady);

    static RequestedModels Spec(int min, int max, string distinct = "model", string[]? require = null, string[]? recommend = null) =>
        new(min, max, distinct, (require ?? []).Select(r => new ModelWish([r])).ToList(), recommend ?? [], null, null, false);

    static ModelPlanContext Context(IReadOnlyList<ShelfModel> shelf, IReadOnlyList<string>? previous = null, Func<string, ModelRoad?>? road = null) =>
        new(shelf, previous ?? [], road ?? (_ => null), _ => "about $0.01 an answer");

    static string[] Ticked(ModelSection section) => section.Choices.Where(c => c.Checked).Select(c => c.Key).ToArray();

    [Fact]
    public void TheSitesRecommendationsThatTheVisitorHasAreTickedInTheSitesOrder()
    {
        var shelf = new[] { Shelf("a", "google/gemma-3-1b-it"), Shelf("b", "qwen/qwen3-4b"), Shelf("c", "x-ai/grok-4-1-fast", paid: true) };
        var section = ModelPlanner.Plan("text.generate", Spec(1, 2, recommend: ["x-ai/grok-4.1-fast", "qwen/*"]), Context(shelf));
        Assert.Equal(["c", "b"], Ticked(section));
        Assert.Equal(ModelRole.Recommended, section.Choices.First(c => c.Key == "c").Role);
        Assert.True(section.CanServe);
    }

    [Fact]
    public void TheVisitorsDefaultSetFillsWhatTheSiteDidNotName()
    {
        var shelf = new[] { Shelf("a", "google/gemma-3-1b-it"), Shelf("b", "qwen/qwen3-4b", offer: false), Shelf("c", "mistralai/ministral-8b") };
        var section = ModelPlanner.Plan("text.generate", Spec(2, 5, recommend: ["nobody/has-this"]), Context(shelf));
        Assert.Equal(["a", "c"], Ticked(section));            // b is not in his default set: shown, not ticked
        Assert.Contains(section.Choices, c => c.Key == "b" && !c.Checked);
        var minimal = ModelPlanner.Plan("text.generate", Spec(1, 5), Context(shelf));
        Assert.Equal(["a"], Ticked(minimal));                 // the default set goes only as far as the site's minimum
    }

    [Fact]
    public void ARequiredModelIsTickedFirstAndAMissingOneStopsTheSite()
    {
        var shelf = new[] { Shelf("a", "google/gemma-3-1b-it"), Shelf("b", "openai/gpt-5-mini", paid: true) };
        var ok = ModelPlanner.Plan("text.generate", Spec(1, 1, require: ["openai/gpt-5*"]), Context(shelf));
        Assert.Equal(["b"], Ticked(ok));
        Assert.Equal(ModelRole.Required, ok.Choices[0].Role);

        var missing = ModelPlanner.Plan("text.generate", Spec(1, 3, require: ["anthropic/claude-opus*"]), Context(shelf));
        Assert.False(missing.CanServe);
        Assert.Equal(["anthropic/claude-opus*"], missing.MissingRequired);
    }

    [Fact]
    public void ARequiredModelBehindAKeyTheVisitorHasIsTickedAndMarkedNew()
    {
        var shelf = new[] { Shelf("a", "google/gemma-3-1b-it") };
        var section = ModelPlanner.Plan("text.generate", Spec(1, 2, require: ["anthropic/claude-opus-4.6"]),
            Context(shelf, road: name => new ModelRoad(SourceKinds.OpenRouter, name)));
        var opus = section.Choices.Single(c => c.IsNew);
        Assert.True(opus.Checked);
        Assert.Equal("new:openrouter:anthropic/claude-opus-4.6", opus.Key);
        Assert.Equal(ModelRole.Required, opus.Role);
        Assert.True(section.CanServe);
    }

    [Fact]
    public void ModelsBehindAKeyComeInOnlyWhenTooFewRemain()
    {
        var shelf = new[] { Shelf("a", "google/gemma-3-1b-it") };
        Func<string, ModelRoad?> road = name => new ModelRoad(SourceKinds.OpenRouter, name);
        var enough = ModelPlanner.Plan("text.generate", Spec(1, 3, recommend: ["x-ai/grok-4.1-fast", "openai/gpt-5-mini"]), Context(shelf, road: road));
        Assert.Equal(["a"], Ticked(enough));                                         // one is enough: the paid ones are only offered
        Assert.Equal(2, enough.Choices.Count(c => c.IsNew && !c.Checked));

        var short_ = ModelPlanner.Plan("text.generate", Spec(3, 3, recommend: ["x-ai/grok-4.1-fast", "openai/gpt-5-mini"]), Context(shelf, road: road));
        Assert.Equal(3, Ticked(short_).Length);                                      // too few: the recommendations are ticked, in the site's order
        Assert.Equal(["a", "new:openrouter:x-ai/grok-4.1-fast", "new:openrouter:openai/gpt-5-mini"], Ticked(short_));

        var none = ModelPlanner.Plan("text.generate", Spec(3, 3, recommend: ["x-ai/grok-4.1-fast"]), Context(shelf));
        Assert.False(none.CanServe);
        Assert.Contains("at least 3", none.Shortfall);
    }

    [Fact]
    public void AMaskIsNeverAddedBehindAKey()
    {
        var section = ModelPlanner.Plan("text.generate", Spec(1, 3, recommend: ["openai/gpt-5*"]), Context([], road: name => new ModelRoad(SourceKinds.OpenRouter, name)));
        Assert.Empty(section.Choices);
        Assert.False(section.CanServe);
    }

    [Fact]
    public void DifferentCompaniesAreChosenFirstWhenTheSiteAsksForThem()
    {
        var shelf = new[] { Shelf("g1", "google/gemma-3-1b-it"), Shelf("g2", "google/gemma-3-12b-it"), Shelf("o", "openai/gpt-5-mini", paid: true) };
        var byVendor = ModelPlanner.Plan("text.generate", Spec(2, 2, distinct: "vendor"), Context(shelf));
        Assert.Equal(["g1", "o"], Ticked(byVendor));
        var plain = ModelPlanner.Plan("text.generate", Spec(2, 2), Context(shelf));
        Assert.Equal(["g1", "g2"], Ticked(plain));
        var forced = ModelPlanner.Plan("text.generate", Spec(3, 3, distinct: "vendor"), Context(shelf));
        Assert.Equal(3, Ticked(forced).Length);                                       // the minimum wins over the wish for variety
    }

    [Fact]
    public void WhatTheVisitorGaveTheSiteLastTimeIsKeptAndAModelThatCannotAnswerIsNotTicked()
    {
        var shelf = new[] { Shelf("a", "google/gemma-3-1b-it"), Shelf("b", "qwen/qwen3-4b"), Shelf("c", "mistralai/ministral-8b", notReady: "not running") };
        var section = ModelPlanner.Plan("text.generate", Spec(2, 2), Context(shelf, previous: ["b", "c"]));
        Assert.Equal(["b", "a"], Ticked(section));
        Assert.Equal(["b"], Ticked(ModelPlanner.Plan("text.generate", Spec(1, 2), Context(shelf, previous: ["b", "c"]))));
        var off = section.Choices.Single(c => c.Key == "c");
        Assert.False(off.Ready);
        Assert.Equal("not running", off.Note);
    }

    [Fact]
    public void AStoredChoiceCoversASiteOnlyWhileItGivesEnoughModelsAndTheRequiredOnes()
    {
        var spec = Spec(2, 3, require: ["openai/gpt-5*"]);
        Assert.True(ModelPlanner.Satisfies(spec, ["openai/gpt-5-mini", "qwen/qwen3-4b"]));
        Assert.False(ModelPlanner.Satisfies(spec, ["openai/gpt-5-mini"]));
        Assert.False(ModelPlanner.Satisfies(spec, ["qwen/qwen3-4b", "google/gemma-3-1b-it"]));
    }
}

/// <summary>The engine with a site that asks for models: the window's proposal, the grant, calls by reference, and the visitor's later edits.</summary>
public class ModelRequestTests
{
    const string Site = "https://debate.example.org";

    readonly ManualTime _time = new();
    readonly ScriptedUi _ui = new();
    readonly FakeTextSource _gemma = new("lm:gemma") { Answers = new Queue<string>(Enumerable.Repeat("gemma says 60", 50)) };
    readonly FakeTextSource _qwen = new("lm:qwen") { Answers = new Queue<string>(Enumerable.Repeat("qwen says 70", 50)) };
    readonly FakeTextSource _gpt = new("openrouter:openai/gpt-5-mini", Runs.ApiKey, costPerCall: 0.004m) { Answers = new Queue<string>(Enumerable.Repeat("gpt says 80", 50)) };
    readonly SettingsStore _store = new(null);
    readonly ActivityLog _log;
    readonly WalletEngine _engine;

    public ModelRequestTests()
    {
        _log = new ActivityLog(null, _time.Now);
        _engine = new WalletEngine(_store, [], _log, _ui, _time, [_gemma, _qwen, _gpt]);
        _store.Update(s =>
        {
            s.Sources.Add(new SourceSetting { Id = "lm:gemma", Kind = SourceKinds.LmStudio, Model = "google/gemma-3-1b-it", Skills = [Vocabulary.Generate] });
            s.Sources.Add(new SourceSetting { Id = "lm:qwen", Kind = SourceKinds.LmStudio, Model = "qwen/qwen3-4b", Skills = [Vocabulary.Generate] });
            s.Sources.Add(new SourceSetting { Id = "openrouter:openai/gpt-5-mini", Kind = SourceKinds.OpenRouter, Model = "openai/gpt-5-mini", Skills = [Vocabulary.Generate], Offer = false });
            Router.EnsureTextChain(s, [_gemma, _qwen, _gpt]);
        });
    }

    static string Manifest(string models, string limits = "{\"maxTextChars\":60000}") =>
        "{\"byoa\":\"0.1-draft\",\"purpose\":{\"summary\":\"Debate judge\",\"limits\":" + limits + "},"
        + "\"skills\":{\"text.generate\":{\"required\":true,\"models\":" + models + "}},\"privacy\":\"personal\",\"expectedCost\":{\"perSession\":\"0.05 USD\"}}";

    const string TwoOrThree = "{\"count\":{\"min\":2,\"max\":3},\"recommend\":[\"openai/gpt-5-mini\",\"qwen/*\"],\"why\":\"Different AIs judge each side.\",\"names\":true}";

    async Task<JsonObject> RequestAsync(string manifest) =>
        Assert.IsType<JsonObject>(await _engine.HandleAsync(Clips.Request(Site, click: true, manifest)));

    static WireMessage Call(string grant, string? model, string text = "Who is right?") =>
        new(2, Wire.Call, Site, false, new JsonObject
        {
            ["grantId"] = grant,
            ["skill"] = "text.generate",
            ["input"] = new JsonObject { ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = text }) },
            ["params"] = model is null ? new JsonObject() : new JsonObject { ["model"] = model },
        });

    static bool Ok(JsonNode? result) => result is JsonObject o && o["ok"]?.GetValue<bool>() == true;

    [Fact]
    public async Task TheWindowProposesTheModelsAndTheSiteIsGivenWhatTheVisitorAllows()
    {
        var granted = await RequestAsync(Manifest(TwoOrThree));
        var shown = Assert.Single(_ui.Consents);
        var models = Assert.IsType<ModelSection>(shown.Rows.Single().Models);
        Assert.True(models.CanServe);
        // The site recommended gpt-5-mini and qwen: the visitor has both (gpt-5-mini is a model of his, not in his default set), so both are ticked, in the site's order.
        Assert.Equal(["openrouter:openai/gpt-5-mini", "lm:qwen"], models.Choices.Where(c => c.Checked).Select(c => c.Key).ToArray());

        var list = granted["models"]!["text.generate"]!.AsArray();
        Assert.Equal(2, list.Count);
        Assert.Equal("m1", list[0]!["ref"]!.GetValue<string>());
        Assert.Equal("openai/gpt-5-mini", list[0]!["id"]!.GetValue<string>());
        Assert.Equal("api-key", list[0]!["runs"]!.GetValue<string>());
        Assert.Equal("local", list[1]!["runs"]!.GetValue<string>());
        Assert.Equal("model", list[0]!["kind"]!.GetValue<string>());
        Assert.Equal("model", list[1]!["kind"]!.GetValue<string>());
        Assert.Equal(60_000, granted["limits"]!["maxTextChars"]!.GetValue<int>());      // the site's own, lower than the visitor's 100 000
        Assert.Equal(20_000, granted["limits"]!["maxAnswerTokens"]!.GetValue<int>());
        Assert.Equal(8, granted["limits"]!["maxParallelCalls"]!.GetValue<int>());
        Assert.Equal(["openrouter:openai/gpt-5-mini", "lm:qwen"], _store.Read(s => s.Site(Site)!.ModelsFor("text.generate")!.SourceIds));
    }

    [Fact]
    public async Task ACallNamesTheModelAndOnlyThatModelAnswers()
    {
        var granted = await RequestAsync(Manifest(TwoOrThree));
        var grant = granted["grantId"]!.GetValue<string>();
        var second = Assert.IsType<JsonObject>(await _engine.HandleAsync(Call(grant, "m2")));
        Assert.True(Ok(second));
        Assert.Equal("qwen says 70", second["output"]!["text"]!.GetValue<string>());
        Assert.Equal("m2", second["receipt"]!["model"]!.GetValue<string>());
        Assert.Equal(1, _qwen.Calls);
        Assert.Equal(0, _gpt.Calls);
        Assert.Equal(0, _gemma.Calls);

        var first = Assert.IsType<JsonObject>(await _engine.HandleAsync(Call(grant, "m1")));
        Assert.Equal("gpt says 80", first["output"]!["text"]!.GetValue<string>());
        Assert.Equal("0.004 USD", first["receipt"]!["cost"]!.GetValue<string>());
    }

    [Fact]
    public async Task AModelTheSiteWasNotGivenCannotBeReachedByAnyName()
    {
        var granted = await RequestAsync(Manifest(TwoOrThree));
        var grant = granted["grantId"]!.GetValue<string>();
        Assert.False(Ok(await _engine.HandleAsync(Call(grant, "m3"))));
        Assert.False(Ok(await _engine.HandleAsync(Call(grant, "lm:gemma"))));
        Assert.False(Ok(await _engine.HandleAsync(Call(grant, "google/gemma-3-1b-it"))));
        Assert.Equal(0, _gemma.Calls);                                      // the visitor's third model never saw a thing
        Assert.Contains("no such model for this site", _log.Recent()[0].Note);
    }

    [Fact]
    public async Task ACallThatNamesNoModelUsesTheModelsTheSiteWasGivenInTheVisitorsOrderAndNoOthers()
    {
        var granted = await RequestAsync(Manifest(TwoOrThree));
        var grant = granted["grantId"]!.GetValue<string>();
        _gpt.Fail = true;                                                   // the first of its models cannot answer
        var result = Assert.IsType<JsonObject>(await _engine.HandleAsync(Call(grant, null)));
        Assert.True(Ok(result));
        Assert.Equal("qwen says 70", result["output"]!["text"]!.GetValue<string>());
        Assert.Equal(0, _gemma.Calls);
    }

    [Fact]
    public async Task TheNamesStayWithTheVisitorUnlessTheSiteAskedAndHeAllows()
    {
        var silent = await RequestAsync(Manifest("{\"count\":{\"min\":2,\"max\":2}}"));
        var anonymous = silent["models"]!["text.generate"]!.AsArray();
        Assert.Equal("Model A", anonymous[0]!["name"]!.GetValue<string>());
        Assert.Equal("Model B", anonymous[1]!["name"]!.GetValue<string>());
        Assert.Null(anonymous[0]!["id"]);

        // The visitor hides the names of a site that asked for them.
        _ui.Consent = new ConsentAnswer(true, 0.05m, false, [], null, ShowNames: false);
        var hidden = await RequestAsync(Manifest(TwoOrThree));
        Assert.Equal("Model A", hidden["models"]!["text.generate"]![0]!["name"]!.GetValue<string>());
        Assert.False(_store.Read(s => s.Site(Site)!.ShowModelNames));
    }

    [Fact]
    public async Task TheVisitorsTicksAreWhatTheSiteGetsAndTooFewLeavesTheSkillOff()
    {
        _ui.Consent = new ConsentAnswer(true, 0.05m, false, [], new Dictionary<string, IReadOnlyList<string>> { ["text.generate"] = ["lm:gemma", "lm:qwen"] });
        var granted = await RequestAsync(Manifest(TwoOrThree));
        Assert.Equal(["lm:gemma", "lm:qwen"], _store.Read(s => s.Site(Site)!.ModelsFor("text.generate")!.SourceIds));
        Assert.Equal(2, granted["models"]!["text.generate"]!.AsArray().Count);

        _store.Update(s => s.Sites.Clear());
        _ui.Consent = new ConsentAnswer(true, 0.05m, false, [], new Dictionary<string, IReadOnlyList<string>> { ["text.generate"] = ["lm:gemma"] });
        Assert.Null(await _engine.HandleAsync(Clips.Request(Site, click: true, Manifest(TwoOrThree))));       // one model is not two: nothing is granted
    }

    [Fact]
    public async Task ASiteThatCannotBeGivenWhatItNeedsIsNotOfferedAllow()
    {
        await _engine.HandleAsync(Clips.Request(Site, click: true, Manifest("{\"count\":2,\"require\":[\"anthropic/claude-opus*\"]}")));
        var shown = Assert.Single(_ui.Consents);
        Assert.True(shown.MissingRequired);
        Assert.Equal(["anthropic/claude-opus*"], shown.Rows.Single().Models!.MissingRequired);
        Assert.Contains("cannot work without", shown.Rows.Single().Why);
    }

    [Fact]
    public async Task AModelBehindTheVisitorsOpenRouterKeyIsAddedWhenHeTicksIt()
    {
        _engine.HasOpenRouterKey = () => true;
        _ui.Consent = new ConsentAnswer(true, 0.05m, false, [], new Dictionary<string, IReadOnlyList<string>>
        {
            ["text.generate"] = ["lm:gemma", "new:openrouter:x-ai/grok-4.1-fast"],
        });
        await _engine.HandleAsync(Clips.Request(Site, click: true, Manifest("{\"count\":{\"min\":2,\"max\":3},\"recommend\":[\"x-ai/grok-4.1-fast\"]}")));
        var added = _store.Read(s => s.Sources.Single(x => x.Id == "openrouter:x-ai/grok-4.1-fast"));
        Assert.Equal(SourceKinds.OpenRouter, added.Kind);
        Assert.False(added.Offer);                                          // a model a site asked for is not one he wants everywhere
        Assert.Contains(_store.Read(s => s.Chains.Single(c => c.Skill == Vocabulary.Generate).Stops), st => st.SourceId == added.Id);
        Assert.Equal(["lm:gemma", "openrouter:x-ai/grok-4.1-fast"], _store.Read(s => s.Site(Site)!.ModelsFor("text.generate")!.SourceIds));
    }

    [Fact]
    public async Task AKeylessWalletNeverAddsAModelBehindAKey()
    {
        _ui.Consent = new ConsentAnswer(true, 0.05m, false, [], new Dictionary<string, IReadOnlyList<string>>
        {
            ["text.generate"] = ["lm:gemma", "new:openrouter:x-ai/grok-4.1-fast"],
        });
        await _engine.HandleAsync(Clips.Request(Site, click: true, Manifest("{\"count\":{\"min\":2,\"max\":3}}")));
        Assert.DoesNotContain(_store.Read(s => s.Sources), s => s.Id == "openrouter:x-ai/grok-4.1-fast");
    }

    [Fact]
    public async Task AReturningSiteIsNotAskedAgainWhileItsChoiceStillCoversWhatItAsksAndIsAskedWhenItDoesNot()
    {
        _store.Update(s => s.Sites.Clear());
        _ui.Consent = new ConsentAnswer(true, 0.05m, AutoDecide: true, []);
        await RequestAsync(Manifest(TwoOrThree));
        Assert.Single(_ui.Consents);

        // "decide on my own": the same request again is granted quietly, with the same models.
        var again = Assert.IsType<JsonObject>(await _engine.HandleAsync(Clips.Request(Site, click: false, Manifest(TwoOrThree))));
        Assert.Single(_ui.Consents);
        Assert.Equal(2, again["models"]!["text.generate"]!.AsArray().Count);

        // The site now wants three models, and the stored choice has two: the visitor is asked, and only about what changed.
        var three = "{\"count\":{\"min\":3,\"max\":3},\"why\":\"More judges.\"}";
        _ui.Consent = new ConsentAnswer(true, 0.05m, AutoDecide: true, []);
        await _engine.HandleAsync(Clips.Request(Site, click: true, Manifest(three)));
        Assert.Equal(2, _ui.Consents.Count);
        Assert.True(_ui.Consents[1].IsDifference);
        Assert.NotNull(_ui.Consents[1].Rows.Single().Models);
        Assert.Equal(3, _store.Read(s => s.Site(Site)!.ModelsFor("text.generate")!.SourceIds.Count));
    }

    [Fact]
    public async Task WhatTheVisitorChangesForASiteReachesItsLiveGrantAtOnce()
    {
        var granted = await RequestAsync(Manifest(TwoOrThree));
        var grant = granted["grantId"]!.GetValue<string>();
        var changed = 0;
        _engine.GrantChanged += origin => { Assert.Equal(Site, origin); changed++; };

        _engine.SetSiteModels(Site, "text.generate", ["lm:gemma", "lm:qwen", "openrouter:openai/gpt-5-mini"]);
        Assert.Equal(1, changed);
        var live = _engine.FindGrant(grant)!;
        Assert.Equal(3, live.ModelsJson()!["text.generate"]!.AsArray().Count);
        Assert.True(Ok(await _engine.HandleAsync(Call(grant, "m3"))));
        Assert.Equal("gpt says 80", (await _engine.HandleAsync(Call(grant, "m3")))!["output"]!["text"]!.GetValue<string>());

        _engine.SetSiteModels(Site, "text.generate", ["lm:qwen"]);
        Assert.False(Ok(await _engine.HandleAsync(Call(grant, "m2"))));       // the second model is gone from the site's list
        Assert.True(Ok(await _engine.HandleAsync(Call(grant, "m1"))));

        _engine.SetShowModelNames(Site, false);
        Assert.Equal("Model A", _engine.FindGrant(grant)!.ModelsJson()!["text.generate"]![0]!["name"]!.GetValue<string>());
        Assert.Equal(3, changed);
    }

    [Fact]
    public async Task ASiteWhoseModelsAreAllGoneIsNeverAnsweredByAnotherModel()
    {
        var granted = await RequestAsync(Manifest(TwoOrThree));
        var grant = granted["grantId"]!.GetValue<string>();
        _engine.SetSiteModels(Site, "text.generate", []);                      // the visitor took every model away from this site
        Assert.False(Ok(await _engine.HandleAsync(Call(grant, "m1"))));        // a named model it was once given
        Assert.False(Ok(await _engine.HandleAsync(Call(grant, null))));        // and a call that names none: the wallet's own route is not for a site that asked for models
        Assert.Equal(0, _gpt.Calls + _qwen.Calls + _gemma.Calls);
    }

    [Fact]
    public async Task ASiteThatAsksForNoModelsKeepsTheWalletsRouteAndSeesNoModelList()
    {
        var plain = "{\"byoa\":\"0.1-draft\",\"purpose\":{\"summary\":\"Quiz\",\"limits\":{\"maxTextChars\":8000}},\"skills\":{\"text.generate\":{\"required\":true}},\"privacy\":\"personal\"}";
        var granted = await RequestAsync(plain);
        Assert.Null(granted["models"]);
        Assert.NotNull(granted["limits"]);
        var shown = Assert.Single(_ui.Consents);
        Assert.Null(shown.Rows.Single().Models);
        Assert.NotEmpty(shown.Rows.Single().Routes);
        var result = Assert.IsType<JsonObject>(await _engine.HandleAsync(Call(granted["grantId"]!.GetValue<string>(), null)));
        Assert.Equal("gemma says 60", result["output"]!["text"]!.GetValue<string>());     // first in the route, as before
        Assert.Null(result["receipt"]!["model"]);
    }

    // ------------------------------------------------------------------ the limits a site asks for

    [Fact]
    public async Task ALimitAboveTheVisitorsIsPutToHimOnceAndTakenWhenHeAcceptsIt()
    {
        var asking = Manifest(TwoOrThree, "{\"maxTextChars\":150000,\"maxAnswerTokens\":30000,\"maxParallelCalls\":4}");
        var granted = await RequestAsync(asking);
        var ask = Assert.IsType<LimitsAsk>(_ui.Consents[0].Limits);
        Assert.Equal(150_000, ask.Wanted.InputChars);
        Assert.Equal(30_000, ask.Wanted.AnswerTokens);
        Assert.Null(ask.Wanted.ParallelCalls);                              // 4 is below his 8: nothing to accept
        Assert.Equal(100_000, ask.Current.InputChars);
        Assert.Equal(150_000, _store.Read(s => s.Site(Site)!.TextInputChars));
        Assert.Equal(30_000, _store.Read(s => s.Site(Site)!.TextAnswerTokens));
        Assert.Equal(150_000, TextLimits.ForSite(_store.Snapshot().Limits, _store.Snapshot().Site(Site)).InputChars);
        Assert.Equal(30_000, granted["limits"]!["maxAnswerTokens"]!.GetValue<int>());

        // The same ask again is not put to him again.
        _store.Update(s => s.Site(Site)!.AutoDecide = true);
        Assert.NotNull(await _engine.HandleAsync(Clips.Request(Site, click: false, asking)));
        Assert.Single(_ui.Consents);
    }

    [Fact]
    public async Task RefusingASitesLimitsKeepsTheVisitorsOwnAndTheAskIsRememberedAsSeen()
    {
        _ui.Consent = new ConsentAnswer(true, 0.05m, AutoDecide: true, [], null, UseSiteLimits: false);
        var asking = Manifest(TwoOrThree, "{\"maxTextChars\":150000}");
        await RequestAsync(asking);
        Assert.Null(_store.Read(s => s.Site(Site)!.TextInputChars));
        Assert.Equal(150_000, _store.Read(s => s.Site(Site)!.Asked!.InputChars));
        Assert.NotNull(await _engine.HandleAsync(Clips.Request(Site, click: false, asking)));
        Assert.Single(_ui.Consents);                                        // it asked for the same: the visitor said no once, and that stands

        var more = Manifest(TwoOrThree, "{\"maxTextChars\":300000}");
        _ui.Consent = new ConsentAnswer(true, 0.05m, AutoDecide: true, []);
        await _engine.HandleAsync(Clips.Request(Site, click: true, more));
        Assert.Equal(2, _ui.Consents.Count);                                // asking for more than it did is a new question
    }
}
