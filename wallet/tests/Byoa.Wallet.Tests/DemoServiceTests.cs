using System.Text.Json.Nodes;
using Byoa.Wallet.Activity;
using Byoa.Wallet.Providers;
using Byoa.Wallet.Protocol;
using Byoa.Wallet.Registry;
using Byoa.Wallet.Secrets;
using Byoa.Wallet.Settings;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Tests;

/// <summary>The free demo services: they answer without a key, cost nothing, and are cloud to the gate even so.</summary>
public class DemoServiceTests
{
    const int Port = 5951;

    static HttpClient Plain() => new(new SocketsHttpHandler { AllowAutoRedirect = false });

    static TextRequest ShortRequest { get; } = new([new ChatMessage("user", "Hello!")], 16, 0.7, null, null);

    static CloudChatSource DemoSource(FakeHttp server, string service = "llm7", string? key = null) =>
        new(service, "default", "Free demo", server.Url.TrimEnd('/'), Plain(), () => key, () => key is not null);

    [Fact]
    public void TheDemoServicesAreKnownPresetsAndTheKeyedOnesAreNot()
    {
        Assert.True(ApiServices.IsDemo("llm7"));
        Assert.True(ApiServices.IsDemo("ovh"));
        Assert.False(ApiServices.IsDemo("openai"));
        Assert.False(ApiServices.IsDemo(ApiServices.Custom));
        Assert.False(ApiServices.IsDemo(null));
        Assert.False(ApiServices.IsDemo("nobody"));
        Assert.Equal("https://api.llm7.io/v1", ApiServices.Find("llm7")!.BaseUrl);
        Assert.Equal("api-key:llm7", ApiServices.SecretName("llm7", null));
    }

    [Fact]
    public async Task ADemoSourceAnswersWithNoKeyAndSendsNone()
    {
        using var server = new FakeHttp(Port);
        server.Respond(200, """{"choices":[{"message":{"role":"assistant","content":"OK"}}],"usage":{"prompt_tokens":1,"completion_tokens":1}}""");
        var source = DemoSource(server);

        Assert.False(source.Paid);
        Assert.Equal(0m, source.EstimateCost(ShortRequest));
        Assert.Equal(SourceState.Ready, source.State);
        Assert.Contains("free public service", source.StateText, StringComparison.Ordinal);

        var answer = await source.GenerateAsync(ShortRequest, default);

        Assert.Equal("OK", answer.Text);
        Assert.Equal(0m, answer.Cost);
        var call = Assert.Single(server.Requests);
        Assert.Equal("POST", call.Method);
        Assert.Equal("/chat/completions", call.Path);
        Assert.False(call.Headers.ContainsKey("Authorization"));          // a demo service is asked without any key
    }

    [Fact]
    public async Task AKeyedServiceWithoutAKeyIsStillRefusedBeforeAnyRequest()
    {
        var source = new CloudChatSource("openai", "gpt-5-mini", null, "https://api.openai.com/v1", Plain(), () => null, () => false);

        Assert.Equal(SourceState.NoKey, source.State);
        Assert.True(source.Paid);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => source.GenerateAsync(ShortRequest, default));
        Assert.Contains("no API key", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRegistryTakesADemoDraftAndShowsItAsCloudButFree()
    {
        var dir = TestFolder.New();
        var registry = new WalletRegistry(new SettingsStore(null), new SecretStore(Path.Combine(dir, "secrets.dat")),
            new ActivityLog(null, DateTimeOffset.UtcNow));

        var added = registry.AddSource(new SourceDraft(ApiServices.Kind, "default", null, [Vocabulary.Generate], null,
            "Free demo (LLM7.io)", "llm7"));

        Assert.True(added.Ok, added.Message);
        var info = registry.ListSources().Single(s => s.Id == added.Id);
        Assert.Equal("api", info.Kind);
        Assert.False(info.Paid);                                          // nothing it answers costs money
        Assert.Equal("api-key", info.ProviderKind);                       // and the gate still sees cloud
        Assert.Equal("ready", info.State);
        Assert.False(info.HasApiKey);
        Assert.Contains("Free demo model (public service)", info.StateText, StringComparison.Ordinal);
    }

    [Fact]
    public void ADemoModelJoinsTheTextRouteBeforeThePaidSources()
    {
        var dir = TestFolder.New();
        var registry = new WalletRegistry(new SettingsStore(null), new SecretStore(Path.Combine(dir, "secrets.dat")),
            new ActivityLog(null, DateTimeOffset.UtcNow));
        var paid = registry.AddSource(new SourceDraft(SourceKinds.OpenRouter, "google/gemma-3-12b-it", "https://openrouter.ai/api/v1", [Vocabulary.Generate]));
        Assert.True(paid.Ok, paid.Message);

        var demo = registry.AddSource(new SourceDraft(ApiServices.Kind, "default", null, [Vocabulary.Generate], null, "Free demo", "ovh"));
        Assert.True(demo.Ok, demo.Message);

        var route = Assert.Single(registry.ListChains(Vocabulary.Generate)).OrderedSourceIds;
        Assert.Equal([demo.Id!, paid.Id!], route);                        // free answers before the paid ones
        Assert.Equal([paid.Id], registry.ListSources().Where(s => s.Paid).Select(s => s.Id));
    }

    [Fact]
    public void AnUnknownServiceIsStillRefused()
    {
        var dir = TestFolder.New();
        var registry = new WalletRegistry(new SettingsStore(null), new SecretStore(Path.Combine(dir, "secrets.dat")),
            new ActivityLog(null, DateTimeOffset.UtcNow));

        var refused = registry.AddSource(new SourceDraft(ApiServices.Kind, "x", null, [Vocabulary.Generate], null, null, "nosuch"));

        Assert.False(refused.Ok);
        Assert.Contains("Unknown service", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLiveProviderForADemoPresetNeedsNoKeyEither()
    {
        var home = TestFolder.New();
        var oldHome = Environment.GetEnvironmentVariable(WalletPaths.HomeVariable);
        Environment.SetEnvironmentVariable(WalletPaths.HomeVariable, home);
        try
        {
            using var sources = new SourceSet(new SettingsStore(null), new SecretStore(Path.Combine(home, "secrets.dat")), new HttpClient());

            // The same construction the live list uses for a saved source, without any secret in the store.
            var chat = Assert.IsType<CloudChatSource>(sources.CreateTextSource(new SourceSetting
            {
                Kind = ApiServices.Kind,
                Model = "default",
                Service = "llm7",
            }));

            Assert.False(chat.Paid);
            Assert.Equal("api-key", chat.ProviderKind);
            Assert.Equal(SourceState.Ready, chat.State);
        }
        finally
        {
            Environment.SetEnvironmentVariable(WalletPaths.HomeVariable, oldHome);
        }
    }
}
