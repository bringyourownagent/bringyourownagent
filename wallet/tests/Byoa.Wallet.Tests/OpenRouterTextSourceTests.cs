using System.Text.Json.Nodes;
using Byoa.Wallet.Providers;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Tests;

/// <summary>The OpenRouter text source against a fake OpenRouter on a loopback port: the key rides in the header
/// and never in an error, the cost comes back from the usage, and a model that refuses the schema gets the prompt.</summary>
public class OpenRouterTextSourceTests
{
    const int Port = 5323;

    static HttpClient NoRedirects() => new(new SocketsHttpHandler { AllowAutoRedirect = false });

    static OpenRouterTextSource Source(FakeHttp server, string? key = "sk-test") =>
        new("google/gemma-3-12b-it", null, null, NoRedirects(), () => key, () => key is not null, new Uri(server.Url));

    static TextRequest Plain() =>
        new([new ChatMessage("user", "What is the capital of France?")], 512, 0.7, null, null);

    static TextRequest Quiz() =>
        new([new ChatMessage("user", "Is Paris the capital of France?")], 512, 0.7, null, ["yes", "no"]);

    [Fact]
    public async Task TheKeyGoesInTheHeaderAndTheCostComesFromTheUsage()
    {
        using var server = new FakeHttp(Port);
        server.Respond(200, """{"choices":[{"message":{"content":"  Paris "}}],"usage":{"prompt_tokens":10,"completion_tokens":4,"cost":0.000123}}""");
        var answer = await Source(server).GenerateAsync(Plain(), default);
        Assert.Equal("Paris", answer.Text);
        Assert.Equal(0.000123m, answer.Cost);                     // what the API says it cost, not a guess
        Assert.Equal((10, 4), (answer.InputTokens, answer.OutputTokens));
        Assert.Equal("google/gemma-3-12b-it · OpenRouter", answer.Detail);

        var call = Assert.Single(server.Requests);
        Assert.Equal("/api/v1/chat/completions", call.Path);
        Assert.Equal("Bearer sk-test", call.Headers["Authorization"]);
        Assert.Equal("BYOA agent wallet", call.Headers["X-Title"]);
        var body = JsonNode.Parse(call.Body)!.AsObject();
        Assert.Equal("google/gemma-3-12b-it", body["model"]!.GetValue<string>());
        Assert.True(body["usage"]!["include"]!.GetValue<bool>());
        Assert.Null(body["response_format"]);
    }

    [Fact]
    public async Task ChoicesGoConstrainedAndAskForTheParameters()
    {
        using var server = new FakeHttp(Port);
        server.Respond(200, """{"choices":[{"message":{"content":"{\"answer\":\"yes\"}"}}],"usage":{"cost":0.0004}}""");
        Assert.Equal("yes", (await Source(server).GenerateAsync(Quiz(), default)).Text);

        var body = JsonNode.Parse(Assert.Single(server.Requests).Body)!.AsObject();
        Assert.Equal("json_schema", body["response_format"]!["type"]!.GetValue<string>());
        Assert.True(body["provider"]!["require_parameters"]!.GetValue<bool>());   // a provider that ignores the schema is refused
    }

    [Fact]
    public async Task AModelThatRefusesTheSchemaGetsTheChoicesInThePrompt()
    {
        using var server = new FakeHttp(Port);
        server.Respond(400, """{"error":{"message":"provider does not support structured output"}}""");
        server.Respond(200, """{"choices":[{"message":{"content":"no"}}],"usage":{"cost":0.0002}}""");
        Assert.Equal("no", (await Source(server).GenerateAsync(Quiz(), default)).Text);

        var calls = server.Requests;
        Assert.Equal(2, calls.Count);
        Assert.NotNull(JsonNode.Parse(calls[0].Body)!["response_format"]);
        var retry = JsonNode.Parse(calls[1].Body)!.AsObject();
        Assert.Null(retry["response_format"]);
        Assert.Null(retry["provider"]);
        var last = retry["messages"]!.AsArray()[^1]!.AsObject();
        Assert.Equal("system", last["role"]!.GetValue<string>());
        Assert.Contains("\"yes\" | \"no\"", last["content"]!.GetValue<string>());
        Assert.DoesNotContain("sk-test", calls[1].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARedirectIsNotFollowedWithTheApiKey()
    {
        using var server = new FakeHttp(Port);
        server.Respond(302, "{\"location\":\"https://elsewhere.example/\"}");
        var e = await Assert.ThrowsAsync<HttpRequestException>(() => Source(server).GenerateAsync(Plain(), default));
        Assert.Equal("OpenRouter answered HTTP 302", e.Message);
        Assert.Single(server.Requests);
        Assert.Equal("Bearer sk-test", server.Requests[0].Headers["Authorization"]);
    }

    [Fact]
    public async Task ABadKeyIsAnErrorWithoutTheKey()
    {
        using var server = new FakeHttp(Port);
        var source = Source(server);
        server.Respond(401, """{"error":{"message":"Invalid key sk-test provided in request"}}""");
        var e = await Assert.ThrowsAsync<HttpRequestException>(() => source.GenerateAsync(Plain(), default));
        Assert.Equal("OpenRouter answered HTTP 401", e.Message);  // the body echoed the key; the error may not
        Assert.DoesNotContain("sk-test", e.Message);
        Assert.Equal("key added · last call failed: HTTP 401", source.StateText);
        Assert.DoesNotContain("sk-test", source.StateText);

        server.Respond(200, """{"choices":[{"message":{"content":"Paris"}}],"usage":{"cost":0.0001}}""");
        Assert.Equal("Paris", (await source.GenerateAsync(Plain(), default)).Text);
        Assert.StartsWith("key added · paid, about", source.StateText);           // the failure is forgotten
    }

    [Fact]
    public async Task WithoutAKeyNothingRuns()
    {
        using var server = new FakeHttp(Port);
        var source = Source(server, key: null);
        Assert.Equal(SourceState.NoKey, source.State);
        Assert.Equal("no API key added", source.StateText);
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => source.GenerateAsync(Plain(), default));
        Assert.Equal("no OpenRouter key", e.Message);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task AUnhelpfulAnswerIsAPlainError()
    {
        using var server = new FakeHttp(Port);
        server.Respond(200, """{"choices":[{"message":{"content":""}}]}""");
        var empty = await Assert.ThrowsAsync<TextSourceException>(() => Source(server).GenerateAsync(Plain(), default));
        Assert.Equal("OpenRouter answered nothing", empty.Message);
        Assert.Equal(OpenRouterTextSource.FlatEstimate, empty.Cost);

        server.Respond(200, """{"choices":[{"message":{"content":"maybe"}}]}""");
        var missed = await Assert.ThrowsAsync<TextSourceException>(() => Source(server).GenerateAsync(Quiz(), default));
        Assert.Equal("OpenRouter did not answer with one of the choices", missed.Message);

        server.Respond(200, "hello, not JSON");
        var httpError = await Assert.ThrowsAsync<HttpRequestException>(() => Source(server).GenerateAsync(Plain(), default));
        Assert.Equal("OpenRouter answered something that is not JSON", httpError.Message);
    }

    [Fact]
    public async Task MissingOrZeroUsageCostUsesTheShortAnswerEstimate()
    {
        using var server = new FakeHttp(Port);
        var source = Source(server);
        server.Respond(200, """{"choices":[{"message":{"content":"Paris"}}],"usage":{"prompt_tokens":2}}""");
        var missing = await source.GenerateAsync(Plain(), default);
        Assert.Equal(OpenRouterTextSource.FlatEstimate, missing.Cost);

        server.Respond(200, """{"choices":[{"message":{"content":"Paris"}}],"usage":{"cost":0}}""");
        var zero = await source.GenerateAsync(Plain(), default);
        Assert.Equal(OpenRouterTextSource.FlatEstimate, zero.Cost);

        server.Respond(200, """{"choices":[{"message":{"content":"Paris"}}],"usage":{"cost":"0.0003"}}""");
        Assert.Equal(0.0003m, (await source.GenerateAsync(Plain(), default)).Cost);
    }

    [Fact]
    public async Task TheEstimateComesFromThePriceListWhenItKnowsTheModel()
    {
        using var server = new FakeHttp(Port);
        var source = Source(server);
        Assert.Equal(OpenRouterTextSource.FlatEstimate, source.EstimateCost(Plain()));    // the round guess until prices arrive
        Assert.StartsWith("key added · paid, about $0.02", source.StateText);

        server.Respond(200, """
            {"data":[{"id":"other/model","pricing":{"prompt":"0.1","completion":"0.2"}},
            {"id":"google/gemma-3-12b-it","pricing":{"prompt":"0.0000015","completion":"0.000002"}}]}
            """);
        await source.PrimePricesAsync();
        Assert.Equal(0.001036m, source.EstimateCost(Plain()));    // 7.5 prompt tokens plus 512 answer tokens
        Assert.StartsWith("key added · paid, about $0.001027", source.StateText);   // the state line quotes a short hello, not this request
        Assert.Equal("/api/v1/models", Assert.Single(server.Requests).Path);

        await source.PrimePricesAsync();                          // the list is read about once an hour, not per call
        Assert.Single(server.Requests);
    }

    [Fact]
    public void TheWalletsCatalogGivesThePriceBeforeTheSourceHasReadTheListItself()
    {
        using var server = new FakeHttp(Port);
        var catalog = new Catalog(new CatalogModel("google/gemma-3-12b-it", "Gemma", 1, 0.0000015m, 0.000002m, 1));
        var source = new OpenRouterTextSource("google/gemma-3-12b-it", null, null, NoRedirects(), () => "sk-test", () => true, new Uri(server.Url), () => catalog);
        Assert.Equal(0.001036m, source.EstimateCost(Plain()));              // the same price as after reading the list, from the first call on
        Assert.NotEqual(OpenRouterTextSource.FlatEstimate, source.EstimateCost(Plain()));
        var unknown = new OpenRouterTextSource("some/other-model", null, null, NoRedirects(), () => "sk-test", () => true, new Uri(server.Url), () => catalog);
        Assert.Equal(OpenRouterTextSource.FlatEstimate, unknown.EstimateCost(Plain()));
    }

    sealed class Catalog(CatalogModel model) : IModelCatalog
    {
        public bool Loaded => true;
        public CatalogModel? Find(string? name) => ModelNames.Canon(name) == model.Canonical ? model : null;
        public IReadOnlyList<CatalogModel> Search(string? query, CatalogFilter filter = CatalogFilter.All, int limit = 40) => [model];
    }

    [Fact]
    public async Task APriceListWithoutTheModelLeavesTheFlatEstimate()
    {
        using var server = new FakeHttp(Port);
        var source = Source(server);
        server.Respond(200, """{"data":[{"id":"other/model","pricing":{"prompt":"0.1","completion":"0.2"}}]}""");
        await source.PrimePricesAsync();
        Assert.Equal(OpenRouterTextSource.FlatEstimate, source.EstimateCost(Plain()));

        server.Respond(500, "no");
        await source.PrimePricesAsync();                          // a dead list never breaks the source
        Assert.Equal(OpenRouterTextSource.FlatEstimate, source.EstimateCost(Plain()));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("1e300")]
    [InlineData("1001")]
    public void AbsurdReportedCostsAreIgnored(string value)
    {
        var usage = JsonNode.Parse("{\"cost\":" + value + "}");
        Assert.Null(ChatCompletions.Cost(usage));
    }
}
