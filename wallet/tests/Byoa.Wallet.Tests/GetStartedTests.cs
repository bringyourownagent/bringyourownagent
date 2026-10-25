using Byoa.Wallet.Providers;
using Byoa.Wallet.Settings;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Tests;

/// <summary>The Get started card's decisions, with a fake scan result and a fake catalog: what is offered, what is left out.</summary>
public class GetStartedTests
{
    static RuntimeInfo Studio(bool running = true) => new(SourceKinds.LmStudio, "http://127.0.0.1:1234/v1", running,
    [
        new RuntimeModel("qwen/qwen3.5-4b", "Qwen3.5 4B", 2_300_000_000, false, true),
        new RuntimeModel("llama3.2:3b", null, null, true, true),
    ]);

    static RuntimeInfo Ollama(bool running = true) => new(SourceKinds.Ollama, "http://127.0.0.1:11434", running,
        [new RuntimeModel("gemma3:4b", null, null, false, true)]);

    static SourceSetting Added(string id, string kind = SourceKinds.LmStudio, string model = "m", string skill = Vocabulary.Generate) => new()
    {
        Id = id,
        Kind = kind,
        Model = model,
        Skills = [skill],
    };

    [Fact]
    public void OnlyRunningRuntimesAreOfferedAndTheLoadedModelComesFirst()
    {
        var offers = GetStarted.Local([Studio(), Ollama(running: false)], []);

        Assert.Equal(["llama3.2:3b", "qwen/qwen3.5-4b"], offers.Select(o => o.Model).ToArray());   // in memory first
        var loaded = offers[0];
        Assert.Equal("LM Studio", offers[0].RuntimeName);
        Assert.True(loaded.Loaded);
        Assert.False(offers[1].Loaded);
        Assert.Equal(2_300_000_000L, offers[1].SizeBytes);
    }

    [Fact]
    public void AnAlreadyAddedModelIsLeftOut()
    {
        var offered = SourceIds.OpenAiCompatible(SourceKinds.LmStudio, "llama3.2:3b", "http://127.0.0.1:1234/v1");
        var offers = GetStarted.Local([Studio()], [Added(offered, model: "llama3.2:3b")]);

        Assert.Equal(["qwen/qwen3.5-4b"], offers.Select(o => o.Model).ToArray());
        Assert.All(offers, o => Assert.NotEqual(offered, o.SourceId));
    }

    [Fact]
    public void TheOfferCarriesTheDraftTheAddModelWindowWouldWrite()
    {
        var offers = GetStarted.Local([Ollama()], []);

        var offer = Assert.Single(offers);
        Assert.Equal(SourceKinds.Ollama, offer.Kind);
        Assert.Equal("http://127.0.0.1:11434/v1", offer.Endpoint);           // Ollama's OpenAI-compatible address
        Assert.Equal("Ollama", offer.RuntimeName);
        Assert.Equal(SourceIds.OpenAiCompatible(SourceKinds.Ollama, "gemma3:4b", "http://127.0.0.1:11434/v1"), offer.SourceId);
    }

    [Theory]
    [InlineData("Renesas/Qwen3-ASR-1.7B-GGUF:FP16", false)]
    [InlineData("handy-computer/nemotron-3.5-asr-streaming-0.6b-gguf:Q8_0", false)]
    [InlineData("handy-computer/parakeet-tdt-0.6b-v3-gguf:Q8_0", false)]
    [InlineData("openai/whisper-large-v3", false)]
    [InlineData("nomic-embed-text", false)]
    [InlineData("unsloth/Qwen3.5-4B-GGUF:Q4_K_M", true)]
    [InlineData("ggml-org/gemma-3-1b-it-GGUF:Q4_K_M", true)]
    [InlineData("llama3.2:3b", true)]
    public void ASpeechOrEmbeddingModelIsNotOfferedAsAFirstTextModel(string id, bool offered)
    {
        Assert.Equal(offered, GetStarted.LooksLikeText(id));
        var llama = new RuntimeInfo(SourceKinds.OpenAiCompatible, LocalRuntimes.LlamaEndpoint, true, [new RuntimeModel(id, null, null, false, true)]);
        Assert.Equal(offered ? 1 : 0, GetStarted.Local([llama], []).Count);
    }

    [Fact]
    public void TheLlamaAppIsNamedAndAddedLikeAnyOpenAiCompatibleServer()
    {
        var llama = new RuntimeInfo(SourceKinds.OpenAiCompatible, LocalRuntimes.LlamaEndpoint, true,
            [new RuntimeModel("qwen3.5-1.7b", null, null, true, true)]);

        var offer = Assert.Single(GetStarted.Local([llama], []));

        Assert.Equal("Llama", offer.RuntimeName);
        Assert.Equal(SourceKinds.OpenAiCompatible, offer.Kind);
    }

    [Fact]
    public void TheCardShowsWhileTheWalletHasNoTextModelAtAll()
    {
        Assert.False(GetStarted.HasTextModel(new WalletSettings()));
        Assert.False(GetStarted.HasTextModel(new WalletSettings
        {
            Sources = [Added("lmstudio:m@http://127.0.0.1:1234/v1", skill: Vocabulary.Transcribe)],
        }));

        Assert.True(GetStarted.HasTextModel(new WalletSettings
        {
            Sources = [Added("lmstudio:m@http://127.0.0.1:1234/v1")],
        }));
        Assert.True(GetStarted.HasTextModel(new WalletSettings
        {
            Sources = [Added("lmstudio:m@http://127.0.0.1:1234/v1", skill: Vocabulary.Transcribe),
                       Added("openrouter:google/gemma-3-12b-it", kind: SourceKinds.OpenRouter, model: "google/gemma-3-12b-it")],
        }));
    }

    [Fact]
    public void TheDemoPresetsAnswerWithNoKeyAndSaySo()
    {
        Assert.Equal(["llm7", "ovh"], GetStarted.DemoPresets.Select(p => p.Id).ToArray());
        Assert.All(GetStarted.DemoPresets, p => Assert.True(p.Demo));
        Assert.Equal("default", GetStarted.DemoModel("llm7"));
        Assert.Equal("Mistral-Nemo-Instruct-2407", GetStarted.DemoModel("ovh"));
        Assert.Equal("default", GetStarted.DemoModel("anything-else"));
        Assert.Contains("may keep what you send", GetStarted.DemoWarning, StringComparison.Ordinal);
    }

    sealed class FixedCatalog(params CatalogModel[] models) : IModelCatalog
    {
        public bool Loaded => true;
        public CatalogModel? Find(string? name) => null;
        public IReadOnlyList<CatalogModel> Search(string? query, CatalogFilter filter = CatalogFilter.All, int limit = 40) => models.Take(limit).ToList();
    }

    static CatalogModel Free(string id, int context, long created = 5) => new(id, id, context, 0m, 0m, created);

    [Fact]
    public void TheFreePicksAreFreeVariantsTheBiggestFirstAndNothingElse()
    {
        var catalog = new FixedCatalog(
            Free("big/paid-model", 2_000_000),                        // not a :free variant: out
            Free("deepseek/deepseek-v5:free", 128_000, created: 9),
            Free("google/embed-gemma:free", 2_048),                   // an embedding: out even when free
            Free("tiny/tiny-base:free", 4_096),                       // a base model: out
            Free("qwen/qwen3.5-4b:free", 262_144, created: 2),
            Free("meta/llama-4-maverick:free", 1_048_576, created: 1),
            Free("microsoft/phi-5-mini:free", 131_072),
            Free("small/small:free", 8_192, created: 99));

        var picks = OpenRouterFreeModels.Pick(catalog, ["meta/llama-4-maverick:free"], 3);

        Assert.Equal(["qwen/qwen3.5-4b:free", "microsoft/phi-5-mini:free", "deepseek/deepseek-v5:free"],
            picks.Select(p => p.Id).ToArray());                       // biggest context first, then the newer listing
        Assert.All(picks, p => Assert.Equal(0m, p.PromptPerToken));
    }

    [Fact]
    public void TheFreePicksLeaveOutWhatTheVisitorHasUnderEitherSpelling()
    {
        var catalog = new FixedCatalog(
            Free("Google/gemma-3-12b-it:free", 131_072),
            Free("other/other:free", 4_096));

        var picks = OpenRouterFreeModels.Pick(catalog, ["google/gemma-3-12b-it"]);

        Assert.Equal(["other/other:free"], picks.Select(p => p.Id).ToArray());
    }
}
