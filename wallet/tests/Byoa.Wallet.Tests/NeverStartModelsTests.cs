using System.Text.Json.Nodes;
using Byoa.Wallet.Activity;
using Byoa.Wallet.Engine;
using Byoa.Wallet.Protocol;
using Byoa.Wallet.Providers;
using Byoa.Wallet.Settings;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Tests;

/// <summary>"Never start a model for me": what the runtimes say about memory (the Llama app, LM Studio, Ollama), and a route that sends
/// nothing to a model that is not in memory, so that no runtime starts one for a site's request.</summary>
public class NeverStartModelsTests
{
    static HttpClient NoRedirects() => new(new SocketsHttpHandler { AllowAutoRedirect = false });

    // ------------------------------------------------------------------ what a runtime says

    [Fact]
    public async Task TheLlamaAppsListSaysWhichModelsAreInMemory()
    {
        using var llama = new FakeHttp(5340);
        var list = """{"data":[{"id":"a","status":{"value":"loaded"}},{"id":"b","status":{"value":"unloaded"}},{"id":"c","status":{"value":"loading"}},{"id":"d","status":{"value":"downloading"}},{"id":"e"}]}""";
        for (var i = 0; i < 5; i++) llama.Respond(200, list);
        var api = new Uri(llama.Url + "v1/");

        Assert.True(await LoadState.QueryAsync(NoRedirects(), SourceKinds.OpenAiCompatible, api, "a", default));
        Assert.False(await LoadState.QueryAsync(NoRedirects(), SourceKinds.OpenAiCompatible, api, "b", default));
        Assert.True(await LoadState.QueryAsync(NoRedirects(), SourceKinds.OpenAiCompatible, api, "c", default));      // somebody started it: no new start
        Assert.False(await LoadState.QueryAsync(NoRedirects(), SourceKinds.OpenAiCompatible, api, "d", default));
        Assert.Null(await LoadState.QueryAsync(NoRedirects(), SourceKinds.OpenAiCompatible, api, "e", default));      // a plain entry says nothing
        Assert.All(llama.Requests, r => Assert.Equal("/v1/models", r.Path));                                          // a list: it cannot start anything
    }

    [Fact]
    public async Task LmStudioAndOllamaSayWhatIsInMemoryWithTheirOwnLists()
    {
        using var lmStudio = new FakeHttp(5341);
        lmStudio.Respond(200, """{"data":[{"id":"qwen/qwen3.5-4b","state":"loaded"},{"id":"other","state":"not-loaded"}]}""");
        lmStudio.Respond(200, """{"data":[{"id":"qwen/qwen3.5-4b","state":"loaded"},{"id":"other","state":"not-loaded"}]}""");
        var lm = new Uri(lmStudio.Url + "v1/");
        Assert.True(await LoadState.QueryAsync(NoRedirects(), SourceKinds.LmStudio, lm, "qwen/qwen3.5-4b", default));
        Assert.False(await LoadState.QueryAsync(NoRedirects(), SourceKinds.LmStudio, lm, "other", default));
        Assert.All(lmStudio.Requests, r => Assert.Equal("/api/v0/models", r.Path));

        using var ollama = new FakeHttp(5342);
        ollama.Respond(200, """{"models":[{"name":"llama3.2:3b"}]}""");
        ollama.Respond(200, """{"models":[{"name":"llama3.2:3b"}]}""");
        var ol = new Uri(ollama.Url + "v1/");
        Assert.True(await LoadState.QueryAsync(NoRedirects(), SourceKinds.Ollama, ol, "llama3.2:3b", default));
        Assert.False(await LoadState.QueryAsync(NoRedirects(), SourceKinds.Ollama, ol, "qwen3:4b", default));
        Assert.All(ollama.Requests, r => Assert.Equal("/api/ps", r.Path));
    }

    [Fact]
    public async Task ARuntimeThatDoesNotAnswerOrDoesNotKnowTheModelSaysNothing()
    {
        Assert.Null(await LoadState.QueryAsync(NoRedirects(), SourceKinds.OpenAiCompatible, new Uri("http://127.0.0.1:5349/v1/"), "a", default));
        using var llama = new FakeHttp(5343);
        llama.Respond(200, """{"data":[{"id":"a","status":{"value":"loaded"}}]}""");
        Assert.Null(await LoadState.QueryAsync(NoRedirects(), SourceKinds.OpenAiCompatible, new Uri(llama.Url + "v1/"), "not-there", default));
    }

    [Fact]
    public async Task TheScannerReportsLoadedOnlyWhenTheRuntimeSaysIt()
    {
        using var llama = new FakeHttp(5344);
        llama.Respond(200, """{"data":[{"id":"a","status":{"value":"loaded"}},{"id":"b","status":{"value":"unloaded"}}]}""");
        using var plain = new FakeHttp(5345);
        plain.Respond(200, """{"data":[{"id":"x"}]}""");
        var scanner = new RuntimeScanner(NoRedirects(), () => [(SourceKinds.OpenAiCompatible, llama.Url), (SourceKinds.OpenAiCompatible, plain.Url)]);

        var infos = await scanner.ScanAsync(default);

        var l = infos.Single(r => r.Endpoint == llama.Url.TrimEnd('/'));
        Assert.True(l.Models.Single(m => m.Id == "a") is { Loaded: true, LoadedKnown: true });
        Assert.True(l.Models.Single(m => m.Id == "b") is { Loaded: false, LoadedKnown: true });
        Assert.True(infos.Single(r => r.Endpoint == plain.Url.TrimEnd('/')).Models[0] is { Loaded: false, LoadedKnown: false });
    }

    [Fact]
    public void TheSourcesPageSaysIfAModelIsLoadedWhenTheRuntimeTellsAndNothingWhenItDoesNot()
    {
        var source = new OpenAiCompatibleSource(SourceKinds.OpenAiCompatible, "m", null, "http://127.0.0.1:9931/v1", NoRedirects()) { Up = true, Listed = true };
        Assert.Equal("ready · Llama", source.StateText);
        source.Loaded = false;
        Assert.Equal("ready · Llama · not loaded", source.StateText);
        source.Loaded = true;
        Assert.Equal("ready · Llama · loaded", source.StateText);
    }

    // ------------------------------------------------------------------ the route

    /// <summary>A text source whose runtime says (or does not say) whether its model is in memory.</summary>
    sealed class LoadAwareSource(string id, bool? loaded) : FakeTextSource(id), ILoadCheck
    {
        public bool? Loaded { get; set; } = loaded;
        public int Asked { get; private set; }
        public Task<bool?> LoadedNowAsync(CancellationToken ct) { Asked++; return Task.FromResult(Loaded); }
    }

    const string Site = "https://quiz.example.org";

    const string Manifest = """
        {"byoa":"0.1-draft","purpose":{"summary":"Family quiz","limits":{"maxTextChars":8000,"maxCallsPerMinute":40}},
        "skills":{"text.generate":{"required":true}},"privacy":"personal-voice","expectedCost":{"perSession":"0.05 USD"}}
        """;

    static async Task<(WalletEngine Engine, SettingsStore Store, string Grant)> StartAsync(params ITextSource[] sources)
    {
        var store = new SettingsStore(null);
        var time = new ManualTime();
        var engine = new WalletEngine(store, [], new ActivityLog(null, time.Now), new ScriptedUi(), time, sources);
        store.Update(s => Router.EnsureTextChain(s, sources));
        var granted = Assert.IsType<JsonObject>(await engine.HandleAsync(Clips.Request(Site, click: true, Manifest)));
        return (engine, store, granted["grantId"]!.GetValue<string>());
    }

    static WireMessage Call(string grant) =>
        new(2, Wire.Call, Site, false, new JsonObject
        {
            ["grantId"] = grant, ["skill"] = "text.generate",
            ["input"] = new JsonObject { ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "Hi" }) },
            ["params"] = new JsonObject(),
        });

    static bool Ok(JsonNode? result) => result is JsonObject o && o["ok"]?.GetValue<bool>() == true;

    [Fact]
    public async Task ByDefaultTheFirstModelAnswersWhetherOrNotItIsInMemory()
    {
        var first = new LoadAwareSource("first", loaded: false) { Answers = new Queue<string>(["one"]) };
        var second = new LoadAwareSource("second", loaded: true) { Answers = new Queue<string>(["two"]) };
        var (engine, _, grant) = await StartAsync(first, second);

        Assert.True(Ok(await engine.HandleAsync(Call(grant))));

        Assert.Equal(1, first.Calls);                                 // the runtime would start it: the old behaviour, unchanged
        Assert.Equal(0, first.Asked);
    }

    [Fact]
    public async Task WithTheSwitchOnAModelThatIsNotInMemoryGetsNothingAndTheNextOneAnswers()
    {
        var first = new LoadAwareSource("first", loaded: false) { Answers = new Queue<string>(["one"]) };
        var second = new LoadAwareSource("second", loaded: true) { Answers = new Queue<string>(["two"]) };
        var (engine, store, grant) = await StartAsync(first, second);
        store.Update(s => s.NeverStartModels = true);

        var result = Assert.IsType<JsonObject>(await engine.HandleAsync(Call(grant)));

        Assert.True(Ok(result));
        Assert.Equal(0, first.Calls);                                 // nothing was sent to it, so nothing could start it
        Assert.Equal(1, second.Calls);
        Assert.Equal(1, first.Asked);
    }

    [Fact]
    public async Task WithTheSwitchOnAndNothingInMemoryTheSiteHearsThatAiIsUnavailableAndNothingIsStarted()
    {
        var first = new LoadAwareSource("first", loaded: false);
        var second = new LoadAwareSource("second", loaded: false);
        var (engine, store, grant) = await StartAsync(first, second);
        store.Update(s => s.NeverStartModels = true);

        Assert.False(Ok(await engine.HandleAsync(Call(grant))));

        Assert.Equal(0, first.Calls + second.Calls);
    }

    [Fact]
    public async Task ARuntimeThatDoesNotSayWhatIsInMemoryIsNotHeldBack()
    {
        var unknown = new LoadAwareSource("unknown", loaded: null) { Answers = new Queue<string>(["answer"]) };
        var plain = new FakeTextSource("plain") { Answers = new Queue<string>(["plain"]) };          // a source that cannot say at all
        var (engine, store, grant) = await StartAsync(unknown, plain);
        store.Update(s => s.NeverStartModels = true);

        Assert.True(Ok(await engine.HandleAsync(Call(grant))));

        Assert.Equal(1, unknown.Calls);
    }

    [Fact]
    public void TheSwitchIsOffByDefaultAndStopsTheWarmUp()
    {
        Assert.False(new WalletSettings().NeverStartModels);
        Assert.True(new WalletSettings().WarmUpOnConnect);
    }
}
