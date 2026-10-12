using Byoa.Wallet.Providers;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Tests;

/// <summary>The runtime scanner against fake LM Studio and Ollama servers on loopback ports: what it asks, what
/// it reports, and that a runtime which is not there is simply "not running".</summary>
public class ScannerTests
{
    static RuntimeScanner Scanner(params (string Kind, string Endpoint)[] added) =>
        new(new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }), () => added);

    [Fact]
    public async Task ItSeesTheModelsOfLmStudioAndOllamaAndWhichAreLoaded()
    {
        using var lmStudio = new FakeHttp(5324);
        lmStudio.Respond(200, """{"data":[{"id":"qwen/qwen3.5-4b","state":"loaded"},{"id":"qwen/qwen3.5-1.7b","state":"not-loaded"}]}""");
        using var ollama = new FakeHttp(5325);
        ollama.Respond(200, """{"models":[{"name":"llama3.2:3b","size":2019393189},{"name":"qwen3:4b","size":3958763882}]}""");
        ollama.Respond(200, """{"models":[{"name":"llama3.2:3b","size_vram":2000000000}]}""");

        var infos = await Scanner((SourceKinds.LmStudio, lmStudio.Url), (SourceKinds.Ollama, ollama.Url)).ScanAsync(default);

        var lm = infos.Single(r => r.Endpoint == lmStudio.Url.TrimEnd('/'));
        Assert.True(lm.Running);
        Assert.Equal(2, lm.Models.Count);
        Assert.True(lm.Models[0].Loaded);                         // in memory right now
        Assert.False(lm.Models[1].Loaded);
        Assert.Equal("/api/v0/models", lmStudio.Requests[0].Path);

        var local = infos.Single(r => r.Endpoint == ollama.Url.TrimEnd('/'));
        Assert.True(local.Running);
        Assert.True(local.Models.Single(m => m.Id == "llama3.2:3b").Loaded);
        Assert.False(local.Models.Single(m => m.Id == "qwen3:4b").Loaded);
        Assert.Equal(2019393189, local.Models[0].SizeBytes);
        Assert.Equal("/api/tags", ollama.Requests[0].Path);
        Assert.Equal("/api/ps", ollama.Requests[1].Path);
    }

    [Fact]
    public async Task LmStudioWithoutTheV0ApiFallsBackToThePlainList()
    {
        using var lmStudio = new FakeHttp(5327);
        lmStudio.Respond(404, "{}");
        lmStudio.Respond(200, """{"data":[{"id":"qwen/qwen3.5-4b"}]}""");

        var infos = await Scanner((SourceKinds.LmStudio, lmStudio.Url)).ScanAsync(default);

        var lm = infos.Single(r => r.Endpoint == lmStudio.Url.TrimEnd('/'));
        Assert.True(lm.Running);
        Assert.Equal("qwen/qwen3.5-4b", Assert.Single(lm.Models).Id);
        Assert.False(lm.Models[0].Loaded);                        // the plain list says nothing about memory
        Assert.Equal("/v1/models", lmStudio.Requests[1].Path);
    }

    [Fact]
    public async Task AnAddedOpenAiCompatibleServerIsProbedAtModels()
    {
        using var jan = new FakeHttp(5328);
        jan.Respond(200, """{"data":[{"id":"mistral-7b-instruct"}]}""");

        var infos = await Scanner((SourceKinds.OpenAiCompatible, jan.Url)).ScanAsync(default);

        var found = infos.Single(r => r.Endpoint == jan.Url.TrimEnd('/'));
        Assert.True(found.Running);
        Assert.Equal("mistral-7b-instruct", Assert.Single(found.Models).Id);
        Assert.Equal("/models", jan.Requests[0].Path);
    }

    [Fact]
    public async Task ARuntimeThatIsNotThereIsJustNotRunning()
    {
        var infos = await Scanner((SourceKinds.Ollama, "http://127.0.0.1:5329")).ScanAsync(default);   // nothing listens there

        var down = infos.Single(r => r.Endpoint == "http://127.0.0.1:5329");
        Assert.False(down.Running);
        Assert.Empty(down.Models);
    }

    [Fact]
    public async Task AnOpenRouterKindIsNeverProbedOnTheLocalNetwork()
    {
        var infos = await Scanner((SourceKinds.OpenRouter, "http://127.0.0.1:5330")).ScanAsync(default);

        Assert.DoesNotContain(infos, r => r.Endpoint.Contains("5330"));               // OpenRouter is a key, not a runtime
        Assert.Equal(2, infos.Count);                             // only the two built-in ports were probed
    }
}
