using System.Text.Json.Nodes;
using Byoa.Wallet.Activity;
using Byoa.Wallet.Providers;
using Byoa.Wallet.Registry;
using Byoa.Wallet.Secrets;
using Byoa.Wallet.Settings;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Tests;

/// <summary>The visitor's limit on a model's reasoning (tokens) as the Llama app gets it, GLM at Z.ai (answering at once, and the Coding Plan's own address).</summary>
public class ThinkingBudgetAndZaiTests
{
    static HttpClient NoRedirects() => new(new SocketsHttpHandler { AllowAutoRedirect = false });

    static TextRequest Ask(int maxTokens = 300, bool thinking = true, int budget = 0) =>
        new([new ChatMessage("user", "Show me the countries that start with A.")], maxTokens, 0.5, null, null, thinking, budget);

    static JsonObject Sent(FakeHttp server, int index = 0) => JsonNode.Parse(server.Requests[index].Body)!.AsObject();

    const string Answer = """{"choices":[{"message":{"role":"assistant","content":"Albania"},"finish_reason":"stop"}]}""";

    // ------------------------------------------------------------------ the reasoning limit

    [Fact]
    public async Task ALlamaServerGetsTheLimitAndTheAnswerKeepsTheRoomThePageAskedFor()
    {
        using var server = new FakeHttp(5381);
        server.Respond(200, Answer);
        var source = new OpenAiCompatibleSource(SourceKinds.OpenAiCompatible, "qwen3.5-4b", null, server.Url + "v1", NoRedirects());

        await source.GenerateAsync(Ask(maxTokens: 300, budget: 2000), default);

        var body = Sent(server);
        Assert.Equal(2000, body["thinking_budget_tokens"]!.GetValue<int>());
        Assert.Equal(2300, body["max_tokens"]!.GetValue<int>());                   // 300 for the answer on top of 2000 for thinking
    }

    [Fact]
    public async Task NoLimitWithoutThinkingOrWithZeroOrForOtherRuntimes()
    {
        using var server = new FakeHttp(5382);
        for (var i = 0; i < 4; i++) server.Respond(200, Answer);
        var llama = new OpenAiCompatibleSource(SourceKinds.OpenAiCompatible, "m", null, server.Url + "v1", NoRedirects());
        var lmStudio = new OpenAiCompatibleSource(SourceKinds.LmStudio, "m", null, server.Url + "v1", NoRedirects());

        await llama.GenerateAsync(Ask(budget: 0), default);                               // 0 is "no limit"
        await llama.GenerateAsync(Ask(thinking: false, budget: 2000), default);           // no thinking: nothing to limit
        await lmStudio.GenerateAsync(Ask(budget: 2000), default);                         // a runtime that does not know the field is not sent it
        await llama.GenerateAsync(Ask(), default);                                        // a request that carries no limit

        Assert.All(Enumerable.Range(0, 4), i =>
        {
            Assert.Null(Sent(server, i)["thinking_budget_tokens"]);
            Assert.Equal(300, Sent(server, i)["max_tokens"]!.GetValue<int>());
        });
        Assert.False(Sent(server, 1)["chat_template_kwargs"]!["enable_thinking"]!.GetValue<bool>());
    }

    [Fact]
    public async Task AModelThatStillWritesNothingWithinItsLimitIsNotAskedAgain()
    {
        using var server = new FakeHttp(5383);
        server.Respond(200, """{"choices":[{"message":{"content":"","reasoning_content":"hmm"},"finish_reason":"length"}]}""");
        server.Respond(200, Answer);
        var source = new OpenAiCompatibleSource(SourceKinds.OpenAiCompatible, "m", null, server.Url + "v1", NoRedirects());

        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => source.GenerateAsync(Ask(budget: 2000), default));

        Assert.Contains("hidden reasoning", e.Message);
        Assert.Single(server.Requests);                                                   // a second try with four times the room would only make the visitor wait
    }

    [Fact]
    public void TheLimitIsTwoThousandTokensUnlessTheVisitorChangesIt()
    {
        Assert.Equal(2000, new SourceSetting().ThinkingBudget);
        Assert.Equal(0, Ask().ThinkingBudget);                                            // a bare request has none: only the engine adds the visitor's
    }

    // ------------------------------------------------------------------ GLM at Z.ai

    static CloudChatSource Glm(FakeHttp server, string model, string service = "zai-coding") =>
        new(service, model, null, server.Url.TrimEnd('/'), NoRedirects(), () => "key", () => true);

    [Fact]
    public async Task GlmFiveThreeIsToldToAnswerAtOnceWithTheLowestLevelAndNeverWithASwitch()
    {
        using var server = new FakeHttp(5384);
        server.Respond(200, Answer);
        server.Respond(200, Answer);

        await Glm(server, "glm-5.3-flash").GenerateAsync(Ask(thinking: false), default);
        await Glm(server, "glm-5.3-flash").GenerateAsync(Ask(thinking: true), default);

        var quick = Sent(server, 0);
        Assert.Equal("low", quick["reasoning_effort"]!.GetValue<string>());
        Assert.Null(quick["thinking"]);                                                   // Z.ai answers 400 to "disabled" for this model
        var thoughtful = Sent(server, 1);
        Assert.Null(thoughtful["reasoning_effort"]);                                      // left to the model
        Assert.Null(thoughtful["thinking"]);
    }

    [Fact]
    public async Task AnOlderGlmModelGetsTheSwitch()
    {
        using var server = new FakeHttp(5385);
        server.Respond(200, Answer);

        await Glm(server, "glm-4.7", "zai").GenerateAsync(Ask(thinking: false), default);

        var body = Sent(server);
        Assert.Equal("disabled", body["thinking"]!["type"]!.GetValue<string>());
        Assert.Null(body["reasoning_effort"]);
    }

    [Fact]
    public async Task AModelThatSaysItCannotBeSwitchedOffIsAskedForTheLowestLevelFromThenOn()
    {
        using var server = new FakeHttp(5386);
        server.Respond(400, """{"error":{"code":"1210","message":"This model always engages in thinking and cannot be disabled; please use low, high, or max"}}""");
        server.Respond(200, Answer);
        server.Respond(200, Answer);
        var source = Glm(server, "glm-5-turbo");

        Assert.Equal("Albania", (await source.GenerateAsync(Ask(thinking: false), default)).Text);
        await source.GenerateAsync(Ask(thinking: false), default);

        Assert.Equal("disabled", Sent(server, 0)["thinking"]!["type"]!.GetValue<string>());      // the guess
        Assert.Equal("low", Sent(server, 1)["reasoning_effort"]!.GetValue<string>());             // after the refusal
        Assert.Null(Sent(server, 1)["thinking"]);
        Assert.Equal("low", Sent(server, 2)["reasoning_effort"]!.GetValue<string>());             // and it is remembered
        Assert.Equal(3, server.Requests.Count);
    }

    [Fact]
    public async Task OtherServicesAreNotToldAnythingAboutThinking()
    {
        using var server = new FakeHttp(5387);
        server.Respond(200, Answer);

        await new CloudChatSource("deepseek", "deepseek-v4-flash", null, server.Url.TrimEnd('/'), NoRedirects(), () => "key", () => true)
            .GenerateAsync(Ask(thinking: false), default);

        var body = Sent(server);
        Assert.Null(body["thinking"]);
        Assert.Null(body["reasoning_effort"]);
    }

    // ------------------------------------------------------------------ the Coding Plan's own address

    [Fact]
    public void TheCodingPlanIsAnotherAddressOfTheSameCompanyAndSharesItsKey()
    {
        var plain = ApiServices.Find("zai")!;
        var plan = ApiServices.Find("zai-coding")!;
        Assert.Equal("https://api.z.ai/api/paas/v4", plain.BaseUrl);
        Assert.Equal("https://api.z.ai/api/coding/paas/v4", plan.BaseUrl);
        Assert.Contains("Coding Plan", plan.Name);
        Assert.Equal("z-ai", plan.Vendor);                                                 // the price comes from the public catalog like Z.ai's own
        Assert.Equal("api-key:zai", ApiServices.SecretName("zai", null));
        Assert.Equal("api-key:zai", ApiServices.SecretName("zai-coding", null));          // one key, typed once
        Assert.Equal("api-key:openai", ApiServices.SecretName("openai", null));           // the others keep theirs
        Assert.NotEqual(SourceIds.Api("zai", "glm-5.3-flash", plain.BaseUrl), SourceIds.Api("zai-coding", "glm-5.3-flash", plan.BaseUrl));
    }

    [Fact]
    public void AModelAddedBehindTheCodingPlanGoesToItsAddressWithTheSharedKeyName()
    {
        var store = new SettingsStore(null);
        var dir = TestFolder.New();
        var registry = new WalletRegistry(store, new SecretStore(Path.Combine(dir, "secrets.dat")), new ActivityLog(null, DateTimeOffset.UtcNow),
            [new FakeSource("fake-local")], [], new NoRuntimes());

        var plain = registry.AddSource(new SourceDraft(ApiServices.Kind, "glm-5.3-flash", Skills: [Vocabulary.Generate], Service: "zai"));
        var plan = registry.AddSource(new SourceDraft(ApiServices.Kind, "glm-5.3-flash", "https://elsewhere.example.org/v1", [Vocabulary.Generate], Service: "zai-coding"));

        Assert.True(plain.Ok, plain.Message);
        Assert.True(plan.Ok, plan.Message);
        Assert.NotEqual(plain.Id, plan.Id);                                                // the same model at two addresses: two models
        var sources = store.Snapshot().Sources;
        Assert.Equal("https://api.z.ai/api/coding/paas/v4", sources.Single(s => s.Id == plan.Id).Endpoint);   // a typed address is ignored for a known service
        Assert.All(sources, s => Assert.Equal("api-key:zai", s.SecretRef));
    }

    sealed class NoRuntimes : IRuntimeScanner
    {
        public Task<IReadOnlyList<RuntimeInfo>> ScanAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<RuntimeInfo>>([]);
    }
}
