using System.Text.Json.Nodes;
using Byoa.Wallet.Providers;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Tests;

/// <summary>The OpenRouter source asked for pictures (PROPOSAL-v3 4.4) against a stand-in OpenRouter on a loopback port: what it sends, what it reads back, what it costs.</summary>
public class OpenRouterImageSourceTests
{
    const int Port = 5395;

    static HttpClient NoRedirects() => new(new SocketsHttpHandler { AllowAutoRedirect = false });

    static OpenRouterTextSource Source(FakeHttp server, string? key = "sk-test", Func<IModelCatalog>? catalog = null) =>
        new("google/nano", null, null, NoRedirects(), () => key, () => key is not null, new Uri(server.Url), catalog);

    static ImageRequest Ask(string aspect = "", params Picture[] references) =>
        new("A small red fox", references, 1, aspect.Length == 0 ? null : aspect);

    static string Answer(string[] urls, string usage = "{\"cost\":0.068}")
    {
        var images = new JsonArray(urls.Select(u => (JsonNode)new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = u } }).ToArray());
        return new JsonObject
        {
            ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = "here", ["images"] = images } }),
            ["usage"] = JsonNode.Parse(usage),
        }.ToJsonString();
    }

    [Fact]
    public async Task TheRequestAsksForPicturesWithTheKeyInTheHeaderAndTheAspectOnlyWhenTheSiteGaveOne()
    {
        var png = TestPictures.Png(8, 8);
        using var server = new FakeHttp(Port);
        server.Respond(200, Answer([TestPictures.DataUrl(png)]));
        server.Respond(200, Answer([TestPictures.DataUrl(png)]));
        await Source(server).GenerateImageAsync(Ask("16:9"), default);
        await Source(server).GenerateImageAsync(Ask(), default);

        var first = Assert.IsType<JsonObject>(JsonNode.Parse(server.Requests[0].Body));
        Assert.Equal("/api/v1/chat/completions", server.Requests[0].Path);
        Assert.Equal("Bearer sk-test", server.Requests[0].Headers["Authorization"]);
        Assert.Equal("google/nano", first["model"]!.GetValue<string>());
        Assert.Equal(["image", "text"], first["modalities"]!.AsArray().Select(m => m!.GetValue<string>()));
        Assert.True(first["usage"]!["include"]!.GetValue<bool>());
        Assert.False(first["stream"]!.GetValue<bool>());
        Assert.Equal("16:9", first["image_config"]!["aspect_ratio"]!.GetValue<string>());
        var message = Assert.Single(first["messages"]!.AsArray())!;
        Assert.Equal("user", message["role"]!.GetValue<string>());
        var part = Assert.Single(message["content"]!.AsArray())!;
        Assert.Equal(("text", "A small red fox"), (part["type"]!.GetValue<string>(), part["text"]!.GetValue<string>()));

        Assert.Null(JsonNode.Parse(server.Requests[1].Body)!["image_config"]);               // no aspect asked: the model's own
    }

    [Fact]
    public async Task ReferencePicturesRideInTheMessageAsDataUrlsOfTheirOwnBytes()
    {
        var reference = TestPictures.Png(4, 4);
        using var server = new FakeHttp(Port);
        server.Respond(200, Answer([TestPictures.DataUrl(TestPictures.Png(8, 8))]));
        await Source(server).GenerateImageAsync(Ask("1:1", new Picture(Pictures.Png, reference)), default);

        var content = JsonNode.Parse(Assert.Single(server.Requests).Body)!["messages"]![0]!["content"]!.AsArray();
        Assert.Equal(2, content.Count);
        Assert.Equal("image_url", content[1]!["type"]!.GetValue<string>());
        Assert.Equal("data:image/png;base64," + Convert.ToBase64String(reference), content[1]!["image_url"]!["url"]!.GetValue<string>());
    }

    [Fact]
    public async Task ThePicturesAreTheDataUrlsOfTheFirstChoiceAndALinkIsNeverFollowed()
    {
        var first = TestPictures.Png(8, 8);
        var second = TestPictures.Png(9, 9);
        using var server = new FakeHttp(Port);
        server.Respond(200, Answer([TestPictures.DataUrl(first), "https://elsewhere.example.test/x.png", TestPictures.DataUrl(second, "image/webp"), "data:image/png;base64,@@@"]));
        var answer = await Source(server).GenerateImageAsync(Ask(), default);

        Assert.Equal(2, answer.Pictures.Count);                                            // the link and the broken data are not pictures
        Assert.Equal(first, answer.Pictures[0]);
        Assert.Equal(second, answer.Pictures[1]);                                          // (the type the provider wrote is not read: the wallet finds it in the bytes)
        Assert.Equal(0.068m, answer.Cost);
        Assert.Equal("google/nano · OpenRouter", answer.Detail);
        Assert.Single(server.Requests);                                                    // nothing was fetched
    }

    [Fact]
    public async Task AnswerWithoutAPictureIsAFailureThatStillCarriesWhatItCost()
    {
        using var server = new FakeHttp(Port);
        server.Respond(200, """{"choices":[{"message":{"role":"assistant","content":"I cannot draw that"}}],"usage":{"cost":0.01}}""");
        var failure = await Assert.ThrowsAsync<TextSourceException>(() => Source(server).GenerateImageAsync(Ask(), default));
        Assert.Equal(0.01m, failure.Cost);                                                 // the visitor is charged for the try
        Assert.True(failure.ResponseReceived);
    }

    [Fact]
    public async Task AnErrorNamesTheStatusAndNeverTheKeyOrTheBody()
    {
        using var server = new FakeHttp(Port);
        server.Respond(500, """{"error":{"message":"your key sk-test is bad and your prompt was A small red fox"}}""");
        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => Source(server).GenerateImageAsync(Ask(), default));
        Assert.Equal("OpenRouter answered HTTP 500", failure.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Source(server, key: null).GenerateImageAsync(Ask(), default));
    }

    [Fact]
    public async Task WithNoReportedCostTheAnswerSaysSoAndTheEstimateStaysTheEnginesToUse()
    {
        using var server = new FakeHttp(Port);
        server.Respond(200, Answer([TestPictures.DataUrl(TestPictures.Png(8, 8))], usage: "{}"));
        Assert.Null((await Source(server).GenerateImageAsync(Ask(), default)).Cost);
    }

    [Fact]
    public void ThePriceOfAPictureIsUnknownUnlessThePublicListSaysTheModelIsFree()
    {
        using var server = new FakeHttp(Port);
        var free = new CatalogModel("google/nano", "Nano", 1000, 0m, 0m, 0, ["text"], ["image"]);
        var priced = new CatalogModel("google/nano", "Nano", 1000, 0.000001m, 0.000002m, 0, ["text"], ["image"]);
        Assert.Equal(0m, Source(server, catalog: () => new OneModel(free)).EstimateImageCost(Ask()));
        // a price per token is no price per picture: the round figure stays, whatever the list says of tokens
        Assert.Equal(OpenRouterTextSource.FlatEstimate, Source(server, catalog: () => new OneModel(priced)).EstimateImageCost(Ask()));
        Assert.Equal(OpenRouterTextSource.FlatEstimate, Source(server).EstimateImageCost(Ask()));
    }

    [Theory]
    [InlineData("http://127.0.0.1:5899/", true, true)]
    [InlineData("http://localhost:5899", true, true)]
    [InlineData("http://127.0.0.1:5899/", false, false)]       // the person's own wallet never reads it
    [InlineData("https://openrouter.ai/", true, false)]
    [InlineData("http://example.com/", true, false)]            // nothing but an address on this computer
    [InlineData("https://127.0.0.1:5899/", true, false)]
    [InlineData("not an address", true, false)]
    [InlineData(null, true, false)]
    public void AStandInForOpenRouterIsTakenOnlyInATestRunAndOnlyOnThisComputer(string? given, bool testRun, bool taken)
    {
        Assert.Equal(taken, SourceSet.StandIn(given, testRun) is not null);
    }

    sealed class OneModel(CatalogModel model) : IModelCatalog
    {
        public bool Loaded => true;
        public CatalogModel? Find(string? name) => ModelNames.Canon(name) == model.Canonical ? model : null;
        public IReadOnlyList<CatalogModel> Search(string? query, CatalogFilter filter = CatalogFilter.All, int limit = 40, string skill = Vocabulary.Generate) => [model];
    }
}
