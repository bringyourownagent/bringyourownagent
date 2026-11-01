using System.Text.Json.Nodes;
using Byoa.Wallet.Activity;
using Byoa.Wallet.Engine;
using Byoa.Wallet.Settings;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Tests;

/// <summary>The choice of models for a site that asks for pictures as well as text (PROPOSAL-v3 4.1 and 4.3): two sections, one offering rule, refs unique in a grant.</summary>
public class PictureModelsTests
{
    const string Site = "https://icons.example.org";

    readonly ManualTime _time = new();
    readonly ScriptedUi _ui = new();
    readonly FakeTextSource _gemma = new("lm:gemma");
    readonly FakeTextSource _gpt = new("openrouter:openai/gpt-5-mini", Runs.ApiKey, costPerCall: 0.004m);
    readonly FakeImageSource _nano = new("openrouter:google/nano");
    readonly FakeImageSource _imagine = new("openrouter:someone/imagine");
    readonly FakeTextSource _draws = new("lm:draws");
    readonly SettingsStore _store = new(null);
    readonly ActivityLog _log;
    readonly WalletEngine _engine;

    public PictureModelsTests()
    {
        _log = new ActivityLog(null, _time.Now);
        _engine = new WalletEngine(_store, [], _log, _ui, _time, [_gemma, _gpt, _nano, _imagine, _draws])
        {
            Catalog = new ListedCatalog(
                new CatalogModel("openai/gpt-5-mini", "OpenAI: GPT-5 Mini", 400_000, null, null, 0, ["text", "image"], ["text"]),
                new CatalogModel("google/nano", "Google: Nano", 32_768, null, null, 0, ["text", "image"], ["text", "image"]),
                new CatalogModel("someone/imagine", "Someone: Imagine", 32_768, null, null, 0, ["text"], ["image"]),
                new CatalogModel("black/forest", "Black: Forest", 32_768, null, null, 0, ["text", "image"], ["image"]),
                new CatalogModel("free/draw", "Free: Draw", 32_768, 0m, 0m, 0, ["text"], ["text", "image"])),
            HasOpenRouterKey = () => true,
        };
        _store.Update(s =>
        {
            s.Sources.Add(new SourceSetting { Id = "lm:gemma", Kind = SourceKinds.LmStudio, Model = "google/gemma-3-1b-it", Skills = [Vocabulary.Generate] });
            s.Sources.Add(new SourceSetting { Id = "openrouter:openai/gpt-5-mini", Kind = SourceKinds.OpenRouter, Model = "openai/gpt-5-mini", Skills = [Vocabulary.Generate], Offer = false });
            s.Sources.Add(new SourceSetting { Id = "openrouter:google/nano", Kind = SourceKinds.OpenRouter, Model = "google/nano", Skills = [Vocabulary.Generate], Offer = false });
            s.Sources.Add(new SourceSetting { Id = "openrouter:someone/imagine", Kind = SourceKinds.OpenRouter, Model = "someone/imagine", Skills = [Vocabulary.Generate], Offer = false });
            // a model on this PC that its owner says draws as well: it writes text, and cannot draw here (the only road that draws is the key)
            s.Sources.Add(new SourceSetting { Id = "lm:draws", Kind = SourceKinds.LmStudio, Model = "x/draws", Skills = [Vocabulary.Generate], Outputs = ["text", "image"], Offer = false });
            s.Sources.Add(new SourceSetting { Id = "agent:mine", Kind = SourceKinds.Agent, Model = "mine", Label = "Mine", Skills = [Vocabulary.Generate], AgentCommand = "mine.cmd", AgentFlavor = AgentFlavors.ClaudeCode });
            Router.EnsureTextChain(s, [_gemma, _gpt, _nano, _imagine, _draws]);
        });
    }

    const string TextBlock = "\"text.generate\":{\"required\":true,\"models\":{\"count\":{\"min\":1,\"max\":8},\"kind\":\"any\"}}";
    const string PictureBlock = "\"image.generate\":{\"required\":false,\"models\":{\"count\":{\"min\":1,\"max\":4}}}";

    static string Manifest(string skills, string summary = "Icon designer") =>
        "{\"byoa\":\"0.1-draft\",\"purpose\":{\"summary\":\"" + summary + "\"},\"skills\":{" + skills + "},\"privacy\":\"personal\",\"expectedCost\":{\"perSession\":\"0.05 USD\"}}";

    async Task<JsonObject?> RequestAsync(string manifest) =>
        await _engine.HandleAsync(Clips.Request(Site, click: true, manifest)) as JsonObject;

    ModelSection Section(string skill) => Assert.IsType<ModelSection>(_ui.Consents.Last().Rows.Single(r => r.Skill == skill).Models);

    static string[] Keys(IEnumerable<ModelChoice> choices) => choices.Select(c => c.Key).ToArray();

    static ConsentAnswer Ticks(string[] text, string[]? pictures, params string[] leftOff)
    {
        var models = new Dictionary<string, IReadOnlyList<string>> { ["text.generate"] = text };
        if (pictures is not null) models["image.generate"] = pictures;
        return new ConsentAnswer(true, 0.05m, false, leftOff, models);
    }

    // ------------------------------------------------------------------ two sections, one rule

    [Fact]
    public async Task ASiteThatAsksForBothSkillsGetsTwoSectionsAndEachModelIsInEverySectionItsOutputProduces()
    {
        await RequestAsync(Manifest(TextBlock + "," + PictureBlock));
        var row = _ui.Consents.Last().Rows;
        Assert.Equal(["text.generate", "image.generate"], row.Select(r => r.Skill));               // the text first, the pictures that are drawn for it after
        Assert.All(row, r => Assert.NotNull(r.Models));

        var text = Section("text.generate");
        var pictures = Section("image.generate");
        // the model that writes and draws (Nano) is in both; the one that only draws (Imagine) only in the pictures; text models only in the text
        Assert.Contains("openrouter:google/nano", Keys(text.Choices));
        Assert.Contains("openrouter:google/nano", Keys(pictures.Choices));
        Assert.DoesNotContain("openrouter:someone/imagine", Keys(text.Choices));
        Assert.Contains("openrouter:someone/imagine", Keys(pictures.Choices));
        Assert.Contains("lm:gemma", Keys(text.Choices));
        Assert.DoesNotContain("lm:gemma", Keys(pictures.Choices));
        Assert.Contains("openrouter:openai/gpt-5-mini", Keys(text.Choices));
        Assert.DoesNotContain("openrouter:openai/gpt-5-mini", Keys(pictures.Choices));
        // the agent writes text only
        Assert.Contains("agent:mine", Keys(text.Choices));
        Assert.DoesNotContain("agent:mine", Keys(pictures.Choices));
    }

    [Fact]
    public async Task TheWindowCountsWhatDoesNotFitInEachSectionWithItsReasonAndSaysNothingOfTextModelsInThePictureSection()
    {
        await RequestAsync(Manifest(TextBlock + "," + PictureBlock));
        var miss = Assert.Single(Section("text.generate").Misses);
        Assert.Equal(("Imagine", "does not write text"), (miss.Name, miss.Why));

        // in the picture section: the model on this PC that its owner says draws is counted (it cannot draw here); the plain text models are not
        var picture = Assert.Single(Section("image.generate").Misses);
        Assert.Equal("draws", picture.Name);
        Assert.Equal("can draw only behind an OpenRouter key", picture.Why);
    }

    [Fact]
    public async Task AModelThePublicListDoesNotKnowIsNotKnownToDrawAndIsOfferedForPicturesToNoOne()
    {
        // an OpenRouter model the list says nothing about: not known to draw, so counted as not fitting, never offered, never ticked
        _store.Update(s => s.Sources.Add(new SourceSetting { Id = "openrouter:odd/unlisted", Kind = SourceKinds.OpenRouter, Model = "odd/unlisted", Skills = [Vocabulary.Generate], Offer = false }));
        await RequestAsync(Manifest(TextBlock + "," + PictureBlock));
        var pictures = Section("image.generate");
        Assert.DoesNotContain("openrouter:odd/unlisted", Keys(pictures.Choices));
        Assert.Contains(pictures.Misses, m => m.Why == "is not known to draw pictures");
        Assert.Contains("openrouter:odd/unlisted", Keys(Section("text.generate").Choices));     // for text an unknown output is offered
    }

    [Fact]
    public async Task APictureSkillWithoutAModelsBlockStillGetsModelsToTickOneToFour()
    {
        await RequestAsync(Manifest(TextBlock + ",\"image.generate\":{\"required\":false}"));
        var spec = Section("image.generate").Spec;
        Assert.Equal((1, 4), (spec.Min, spec.Max));
        Assert.Contains("openrouter:someone/imagine", Keys(Section("image.generate").Choices));
    }

    [Fact]
    public async Task APictureCostIsFreeOnlyWhenThePublicListSaysSoAndUnknownOtherwise()
    {
        await RequestAsync(Manifest(TextBlock + "," + PictureBlock));
        var nano = Section("image.generate").Choices.Single(c => c.Key == "openrouter:google/nano");
        Assert.Equal("about $0.004 an answer", Section("text.generate").Choices.Single(c => c.Key == "openrouter:google/nano").Cost);
        Assert.Equal("price unknown", nano.Cost);                                    // a picture is never priced like an answer

        var found = _engine.SearchNewModels(null, null, skill: Vocabulary.ImageGenerate);
        Assert.Equal("free", found.Single(c => c.Key == "new:openrouter:free/draw").Cost);
        Assert.Equal("price unknown", found.Single(c => c.Key == "new:openrouter:black/forest").Cost);
    }

    // ------------------------------------------------------------------ what the visitor ticked

    [Fact]
    public async Task AnUntickedOptionalPictureSectionGivesAGrantWithoutImageGenerate()
    {
        _ui.Consent = Ticks(["lm:gemma"], ["openrouter:someone/imagine"], leftOff: "image.generate");
        var granted = await RequestAsync(Manifest(TextBlock + "," + PictureBlock));
        Assert.Equal(["text.generate"], granted!["skills"]!.AsArray().Select(s => s!.GetValue<string>()));
        var models = granted["models"]!.AsObject();
        Assert.True(models.ContainsKey("text.generate"));
        Assert.False(models.ContainsKey("image.generate"));
        Assert.Null(_store.Read(s => s.Site(Site)!.ModelsFor("image.generate")));          // nothing is stored for a section he left off
    }

    [Fact]
    public async Task ATickedPictureSectionGivesTheSkillAndItsModels()
    {
        _ui.Consent = Ticks(["lm:gemma"], ["openrouter:someone/imagine"]);
        var granted = await RequestAsync(Manifest(TextBlock + "," + PictureBlock));
        Assert.Equal(["image.generate", "text.generate"], granted!["skills"]!.AsArray().Select(s => s!.GetValue<string>()).Order());
        var list = granted["models"]!["image.generate"]!.AsArray();
        var only = Assert.Single(list)!;
        Assert.Equal("m2", only["ref"]!.GetValue<string>());
        Assert.Equal("api-key", only["runs"]!.GetValue<string>());
        Assert.Equal("model", only["kind"]!.GetValue<string>());
        Assert.Equal(["openrouter:someone/imagine"], _store.Read(s => s.Site(Site)!.ModelsFor("image.generate")!.SourceIds));
    }

    [Fact]
    public async Task RefsAreUniqueWithinAGrantTextFirstThenPicturesAndAModelInBothSectionsHasOneRefInEach()
    {
        _ui.Consent = Ticks(["lm:gemma", "openrouter:google/nano"], ["openrouter:google/nano", "openrouter:someone/imagine"]);
        var granted = await RequestAsync(Manifest(TextBlock + "," + PictureBlock));
        var text = granted!["models"]!["text.generate"]!.AsArray();
        var pictures = granted["models"]!["image.generate"]!.AsArray();
        Assert.Equal(["m1", "m2"], text.Select(m => m!["ref"]!.GetValue<string>()));
        Assert.Equal(["m3", "m4"], pictures.Select(m => m!["ref"]!.GetValue<string>()));
        // Nano is in both, under two refs
        var grant = _engine.FindGrant(granted["grantId"]!.GetValue<string>())!;
        Assert.Equal("m2", grant.Models["text.generate"].Single(m => m.SourceIds.Contains("openrouter:google/nano")).Ref);
        Assert.Equal("m3", grant.Models["image.generate"].Single(m => m.SourceIds.Contains("openrouter:google/nano")).Ref);
        Assert.All(pictures, m => Assert.Equal("model", m!["kind"]!.GetValue<string>()));
        var all = text.Concat(pictures).Select(m => m!["ref"]!.GetValue<string>()).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
    }

    [Fact]
    public async Task WithNoTextModelsInTheGrantThePicturesStartAtM1()
    {
        _ui.Consent = Ticks([], ["openrouter:someone/imagine"]);
        var granted = await RequestAsync(Manifest("\"text.generate\":{\"required\":false,\"models\":{\"count\":{\"min\":1,\"max\":2}}}," + PictureBlock));
        Assert.Equal("m1", granted!["models"]!["image.generate"]![0]!["ref"]!.GetValue<string>());
    }

    // ------------------------------------------------------------------ the stored choice, reuse, renewal

    [Fact]
    public async Task TheChoiceForEachSkillIsStoredAndASiteDecidedOnItsOwnIsGrantedAgainQuietlyWithBoth()
    {
        _ui.Consent = new ConsentAnswer(true, 0.05m, AutoDecide: true, [], new Dictionary<string, IReadOnlyList<string>>
        {
            ["text.generate"] = ["lm:gemma"],
            ["image.generate"] = ["openrouter:someone/imagine"],
        });
        var manifest = Manifest(TextBlock + "," + PictureBlock);
        await RequestAsync(manifest);
        Assert.Single(_ui.Consents);
        Assert.Equal(["lm:gemma"], _store.Read(s => s.Site(Site)!.ModelsFor("text.generate")!.SourceIds));
        Assert.Equal(["openrouter:someone/imagine"], _store.Read(s => s.Site(Site)!.ModelsFor("image.generate")!.SourceIds));

        var again = await RequestAsync(manifest);                                   // a page's request, after a click, decided on its own
        Assert.Single(_ui.Consents);
        Assert.Equal(["m2"], again!["models"]!["image.generate"]!.AsArray().Select(m => m!["ref"]!.GetValue<string>()));
        var renewed = await _engine.RenewViaLinkAsync(Site, (JsonObject)JsonNode.Parse(manifest)!) as JsonObject;     // and a reconnect with the pairing
        Assert.Single(_ui.Consents);
        Assert.Equal(["m2"], renewed!["models"]!["image.generate"]!.AsArray().Select(m => m!["ref"]!.GetValue<string>()));
    }

    [Fact]
    public async Task AStoredPictureChoiceThatNoLongerFitsIsAskedAboutAgainEvenForASiteDecidedOnItsOwn()
    {
        _ui.Consent = new ConsentAnswer(true, 0.05m, AutoDecide: true, [], new Dictionary<string, IReadOnlyList<string>>
        {
            ["text.generate"] = ["lm:gemma"],
            ["image.generate"] = ["openrouter:someone/imagine"],
        });
        var manifest = Manifest(TextBlock + "," + PictureBlock);
        await RequestAsync(manifest);
        Assert.Single(_ui.Consents);

        _store.Update(s => s.Source("openrouter:someone/imagine").Outputs = ["text"]);       // the visitor now says it does not draw
        _ui.Consent = null;
        await RequestAsync(manifest);
        Assert.Equal(2, _ui.Consents.Count);
        Assert.DoesNotContain("openrouter:someone/imagine", Keys(Section("image.generate").Choices));
        // the whole request comes back, as the first time: the text is already allowed, the pictures are asked about again
        Assert.Equal([("text.generate", true), ("image.generate", false)], _ui.Consents.Last().Rows.Select(r => (r.Skill, r.AlreadyAllowed)));
    }

    [Fact]
    public async Task AModelThatDrawsOnEveryTextRequestIsOfferedForPicturesAndNotForText()
    {
        _nano.RefuseText(TextRefusals.DrawsPictures);                      // learned on an earlier text call (DrawsOnTextTests)
        _store.Update(s => s.Source("openrouter:google/nano").DrawsOnText = true);
        await RequestAsync(Manifest(TextBlock + "," + PictureBlock));
        var forText = Section("text.generate").Choices.Single(c => c.Key == "openrouter:google/nano");
        Assert.False(forText.Ready);
        Assert.False(forText.Checked);
        Assert.Contains(TextRefusals.DrawsPictures, forText.Note);
        var forPictures = Section("image.generate").Choices.Single(c => c.Key == "openrouter:google/nano");
        Assert.True(forPictures.Ready);
    }

    [Fact]
    public async Task APictureSkillThatTheSiteAddsLaterIsAskedAboutWithTheTextMarkedAlreadyAllowed()
    {
        _ui.Consent = new ConsentAnswer(true, 0.05m, AutoDecide: true, [], new Dictionary<string, IReadOnlyList<string>> { ["text.generate"] = ["lm:gemma"] });
        await RequestAsync(Manifest(TextBlock));
        Assert.Single(_ui.Consents);
        await RequestAsync(Manifest(TextBlock + "," + PictureBlock));
        Assert.Equal(2, _ui.Consents.Count);
        Assert.True(_ui.Consents.Last().IsDifference);
        Assert.Equal([("text.generate", true), ("image.generate", false)], _ui.Consents.Last().Rows.Select(r => (r.Skill, r.AlreadyAllowed)));
    }

    [Fact]
    public async Task AnAllowedOptionalSkillTheVisitorUnticksWhenTheSiteAsksForMoreIsNoLongerAllowed()
    {
        var models = new Dictionary<string, IReadOnlyList<string>> { ["text.generate"] = ["lm:gemma"], ["image.generate"] = ["openrouter:someone/imagine"] };
        _ui.Consent = new ConsentAnswer(true, 0.05m, AutoDecide: true, [], models);
        await RequestAsync(Manifest(TextBlock + "," + PictureBlock));
        Assert.Equal(["image.generate", "text.generate"], _store.Read(s => s.Site(Site)!.Skills));

        // The site now also wants speech: the window shows all three, and the visitor leaves the pictures off.
        _ui.Consent = new ConsentAnswer(true, 0.05m, AutoDecide: true, ["image.generate"], models);
        await RequestAsync(Manifest(TextBlock + "," + PictureBlock + ",\"audio.transcribe\":{\"required\":false}"));
        Assert.True(_ui.Consents.Last().Rows.Single(r => r.Skill == "image.generate").AlreadyAllowed);
        Assert.DoesNotContain("image.generate", _store.Read(s => s.Site(Site)!.Skills));
        Assert.Contains("text.generate", _store.Read(s => s.Site(Site)!.Skills));
    }

    [Fact]
    public async Task AChangedAskOnThePictureModelsIsTheSiteAskingForMore()
    {
        _ui.Consent = new ConsentAnswer(true, 0.05m, AutoDecide: true, [], new Dictionary<string, IReadOnlyList<string>>
        {
            ["text.generate"] = ["lm:gemma"],
            ["image.generate"] = ["openrouter:google/nano"],
        });
        await RequestAsync(Manifest(TextBlock + "," + PictureBlock));
        await RequestAsync(Manifest(TextBlock + "," + PictureBlock));
        Assert.Single(_ui.Consents);
        await RequestAsync(Manifest(TextBlock + ",\"image.generate\":{\"required\":false,\"models\":{\"count\":{\"min\":1,\"max\":4},\"needs\":{\"input\":[\"text\",\"image\"]}}}"));
        Assert.Equal(2, _ui.Consents.Count);
    }

    // ------------------------------------------------------------------ the search behind the key, the Sites page

    [Fact]
    public void TheSearchBehindTheKeyForThePictureSectionOffersOnlyModelsKnownToDrawAndJudgesThemByWhatTheSiteAsked()
    {
        RequestedModels Ask(string models) => RequestedModels.From(JsonNode.Parse(models))!;
        var found = _engine.SearchNewModels(null, Ask("""{"count":1}"""), skill: Vocabulary.ImageGenerate);
        // (the ones the visitor already has are not "new")
        Assert.Equal(["new:openrouter:black/forest", "new:openrouter:free/draw"], Keys(found).Order());
        // the same list for text offers what writes text and was not added yet
        Assert.Equal(["new:openrouter:free/draw"], Keys(_engine.SearchNewModels(null, Ask("""{"count":1}"""))));

        // a picture site that wants a model that reads pictures: Forest reads them, Free: Draw reads text only
        var reads = _engine.SearchNewModels(null, Ask("""{"count":1,"needs":{"input":["image"]}}"""), skill: Vocabulary.ImageGenerate);
        Assert.Equal(["new:openrouter:black/forest"], Keys(reads));
    }

    [Fact]
    public async Task ThePicturesOfASiteAreItsOwnListOnTheSitesPageWithItsOwnNotes()
    {
        _ui.Consent = Ticks(["lm:gemma"], ["openrouter:google/nano", "openrouter:someone/imagine"]);
        var granted = await RequestAsync(Manifest(TextBlock + "," + PictureBlock));
        Assert.Empty(_engine.SiteModelNotes(Site, Vocabulary.ImageGenerate));

        _store.Update(s => s.Source("openrouter:someone/imagine").Outputs = ["text"]);
        Assert.StartsWith("not given to the site: does not draw pictures", _engine.SiteModelNotes(Site, Vocabulary.ImageGenerate)["openrouter:someone/imagine"]);
        Assert.Empty(_engine.SiteModelNotes(Site));                                       // the text list is judged by its own skill

        _engine.SetSiteModels(Site, Vocabulary.ImageGenerate, ["openrouter:google/nano"]);
        Assert.Equal(["openrouter:google/nano"], _store.Read(s => s.Site(Site)!.ModelsFor("image.generate")!.SourceIds));
        Assert.Equal(["lm:gemma"], _store.Read(s => s.Site(Site)!.ModelsFor("text.generate")!.SourceIds));     // and the text list is untouched
        Assert.Single(_engine.FindGrant(granted!["grantId"]!.GetValue<string>())!.Models["image.generate"]);        // the live grant follows at once

        Assert.True(_engine.ServesPictures("openrouter:google/nano"));
        Assert.False(_engine.ServesPictures("openrouter:someone/imagine"));               // the visitor says it writes text only
        Assert.False(_engine.ServesPictures("lm:gemma"));
        Assert.False(_engine.ServesPictures("lm:draws"));                                // draws, but not behind the key
        Assert.False(_engine.ServesPictures("agent:mine"));
        Assert.False(_engine.ServesPictures("no:such"));
    }

    [Fact]
    public void TheHelpPagesSayWhatTheWalletDoesAboutPicturesAndNotTheRuleThatWasLeftOut()
    {
        var sites = Byoa.Wallet.Help.HelpCatalog.Get("sites");
        Assert.Contains("you tick it in each one where you want it", sites);              // a model that writes text and draws is in both sections
        Assert.Contains("the wallet asks before the first one", sites);                   // the price of a picture is not known
        Assert.Contains("made smaller before it is sent", sites);
        Assert.Contains("one that only draws is never offered for text", sites);
        Assert.Contains("only draws", Byoa.Wallet.Help.HelpCatalog.Get("add-model"));
        Assert.Contains("Image generation", Byoa.Wallet.Help.HelpCatalog.Get("skills"));
        foreach (var topic in Byoa.Wallet.Help.HelpCatalog.Topics)
        {
            var article = Byoa.Wallet.Help.HelpCatalog.Get(topic);
            Assert.DoesNotContain("Also use as a text model", article);                  // the tick that was taken out again
            Assert.DoesNotContain("primary", article, StringComparison.OrdinalIgnoreCase);
        }
    }
}
