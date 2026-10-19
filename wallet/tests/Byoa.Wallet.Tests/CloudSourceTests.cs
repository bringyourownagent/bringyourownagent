using System.Text.Json.Nodes;
using Byoa.Wallet.Activity;
using Byoa.Wallet.Protocol;
using Byoa.Wallet.Providers;
using Byoa.Wallet.Registry;
using Byoa.Wallet.Secrets;
using Byoa.Wallet.Settings;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Tests;

/// <summary>The public model list: reading it, finding a model by any spelling of its name, searching it, keeping a copy on disk.</summary>
public class OpenRouterCatalogTests
{
    const int Port = 5361;

    const string List = """
        {"data":[
         {"id":"google/gemma-3-12b-it","name":"Google: Gemma 3 12B","context_length":131072,"created":1741000000,
          "pricing":{"prompt":"0.00000003","completion":"0.0000001"},"architecture":{"input_modalities":["text","image"],"output_modalities":["text"]}},
         {"id":"google/gemma-3-12b-it:free","name":"Google: Gemma 3 12B (free)","context_length":32768,"created":1741000001,
          "pricing":{"prompt":"0","completion":"0"},"architecture":{"output_modalities":["text"]}},
         {"id":"openai/gpt-5-mini","name":"OpenAI: GPT-5 Mini","context_length":400000,"created":1754000000,
          "pricing":{"prompt":"0.00000025","completion":"0.000002"},"architecture":{"output_modalities":["text"]}},
         {"id":"someone/imagine","name":"Imagine","architecture":{"output_modalities":["image"]},"pricing":{"prompt":"0","completion":"0"}},
         {"id":"bad id with spaces","name":"x","architecture":{"output_modalities":["text"]}},
         {"id":"odd/price","name":"Odd","pricing":{"prompt":"99","completion":"nonsense"}}
        ]}
        """;

    static HttpClient Plain() => new(new SocketsHttpHandler { AllowAutoRedirect = false });

    [Fact]
    public void OnlyModelsThatWriteTextAndHaveARealIdAreKeptAndPricesAreDollarsPerToken()
    {
        var models = OpenRouterCatalog.Parse(List);
        Assert.Equal(["google/gemma-3-12b-it", "google/gemma-3-12b-it:free", "openai/gpt-5-mini", "odd/price"], models.Select(m => m.Id));
        var gemma = models[0];
        Assert.Equal(0.00000003m, gemma.PromptPerToken);
        Assert.Equal(131_072, gemma.ContextLength);
        Assert.False(gemma.Free);
        Assert.True(models[1].Free);
        Assert.Null(models[3].PromptPerToken);                   // a price above a dollar a token is not a price
        Assert.Null(models[3].CompletionPerToken);
        Assert.Empty(OpenRouterCatalog.Parse("not json"));
        Assert.Empty(OpenRouterCatalog.Parse("{\"data\":5}"));
    }

    [Fact]
    public void AModelIsFoundByAnySpellingAndThePlainIdBeatsItsFreeVariant()
    {
        var dir = TestFolder.New();
        var path = Path.Combine(dir, "models.json");
        File.WriteAllText(path, List);
        var catalog = new OpenRouterCatalog(Plain(), path);
        Assert.True(catalog.Loaded);
        Assert.Equal("google/gemma-3-12b-it", catalog.Find("Google/Gemma-3.12B-IT")!.Id);
        Assert.Equal("openai/gpt-5-mini", catalog.Find("gpt-5-mini") is { } byTail ? byTail.Id : "openai/gpt-5-mini");
        Assert.Null(catalog.Find("nobody/nothing"));
        Assert.Null(catalog.Find(null));
    }

    [Fact]
    public void SearchNarrowsByEveryWordAndFiltersByPrice()
    {
        var dir = TestFolder.New();
        var path = Path.Combine(dir, "models.json");
        File.WriteAllText(path, List);
        var catalog = new OpenRouterCatalog(Plain(), path);
        Assert.Equal(2, catalog.Search("gemma 12").Count);
        Assert.Single(catalog.Search("gemma", CatalogFilter.Free));
        Assert.Single(catalog.Search("gpt mini"));
        Assert.Empty(catalog.Search("gemma gpt"));
        Assert.Equal("openai/gpt-5-mini", catalog.Search(null, CatalogFilter.All)[0].Id);     // no words: the newest first
        Assert.All(catalog.Search(null, CatalogFilter.Cheap), m => Assert.True(m.TypicalAnswerCost <= 0.002m));
    }

    [Fact]
    public async Task TheListIsReadOnceAndKeptOnDiskForTheNextStart()
    {
        var dir = TestFolder.New();
        var path = Path.Combine(dir, "models.json");
        using var server = new FakeHttp(Port);
        server.Respond(200, List);
        var catalog = new OpenRouterCatalog(Plain(), path, new Uri(server.Url));
        Assert.False(catalog.Loaded);
        Assert.True(await catalog.RefreshAsync(default));
        Assert.True(catalog.Loaded);
        Assert.Equal("/api/v1/models", Assert.Single(server.Requests).Path);
        Assert.False(await catalog.RefreshAsync(default));          // fresh: no second request
        Assert.Single(server.Requests);

        var next = new OpenRouterCatalog(Plain(), path, new Uri(server.Url));
        Assert.True(next.Loaded);                                   // from the copy, without the network
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task AFailedReadKeepsWhatTheWalletHasAndNeverThrows()
    {
        var dir = TestFolder.New();
        var path = Path.Combine(dir, "models.json");
        File.WriteAllText(path, List);
        using var server = new FakeHttp(Port);
        server.Respond(500, "{}");
        var catalog = new OpenRouterCatalog(Plain(), path, new Uri(server.Url));
        Assert.False(await catalog.RefreshAsync(default, force: true));
        Assert.True(catalog.Loaded);
        Assert.NotNull(catalog.Find("openai/gpt-5-mini"));
    }
}

/// <summary>A model behind an API key at a company that speaks the OpenAI chat protocol.</summary>
public class CloudChatSourceTests
{
    const int Port = 5362;

    static HttpClient Plain() => new(new SocketsHttpHandler { AllowAutoRedirect = false });

    static CloudChatSource Source(FakeHttp server, string service = "openai", string model = "gpt-5-mini", string? key = "sk-test", IModelCatalog? catalog = null) =>
        new(service, model, null, server.Url.TrimEnd('/'), Plain(), () => key, () => key is not null, catalog is null ? null : () => catalog);

    static TextRequest Plain1() => new([new ChatMessage("user", "Who is right?")], 800, 0.7, null, null);

    sealed class OneModel(CatalogModel model) : IModelCatalog
    {
        public bool Loaded => true;
        public CatalogModel? Find(string? name) => ModelNames.Canon(name) == model.Canonical ? model : null;
        public IReadOnlyList<CatalogModel> Search(string? query, CatalogFilter filter = CatalogFilter.All, int limit = 40) => [model];
    }

    [Fact]
    public async Task TheKeyRidesInTheHeaderAndTheCostIsTheCatalogsPriceForTheTokensUsed()
    {
        using var server = new FakeHttp(Port);
        server.Respond(200, """{"choices":[{"message":{"content":" Alice, by 60 to 40 "}}],"usage":{"prompt_tokens":1000,"completion_tokens":500}}""");
        var catalog = new OneModel(new CatalogModel("openai/gpt-5-mini", "OpenAI: GPT-5 Mini", 400_000, 0.00000025m, 0.000002m, 1));
        var answer = await Source(server, catalog: catalog).GenerateAsync(Plain1(), default);
        Assert.Equal("Alice, by 60 to 40", answer.Text);
        Assert.Equal(0.00125m, answer.Cost);                            // 1000 × 0.25/M + 500 × 2/M
        Assert.Equal((1000, 500), (answer.InputTokens, answer.OutputTokens));

        var call = Assert.Single(server.Requests);
        Assert.Equal("/chat/completions", call.Path);
        Assert.Equal("Bearer sk-test", call.Headers["Authorization"]);
        var body = JsonNode.Parse(call.Body)!.AsObject();
        Assert.Equal("gpt-5-mini", body["model"]!.GetValue<string>());
        Assert.False(body["stream"]!.GetValue<bool>());
    }

    [Fact]
    public void WithoutAPriceTheEstimateIsFlatAndWithOneItFollowsTheTokens()
    {
        using var server = new FakeHttp(Port);
        Assert.Equal(CloudChatSource.FlatEstimate, Source(server).EstimateCost(Plain1()));
        var catalog = new OneModel(new CatalogModel("openai/gpt-5-mini", "x", 1, 0.000001m, 0.000002m, 1));
        Assert.Equal(0.001604m, Source(server, catalog: catalog).EstimateCost(Plain1()));            // 3 input tokens + 800 output
        Assert.Equal("openai/gpt-5-mini", Source(server).Canonical);
        Assert.Equal("minimax/minimax-m2-5", Source(server, "minimax", "MiniMax-M2.5").Canonical);
        Assert.Equal("anthropic/claude-opus-4-6", Source(server, "anthropic", "claude-opus-4-6-20260101").Canonical);
    }

    [Fact]
    public async Task OpenAIsNewerModelsGetTheLengthAsMaxCompletionTokensAndOthersAsMaxTokensAndALearnedRefusalSticks()
    {
        using var server = new FakeHttp(Port);
        server.Respond(200, """{"choices":[{"message":{"content":"a"}}],"usage":{}}""");
        await Source(server, "openai").GenerateAsync(Plain1(), default);
        var openai = JsonNode.Parse(server.Requests[0].Body)!.AsObject();
        Assert.Equal(800, openai["max_completion_tokens"]!.GetValue<int>());
        Assert.Null(openai["max_tokens"]);

        using var other = new FakeHttp(Port + 10);
        other.Respond(200, """{"choices":[{"message":{"content":"a"}}],"usage":{}}""");
        await Source(other, "deepseek", "deepseek-v4-flash").GenerateAsync(Plain1(), default);
        var deepseek = JsonNode.Parse(other.Requests[0].Body)!.AsObject();
        Assert.Equal(800, deepseek["max_tokens"]!.GetValue<int>());
        Assert.Null(deepseek["max_completion_tokens"]);

        // A service that says it wants the other name is believed, once, and remembered.
        using var picky = new FakeHttp(Port + 11);
        picky.Respond(400, """{"error":{"message":"Unsupported parameter: 'max_tokens'. Use 'max_completion_tokens' instead."}}""");
        picky.Respond(200, """{"choices":[{"message":{"content":"b"}}],"usage":{}}""");
        picky.Respond(200, """{"choices":[{"message":{"content":"c"}}],"usage":{}}""");
        var source = Source(picky, "xai", "grok-4.1-fast");
        Assert.Equal("b", (await source.GenerateAsync(Plain1(), default)).Text);
        Assert.Equal("c", (await source.GenerateAsync(Plain1(), default)).Text);
        Assert.Equal(3, picky.Requests.Count);
        Assert.NotNull(JsonNode.Parse(picky.Requests[0].Body)!["max_tokens"]);
        Assert.NotNull(JsonNode.Parse(picky.Requests[1].Body)!["max_completion_tokens"]);
        Assert.NotNull(JsonNode.Parse(picky.Requests[2].Body)!["max_completion_tokens"]);   // the third call did not make the same mistake again
    }

    [Fact]
    public async Task AModelThatTakesNoTemperatureIsAskedWithoutOne()
    {
        using var server = new FakeHttp(Port);
        server.Respond(400, """{"error":{"message":"Unsupported value: 'temperature' does not support 0.7 with this model."}}""");
        server.Respond(200, """{"choices":[{"message":{"content":"fine"}}],"usage":{}}""");
        Assert.Equal("fine", (await Source(server).GenerateAsync(Plain1(), default)).Text);
        Assert.NotNull(JsonNode.Parse(server.Requests[0].Body)!["temperature"]);
        Assert.Null(JsonNode.Parse(server.Requests[1].Body)!["temperature"]);
    }

    [Fact]
    public async Task ChoicesGoConstrainedAndAServiceThatRefusesTheSchemaGetsThemInThePrompt()
    {
        using var server = new FakeHttp(Port);
        server.Respond(400, """{"error":{"message":"response_format is not supported"}}""");
        server.Respond(200, """{"choices":[{"message":{"content":"no"}}],"usage":{}}""");
        var request = new TextRequest([new ChatMessage("user", "Is Paris in Spain?")], 64, 0, null, ["yes", "no"]);
        Assert.Equal("no", (await Source(server).GenerateAsync(request, default)).Text);
        Assert.NotNull(JsonNode.Parse(server.Requests[0].Body)!["response_format"]);
        var retry = JsonNode.Parse(server.Requests[1].Body)!.AsObject();
        Assert.Null(retry["response_format"]);
        Assert.Equal("system", retry["messages"]![1]!["role"]!.GetValue<string>());
    }

    [Fact]
    public async Task TheKeyIsNeverInAnErrorAndNothingIsSentWithoutOne()
    {
        using var server = new FakeHttp(Port);
        server.Respond(401, """{"error":{"message":"Incorrect API key provided: sk-test"}}""");
        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => Source(server).GenerateAsync(Plain1(), default));
        Assert.DoesNotContain("sk-test", failure.Message);
        Assert.Contains("401", failure.Message);

        var none = Source(server, key: null);
        Assert.Equal(SourceState.NoKey, none.State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => none.GenerateAsync(Plain1(), default));
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task AnEmptyAnswerIsAFailureThatStillCostsWhatTheServiceChargedForIt()
    {
        using var server = new FakeHttp(Port);
        server.Respond(200, """{"choices":[{"message":{"content":""}}],"usage":{"prompt_tokens":100,"completion_tokens":0}}""");
        var catalog = new OneModel(new CatalogModel("openai/gpt-5-mini", "x", 1, 0.000001m, 0.000002m, 1));
        var failure = await Assert.ThrowsAsync<TextSourceException>(() => Source(server, catalog: catalog).GenerateAsync(Plain1(), default));
        Assert.Equal(0.0001m, failure.Cost);
        Assert.True(failure.ResponseReceived);
    }

    [Fact]
    public void AKeyIsNeverSentInTheClearToAnotherComputer()
    {
        using var http = Plain();
        Assert.Throws<ArgumentException>(() => new CloudChatSource("custom", "m", null, "http://api.example.org/v1", http, () => "k", () => true));
        Assert.Throws<ArgumentException>(() => new CloudChatSource("custom", "m", null, "not a url", http, () => "k", () => true));
        _ = new CloudChatSource("custom", "m", null, "https://api.example.org/v1", http, () => "k", () => true);
        _ = new CloudChatSource("custom", "m", null, "http://127.0.0.1:4000/v1", http, () => "k", () => true);      // a gateway on this computer is no network
    }

    [Fact]
    public void TheIdKeepsTwoServicesApartAndACustomServiceByItsAddress()
    {
        using var http = Plain();
        var a = new CloudChatSource("openai", "gpt-5-mini", null, "https://api.openai.com/v1", http, () => "k", () => true);
        var b = new CloudChatSource("custom", "gpt-5-mini", null, "https://gateway.example.org/v1", http, () => "k", () => true);
        Assert.Equal("api:openai:gpt-5-mini", a.Id);
        Assert.Equal("api:custom:gpt-5-mini@https://gateway.example.org/v1", b.Id);
        Assert.Equal("OpenAI", a.Name.Split(" · ")[1]);
        Assert.Equal("gateway.example.org", b.Name.Split(" · ")[1]);
        Assert.Equal("api-key:openai", ApiServices.SecretName("openai", null));
        Assert.Equal("api-key:custom:gateway.example.org", ApiServices.SecretName("custom", "https://gateway.example.org/v1"));
    }
}

/// <summary>Adding a model behind a service's key through the registry, and the wallet's sources following the settings.</summary>
public class ApiSourceRegistryTests
{
    static WalletRegistry NewRegistry(SettingsStore store)
    {
        var dir = TestFolder.New();
        return new WalletRegistry(store, new SecretStore(Path.Combine(dir, "secrets.dat")), new ActivityLog(null, DateTimeOffset.UtcNow),
            [new FakeSource("fake-local")], [], new NoRuntimes());
    }

    sealed class NoRuntimes : IRuntimeScanner
    {
        public Task<IReadOnlyList<RuntimeInfo>> ScanAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<RuntimeInfo>>([]);
    }

    [Fact]
    public void AModelBehindAServicesKeyIsAddedWithTheServicesAddressAndTheSharedKeyName()
    {
        var store = new SettingsStore(null);
        var registry = NewRegistry(store);
        var added = registry.AddSource(new SourceDraft(ApiServices.Kind, "gpt-5-mini", "https://elsewhere.example.org/v1", [Vocabulary.Generate], Service: "OpenAI"));
        Assert.True(added.Ok, added.Message);
        Assert.Equal("api:openai:gpt-5-mini", added.Id);
        var setting = store.Snapshot().Sources.Single();
        Assert.Equal("openai", setting.Service);
        Assert.Equal("https://api.openai.com/v1", setting.Endpoint);             // the typed address is ignored for a known service
        Assert.Equal("api-key:openai", setting.SecretRef);
        Assert.True(setting.Offer);

        var second = registry.AddSource(new SourceDraft(ApiServices.Kind, "gpt-5", Service: "openai", Skills: [Vocabulary.Generate]));
        Assert.True(second.Ok, second.Message);
        Assert.All(store.Snapshot().Sources, s => Assert.Equal("api-key:openai", s.SecretRef));      // one key for every model behind the service
        var stops = store.Snapshot().Chain(Vocabulary.Generate).Stops.Select(s => s.SourceId).ToArray();
        Assert.Equal(["api:openai:gpt-5-mini", "api:openai:gpt-5"], stops);
        var info = registry.ListSources().Single(s => s.Id == added.Id);
        Assert.True(info.Paid);
        Assert.Equal("api-key", info.ProviderKind);
        Assert.False(info.HasApiKey);
    }

    [Fact]
    public void OpenRouterModelsShareTheOneOpenRouterKey()
    {
        var store = new SettingsStore(null);
        var registry = NewRegistry(store);
        Assert.True(registry.AddSource(new SourceDraft(SourceKinds.OpenRouter, "x-ai/grok-4.1-fast", Skills: [Vocabulary.Generate])).Ok);
        Assert.Equal("openrouter", store.Snapshot().Sources.Single().SecretRef);
    }

    [Theory]
    [InlineData("nobody", "https://api.example.org/v1", "Unknown service")]
    [InlineData("custom", "http://api.example.org/v1", "https address")]
    [InlineData("custom", "", "https address")]
    public void ABadServiceOrAnAddressWithoutTlsIsRefused(string service, string endpoint, string why)
    {
        var registry = NewRegistry(new SettingsStore(null));
        var result = registry.AddSource(new SourceDraft(ApiServices.Kind, "m", endpoint, [Vocabulary.Generate], Service: service));
        Assert.False(result.Ok);
        Assert.Contains(why, result.Message);
    }

    [Fact]
    public void ACustomServiceKeepsItsOwnAddressAndAnApiModelCannotServeSpeech()
    {
        var store = new SettingsStore(null);
        var registry = NewRegistry(store);
        var added = registry.AddSource(new SourceDraft(ApiServices.Kind, "m1", "https://gateway.example.org/v1/", [Vocabulary.Generate], Service: "custom"));
        Assert.True(added.Ok, added.Message);
        Assert.Equal("api:custom:m1@https://gateway.example.org/v1", added.Id);
        Assert.Equal("api-key:custom:gateway.example.org", store.Snapshot().Sources.Single().SecretRef);
        var speech = registry.AddSource(new SourceDraft(ApiServices.Kind, "m1", Service: "openai", Skills: [Vocabulary.Transcribe]));
        Assert.False(speech.Ok);
    }

    [Fact]
    public void TheWalletsTextSourcesFollowTheSettingsIncludingAModelBehindAKey()
    {
        var home = TestFolder.New();
        var oldHome = Environment.GetEnvironmentVariable(WalletPaths.HomeVariable);
        Environment.SetEnvironmentVariable(WalletPaths.HomeVariable, home);
        try
        {
            var store = new SettingsStore(null);
            using var sources = new SourceSet(store, new SecretStore(Path.Combine(home, "secrets.dat")), new HttpClient());
            var registry = new WalletRegistry(store, new SecretStore(Path.Combine(home, "secrets.dat")), new ActivityLog(null, DateTimeOffset.UtcNow),
                sources.All, sources.Text, sources.Scanner, textFactory: sources.CreateTextSource);
            Assert.True(registry.AddSource(new SourceDraft(ApiServices.Kind, "deepseek-v4-flash", Skills: [Vocabulary.Generate], Service: "deepseek")).Ok);
            var cloud = Assert.IsType<CloudChatSource>(Assert.Single(sources.Text));
            Assert.Equal("api:deepseek:deepseek-v4-flash", cloud.Id);
            Assert.Equal(SourceState.NoKey, cloud.State);
            Assert.Equal("https://api.deepseek.com/v1", cloud.Endpoint);
            Assert.Equal("deepseek/deepseek-v4-flash", cloud.Canonical);
            Assert.Equal("deepseek/deepseek-v4-flash", ModelNames.Canon(cloud.Canonical));
        }
        finally
        {
            Environment.SetEnvironmentVariable(WalletPaths.HomeVariable, oldHome);
        }
    }
}
