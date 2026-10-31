using System.Text.Json.Nodes;
using Byoa.Wallet.Engine;
using Byoa.Wallet.Settings;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Tests;

/// <summary>The one engine that decides whether a source suits what a site asks of a skill (PROPOSAL-v2 3.1 to 3.3).</summary>
public class ModelFitTests
{
    static readonly string[] Text = ["text"];
    static readonly string[] TextAndImage = ["text", "image"];

    static RequestedModels Spec(string models) => RequestedModels.From(JsonNode.Parse(models))!;

    static FitVerdict Evaluate(ModelFacts facts, string skill = Vocabulary.Generate, string? spec = null, string modelId = "some/model") =>
        ModelFit.Evaluate(facts, modelId, skill, spec is null ? null : Spec(spec));

    // ------------------------------------------------------------------ the skill's own properties

    [Fact]
    public void ATextSkillNeverOffersAModelThatDrawsPicturesUnlessTheVisitorSaidSo()
    {
        var draws = new ModelFacts(Output: TextAndImage);
        var verdict = Evaluate(draws);
        Assert.Equal(FitState.Excluded, verdict.State);
        Assert.Contains("draws pictures", Assert.Single(verdict.Reasons));
        Assert.False(verdict.Offered(dropOnMismatch: false));                  // not even where the site wants mismatches shown

        Assert.Equal(FitState.Fits, Evaluate(draws with { AlsoText = true }).State);
        Assert.Equal(FitState.Excluded, Evaluate(new ModelFacts(Output: ["image"]) { AlsoText = true }).State);     // nothing to write text with
        Assert.Equal(FitState.Excluded, Evaluate(new ModelFacts(Output: ["embeddings"])).State);
    }

    [Fact]
    public void ASitesNeedsCanOnlyNarrowTheSkillsPropertiesNeverWidenThem()
    {
        var draws = new ModelFacts(Output: TextAndImage);
        // The site asks for picture output on a text skill, in every form it could write it: the model stays out.
        Assert.Equal(FitState.Excluded, Evaluate(draws, spec: """{"count":1,"needs":{"output":["image"]}}""").State);
        Assert.Equal(FitState.Excluded, Evaluate(draws, spec: """{"count":1,"kind":"any","onMismatch":"show","needs":{"output":{"includes":["text","image"]}}}""").State);
        Assert.Equal(FitState.Excluded, Evaluate(draws, spec: """{"count":1,"require":["some/model"]}""").State);       // naming it does not help either
    }

    [Fact]
    public void ASourceWhoseOutputIsNotKnownIsOfferedForTextAsItAlwaysWas()
    {
        // A model the wallet knows nothing about was added by the visitor for text; it is not turned away for what nobody has said.
        var verdict = Evaluate(ModelFacts.Unknown);
        Assert.Equal(FitState.Fits, verdict.State);
        Assert.Empty(verdict.Can);
    }

    [Fact]
    public void APictureSkillIsOfferedOnlyASourceKnownToDrawPictures()
    {
        Assert.Equal(FitState.Fits, Evaluate(new ModelFacts(Output: TextAndImage), Vocabulary.ImageGenerate).State);
        Assert.Equal(FitState.Excluded, Evaluate(ModelFacts.Unknown, Vocabulary.ImageGenerate).State);                  // unknown is not known to draw
        Assert.Equal(FitState.Excluded, Evaluate(new ModelFacts(Output: Text), Vocabulary.ImageGenerate).State);
        var agent = ModelFacts.Of(new SourceSetting { Kind = SourceKinds.Agent }, null, "api-key");
        Assert.Equal(FitState.Excluded, Evaluate(agent, Vocabulary.ImageGenerate, modelId: "agent/mine").State);       // an agent never serves it
    }

    // ------------------------------------------------------------------ the site's needs

    [Fact]
    public void AKnownPropertyThatIsWrongIsAMismatchWithAReasonInPlainWords()
    {
        var textOnly = new ModelFacts(Input: Text, Output: Text, ContextThousands: 32, Runs: "api-key");
        var verdict = Evaluate(textOnly, spec: """{"count":1,"needs":{"input":["text","image"],"output":["text"],"context":64,"runs":["local"]}}""");
        Assert.Equal(FitState.Mismatch, verdict.State);
        Assert.Equal(["cannot read pictures", "reads 32k at most; the site wants 64k or more", "runs on an API key; the site wants this PC"], verdict.Reasons);
        Assert.Equal(new Dictionary<string, bool> { ["input"] = false, ["output"] = true, ["context"] = false, ["runs"] = false }, verdict.Can);
        Assert.True(verdict.Offered(dropOnMismatch: false));                   // onMismatch "show"
        Assert.False(verdict.Offered(dropOnMismatch: true));                   // onMismatch "drop"
    }

    [Fact]
    public void APropertyThatIsNotKnownIsNeverAFailureAndIsNeverAVerdict()
    {
        var unknownInput = new ModelFacts(Output: Text, Runs: "local");
        var verdict = Evaluate(unknownInput, spec: """{"count":1,"needs":{"input":["text","image"],"output":["text"],"context":64,"runs":["local"]}}""");
        Assert.Equal(FitState.Unknown, verdict.State);
        Assert.Equal(["input", "context"], verdict.UnknownProperties);
        Assert.Empty(verdict.Reasons);
        Assert.Equal(new Dictionary<string, bool> { ["output"] = true, ["runs"] = true }, verdict.Can);     // a key is absent when the property is unknown
        Assert.True(verdict.Offered(dropOnMismatch: true));                    // an unknown never drops anything

        // A known failure wins over an unknown property.
        var both = Evaluate(new ModelFacts(Input: Text), spec: """{"count":1,"needs":{"input":["image"],"output":["text"]}}""");
        Assert.Equal(FitState.Mismatch, both.State);
        Assert.Equal(["output"], both.UnknownProperties);
    }

    [Fact]
    public void AModelThatMeetsEveryConditionFitsAndEverythingTheSiteDidNotAskAboutIsLeftOut()
    {
        var facts = new ModelFacts(Input: TextAndImage, Output: Text, ContextThousands: 131, Runs: "api-key");
        var verdict = Evaluate(facts, spec: """{"count":1,"needs":{"input":["text","image"],"context":64}}""");
        Assert.Equal(FitState.Fits, verdict.State);
        Assert.Equal(new Dictionary<string, bool> { ["input"] = true, ["context"] = true }, verdict.Can);        // not output, not runs: nobody asked
        Assert.Empty(Evaluate(facts, spec: """{"count":1}""").Can);
    }

    [Fact]
    public void WordsAndShapesTheWalletDoesNotKnowAreNotConditions()
    {
        // (the reader drops them; the engine has nothing to check)
        var verdict = Evaluate(new ModelFacts(Input: Text), spec: """{"count":1,"needs":{"input":["smell"],"level":5,"x-thing":1}}""");
        Assert.Equal(FitState.Fits, verdict.State);
        Assert.Empty(verdict.Can);
    }

    // ------------------------------------------------------------------ kind and the agent

    [Fact]
    public void TheKindOfSourceFiltersAndAnAgentTheSiteNamesIsAskedFor()
    {
        var agent = ModelFacts.Of(new SourceSetting { Kind = SourceKinds.Agent }, null, "api-key");
        var model = new ModelFacts(Output: Text);

        Assert.Equal(FitState.Mismatch, Evaluate(agent, spec: """{"count":1}""", modelId: "agent/mine").State);                       // the default kind is "model"
        Assert.Equal(FitState.Fits, Evaluate(agent, spec: """{"count":1,"kind":"agent"}""", modelId: "agent/mine").State);
        Assert.Equal(FitState.Fits, Evaluate(agent, spec: """{"count":1,"kind":"any"}""", modelId: "agent/mine").State);
        Assert.Equal(FitState.Fits, Evaluate(agent, spec: """{"count":1,"require":["agent/*"]}""", modelId: "agent/mine").State);       // "agent/*" keeps working
        Assert.Equal(FitState.Fits, Evaluate(agent, spec: """{"count":1,"recommend":["agent/*"]}""", modelId: "agent/mine").State);

        Assert.Equal(FitState.Fits, Evaluate(model, spec: """{"count":1}""").State);
        Assert.Equal(FitState.Mismatch, Evaluate(model, spec: """{"count":1,"kind":"agent"}""").State);
        Assert.Equal(FitState.Fits, Evaluate(model, spec: """{"count":1,"kind":"any"}""").State);
    }

    [Fact]
    public void AMismatchIsDroppedForAModelAndShownForAnAgentUnlessTheSiteSaysOtherwise()
    {
        var agent = ModelFacts.Of(new SourceSetting { Kind = SourceKinds.Agent }, null, "api-key");
        Assert.True(ModelFit.DropsOnMismatch(Spec("""{"count":1}"""), new ModelFacts()));
        Assert.False(ModelFit.DropsOnMismatch(Spec("""{"count":1}"""), agent));
        Assert.False(ModelFit.DropsOnMismatch(Spec("""{"count":1,"onMismatch":"show"}"""), new ModelFacts()));
        Assert.True(ModelFit.DropsOnMismatch(Spec("""{"count":1,"onMismatch":"drop"}"""), agent));
        Assert.True(ModelFit.DropsOnMismatch(null, new ModelFacts()));
    }

    [Fact]
    public void AnAgentCannotReadPicturesButItsTextIsKnown()
    {
        var agent = ModelFacts.Of(new SourceSetting { Kind = SourceKinds.Agent }, null, "api-key");
        var verdict = Evaluate(agent, spec: """{"count":1,"kind":"agent","needs":{"input":["text","image"],"output":["text"]}}""", modelId: "agent/mine");
        Assert.Equal(FitState.Mismatch, verdict.State);
        Assert.Equal(["cannot read pictures"], verdict.Reasons);
        Assert.False(ModelFit.DropsOnMismatch(Spec("""{"count":1,"kind":"agent"}"""), agent));       // soft by default: it stays tickable
    }

    [Fact]
    public void TheNeedsOfASiteAreSaidInShortPlainWordsAndTextIsNotRepeated()
    {
        Assert.Empty(ModelFit.NeedsWords(Spec("""{"count":1}""")));
        Assert.Equal(["reads pictures", "draws pictures", "context 64k+", "runs on this PC or an API key", "a model or your agent"],
            ModelFit.NeedsWords(Spec("""{"count":1,"kind":"any","needs":{"input":["text","image"],"output":["text","image"],"context":64,"runs":["local","api-key"]}}""")));
        Assert.Equal(["hears audio", "your own agent"], ModelFit.NeedsWords(Spec("""{"count":1,"kind":"agent","needs":{"input":["audio"],"output":["text"]}}""")));
    }

    [Fact]
    public void WhatIsSaidBesideAModelThatDoesNotSurelyFitIsItsMismatchesOrWhatIsNotKnown()
    {
        var spec = """{"count":1,"needs":{"input":["image"],"context":64}}""";
        Assert.Null(ModelFit.Marks(Evaluate(new ModelFacts(Input: TextAndImage, ContextThousands: 100), spec: spec)));
        Assert.Equal(["cannot read pictures"], ModelFit.Marks(Evaluate(new ModelFacts(Input: Text, ContextThousands: 100), spec: spec)));
        Assert.Equal(["not known: what it reads, how much it reads at once"], ModelFit.Marks(Evaluate(ModelFacts.Unknown, spec: spec)));
    }

    // ------------------------------------------------------------------ where the facts come from

    [Fact]
    public void TheVisitorsWordBeatsTheCatalogAndTheCatalogIsOnlyForTheCatalogsOwnModels()
    {
        var listed = new CatalogModel("google/gemma-3-12b-it", "Gemma", 131_072, null, null, 0, ["text", "image"], ["text"]);
        var behindKey = ModelFacts.Of(new SourceSetting { Kind = SourceKinds.OpenRouter }, listed, "api-key");
        Assert.Equal(["text", "image"], behindKey.Input);
        Assert.Equal(["text"], behindKey.Output);
        Assert.Equal(131, behindKey.ContextThousands);                          // thousands of tokens
        Assert.Equal("api-key", behindKey.Runs);

        var said = ModelFacts.Of(new SourceSetting { Kind = SourceKinds.OpenRouter, Inputs = ["text"], AlsoText = true }, listed, "api-key");
        Assert.Equal(["text"], said.Input);                                     // his word beats the list
        Assert.Equal(["text"], said.Output);                                    // what he did not say, the list says
        Assert.True(said.AlsoText);

        // A copy of the model that runs here may lack what the model has (the picture part): the catalog is not asked about it.
        var local = ModelFacts.Of(new SourceSetting { Kind = SourceKinds.LmStudio }, listed, "local");
        Assert.Null(local.Input);
        Assert.Null(local.Output);
        Assert.Null(local.ContextThousands);
        Assert.Equal("local", local.Runs);                                      // where it runs is never unknown
        Assert.Equal(["image"], ModelFacts.Of(new SourceSetting { Kind = SourceKinds.LmStudio, Inputs = ["image"] }, listed, "local").Input);

        var noList = new CatalogModel("x/y", "Y", 0, null, null, 0);
        Assert.Null(ModelFacts.Of(new SourceSetting { Kind = SourceKinds.OpenRouter }, noList, "api-key").ContextThousands);     // a list that gave no length says nothing
    }

    [Fact]
    public void AnAgentsFactsComeFromWhatTheWalletLaunches()
    {
        var agent = ModelFacts.Of(new SourceSetting { Kind = SourceKinds.Agent, Inputs = ["image"], AlsoText = true }, null, "api-key");
        Assert.True(agent.Agent);
        Assert.Equal(["text"], agent.Input);                                    // nothing the visitor writes makes the agent read pictures
        Assert.Equal(["text"], agent.Output);
        Assert.Equal(["web.search", "web.read"], AgentProfile.Abilities);       // derived from the tools of the command line
        var arguments = Byoa.Wallet.Providers.AgentCliSource.Arguments().ToList();
        Assert.Equal(string.Join(',', AgentProfile.Tools), arguments[arguments.IndexOf("--tools") + 1]);     // the command and the abilities have one source
        Assert.DoesNotContain("image.read", AgentProfile.Abilities);
    }

    [Fact]
    public void WhatTheSiteHopesForIsAnsweredFromTheProfileOnlyForWordsTheWalletKnows()
    {
        var asked = new[] { "web.search", "image.read", "x-thing", "web.read" };
        Assert.Equal(new Dictionary<string, bool> { ["web.search"] = true, ["image.read"] = false, ["web.read"] = true }, AgentProfile.Verdicts(asked));
        Assert.Equal(["image.read"], AgentProfile.Lacks(asked));                // x-thing is neither had nor lacked: the wallet cannot say
        Assert.Empty(AgentProfile.Lacks(["web.search"]));
    }
}
