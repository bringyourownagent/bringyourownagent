using System.Text.Json.Nodes;
using Byoa.Wallet.Activity;
using Byoa.Wallet.Audio;
using Byoa.Wallet.Protocol;
using Byoa.Wallet.Providers;
using Byoa.Wallet.Registry;
using Byoa.Wallet.Secrets;
using Byoa.Wallet.Settings;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Tests;

/// <summary>
/// A speech model in a program on this computer (the Llama app, LM Studio, ...): the wallet sends the clip as
/// <c>input_audio</c> in a chat request and reads the text back. It holds no model of its own.
/// </summary>
public class OpenAiCompatibleSpeechSourceTests
{
    const int Port = 5341;
    const string Model = "Renesas/Qwen3-ASR-1.7B-GGUF:FP16";

    static HttpClient NoRedirects() => new(new SocketsHttpHandler { AllowAutoRedirect = false });

    static OpenAiCompatibleSpeechSource Source(FakeHttp server, IReadOnlyList<string>? languages = null) =>
        new(SourceKinds.OpenAiCompatible, Model, null, server.Url + "v1", languages, NoRedirects());

    static float[] Clip(double seconds)
    {
        var samples = new float[(int)(seconds * Wav.TargetRate)];
        for (var i = 0; i < samples.Length; i++) samples[i] = 0.1f * MathF.Sin(i * 0.05f);
        return samples;
    }

    [Fact]
    public async Task TheClipGoesAsInputAudioAndTheAnswerIsTheText()
    {
        using var server = new FakeHttp(Port);
        server.Respond(200, """{"choices":[{"message":{"role":"assistant","content":"language Russian<asr_text>\u0424\u0440\u0430\u043d\u0446\u0438\u044f"},"finish_reason":"stop"}]}""");
        var source = Source(server);

        var transcript = await source.TranscribeAsync(Clip(1.5), "ru", default);

        Assert.Equal("\u0424\u0440\u0430\u043d\u0446\u0438\u044f", transcript.Text);                  // the model's tag is not part of what was said
        Assert.Equal(0, transcript.Cost);
        Assert.StartsWith("openai-compatible", source.Id);
        var call = Assert.Single(server.Requests);
        Assert.Equal("/v1/chat/completions", call.Path);
        var body = JsonNode.Parse(call.Body)!.AsObject();
        Assert.Equal(Model, body["model"]!.GetValue<string>());
        Assert.Equal(0, body["temperature"]!.GetValue<double>());
        Assert.False(body["stream"]!.GetValue<bool>());
        Assert.False(body["chat_template_kwargs"]!["enable_thinking"]!.GetValue<bool>());
        var parts = body["messages"]![0]!["content"]!.AsArray();
        Assert.Equal("input_audio", parts[0]!["type"]!.GetValue<string>());
        Assert.Equal("wav", parts[0]!["input_audio"]!["format"]!.GetValue<string>());
        var wav = Convert.FromBase64String(parts[0]!["input_audio"]!["data"]!.GetValue<string>());
        var clip = Wav.Parse(wav);                                 // what goes out is a real 16 kHz mono WAV of the same clip
        Assert.Equal(Wav.TargetRate, clip.SampleRate);
        Assert.Equal(1.5, clip.Seconds, 2);
        var instruction = parts[1]!["text"]!.GetValue<string>();
        Assert.Contains("Russian (write it in Cyrillic)", instruction);
        Assert.Contains("Do not translate", instruction);
    }

    [Fact]
    public async Task WithoutALanguageTheModelIsToldToHearTheSpeakersLanguage()
    {
        using var server = new FakeHttp(Port);
        server.Respond(200, """{"choices":[{"message":{"content":"Japan"}}]}""");

        await Source(server).TranscribeAsync(Clip(1), null, default);

        var text = JsonNode.Parse(Assert.Single(server.Requests).Body)!["messages"]![0]!["content"]![1]!["text"]!.GetValue<string>();
        Assert.Contains("in the language of the speaker", text);
    }

    [Theory]
    [InlineData("language Russian<asr_text>\u042f\u043f\u043e\u043d\u0438\u044f", "\u042f\u043f\u043e\u043d\u0438\u044f")]
    [InlineData("language None<asr_text>", "")]
    [InlineData("  Brazil  ", "Brazil")]
    [InlineData("<think>hmm</think>New Zealand", "New Zealand")]
    [InlineData("Okay, so Portugal", "Okay, so Portugal")]            // what was said is never cut as if it were reasoning
    public void TheAnswerIsCleanedOfTheModelsOwnMarkup(string raw, string expected) =>
        Assert.Equal(expected, OpenAiCompatibleSpeechSource.Clean(raw));

    [Fact]
    public async Task ARuntimeThatCannotHearAudioExplainsItself()
    {
        using var server = new FakeHttp(Port);
        server.Respond(400, """{"error":{"code":400,"message":"image input is not supported - hint: provide the mmproj","type":"invalid_request_error"}}""");
        var source = Source(server);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => source.TranscribeAsync(Clip(0.5), "ru", default));

        Assert.Contains("HTTP 400", error.Message);
        Assert.Contains("image input is not supported", error.Message);
        Assert.Contains("last call failed", source.StateText);
    }

    [Fact]
    public async Task ASuccessClearsTheLastErrorAndMeasuresTheSpeed()
    {
        using var server = new FakeHttp(Port);
        server.Respond(500, "{}");
        server.Respond(200, """{"choices":[{"message":{"content":"Czech Republic"}}]}""");
        var source = Source(server);
        Assert.False(source.HasMeasuredSeconds);

        await Assert.ThrowsAsync<HttpRequestException>(() => source.TranscribeAsync(Clip(1), "en", default));
        var ok = await source.TranscribeAsync(Clip(1), "en", default);

        Assert.Equal("Czech Republic", ok.Text);
        Assert.True(source.HasMeasuredSeconds);
        Assert.DoesNotContain("failed", source.StateText);
    }

    [Fact]
    public async Task TheAnswerRoomGrowsWithTheClipButStaysBounded()
    {
        using var server = new FakeHttp(Port);
        server.Handler = _ => (200, """{"choices":[{"message":{"content":"x"}}]}""");
        var source = Source(server);

        await source.TranscribeAsync(Clip(0.5), "en", default);
        await source.TranscribeAsync(Clip(20), "en", default);
        await source.TranscribeAsync(Clip(600), "en", default);

        var tokens = server.Requests.Select(r => JsonNode.Parse(r.Body)!["max_tokens"]!.GetValue<int>()).ToArray();
        Assert.True(tokens[0] < tokens[1] && tokens[1] < tokens[2], string.Join(",", tokens));
        Assert.Equal(2048, tokens[2]);
    }

    [Fact]
    public async Task OneClipAtATimeBecauseALocalRuntimeAnswersOne()
    {
        using var server = new FakeHttp(Port);
        var running = 0;
        var most = 0;
        server.Handler = _ =>
        {
            var now = Interlocked.Increment(ref running);
            lock (server) most = Math.Max(most, now);
            Thread.Sleep(80);
            Interlocked.Decrement(ref running);
            return (200, """{"choices":[{"message":{"content":"ok"}}]}""");
        };
        var source = Source(server);

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => source.TranscribeAsync(Clip(0.5), "en", default)));

        Assert.Equal(1, most);
    }

    [Fact]
    public async Task WarmingSendsOneShortSilentClipAndKeepsNothing()
    {
        using var server = new FakeHttp(Port);
        server.Respond(200, """{"choices":[{"message":{"content":"language None<asr_text>"}}]}""");
        var source = Source(server);

        await source.WarmAsync("ru");

        var call = Assert.Single(server.Requests);
        var clip = Wav.Parse(Convert.FromBase64String(JsonNode.Parse(call.Body)!["messages"]![0]!["content"]![0]!["input_audio"]!["data"]!.GetValue<string>()));
        Assert.True(clip.Seconds <= 1);
        Assert.All(clip.Samples, s => Assert.Equal(0, s));
        Assert.False(source.HasMeasuredSeconds);                   // a warm-up is not a measured phrase
    }

    [Fact]
    public void TheIdKeepsItApartFromATextSourceOfTheSameModel()
    {
        using var http = new HttpClient();
        var speech = new OpenAiCompatibleSpeechSource(SourceKinds.LmStudio, "qwen", null, "http://127.0.0.1:1234/v1", null, http);
        var text = new OpenAiCompatibleSource(SourceKinds.LmStudio, "qwen", null, "http://127.0.0.1:1234/v1", http);

        Assert.NotEqual(text.Id, speech.Id);
        Assert.Equal(text.Id + SourceIds.SpeechSuffix, speech.Id);
        Assert.True(SourceIds.IsSpeech(speech.Id));
        Assert.False(SourceIds.IsSpeech(text.Id));
    }

    [Fact]
    public void ItIsDescribedByItsPropertiesLikeEverySource()
    {
        using var http = new HttpClient();
        var here = new OpenAiCompatibleSpeechSource(SourceKinds.OpenAiCompatible, "qwen3-asr", "Qwen ASR", "http://127.0.0.1:9931/v1", ["ru", "en"], http);
        var home = new OpenAiCompatibleSpeechSource(SourceKinds.OpenAiCompatible, "qwen3-asr", null, "http://192.168.1.20:9931/v1", null, http);

        Assert.Equal("local-model", here.ProviderKind);
        Assert.Equal(Runs.ThisDevice, here.Runs);
        Assert.Equal(["ru", "en"], here.Languages);
        Assert.Equal(2, here.LanguageCount);
        Assert.False(here.Paid);
        Assert.Equal(0, here.EstimateCost(10));
        Assert.Contains("Llama", here.StateText, StringComparison.Ordinal);                  // "ready when Llama runs"
        Assert.Equal("home-server", home.ProviderKind);
        Assert.Null(home.Languages);
        Assert.Equal(0, home.LanguageCount);

        here.Up = false;
        Assert.Contains("is not running", here.StateText);
        here.Up = true;
        here.Listed = false;
        Assert.Equal(SourceState.Missing, here.State);
        here.MarkUnavailable("not allowed");
        Assert.Equal(SourceState.Unavailable, here.State);
    }
}

/// <summary>The provider set builds speech sources from the settings and carries the wallet from earlier versions along.</summary>
public sealed class SpeechSourceSetTests
{
    static string Home(out string? old)
    {
        var home = TestFolder.New();
        old = Environment.GetEnvironmentVariable(WalletPaths.HomeVariable);
        Environment.SetEnvironmentVariable(WalletPaths.HomeVariable, home);
        return home;
    }

    [Fact]
    public void ASpeechModelInTheSettingsBecomesASpeechSourceBeforeThePaidOne()
    {
        var home = Home(out var old);
        try
        {
            var id = OpenAiCompatibleSource.MakeId(SourceKinds.OpenAiCompatible, "qwen3-asr", "http://127.0.0.1:9931/v1", Vocabulary.Transcribe);
            var store = new SettingsStore(null, settings => settings.Sources.Add(new SourceSetting
            {
                Id = id, Kind = SourceKinds.OpenAiCompatible, Model = "qwen3-asr", Endpoint = "http://127.0.0.1:9931/v1",
                Skills = [Vocabulary.Transcribe], Languages = ["ru"],
            }));
            using var sources = new SourceSet(store, new SecretStore(Path.Combine(home, "secrets.dat")), new HttpClient());

            var all = sources.All;
            Assert.Same(all, sources.All);                                        // one list for the engine's whole life
            Assert.Equal([id, OpenRouterSource.SourceId], all.Select(s => s.Id));
            Assert.Equal(["ru"], all[0].Languages);
            Assert.Empty(sources.Text);                                           // a speech source is not offered for text
            Assert.Equal([id, OpenRouterSource.SourceId], store.Snapshot().Chain(Vocabulary.Transcribe).Stops.Select(s => s.SourceId));
            Assert.Same(all[0], sources.FindSpeech(id));

            // the list follows the settings: remove the source, and it is gone from the same list
            store.Update(s => s.Sources.RemoveAll(x => x.Id == id));
            Assert.Equal([OpenRouterSource.SourceId], all.Select(s => s.Id));
        }
        finally
        {
            Environment.SetEnvironmentVariable(WalletPaths.HomeVariable, old);
        }
    }

    [Fact]
    public void ASpeechIdWithoutTheSuffixIsMadeCanonicalAndItsRouteStopFollows()
    {
        var home = Home(out var old);
        try
        {
            var store = new SettingsStore(null, settings =>
            {
                settings.Sources.Add(new SourceSetting
                {
                    Id = "old-id", Kind = SourceKinds.LmStudio, Model = "asr", Skills = [Vocabulary.Transcribe],
                });
                settings.Chain(Vocabulary.Transcribe).Stops.Add(new ChainStop { SourceId = "old-id" });
            });
            using var sources = new SourceSet(store, new SecretStore(Path.Combine(home, "secrets.dat")), new HttpClient());

            var canonical = OpenAiCompatibleSource.MakeId(SourceKinds.LmStudio, "asr", null, Vocabulary.Transcribe);
            Assert.EndsWith(SourceIds.SpeechSuffix, canonical);
            Assert.Contains(sources.All, s => s.Id == canonical);
            var stops = store.Snapshot().Chain(Vocabulary.Transcribe).Stops.Select(s => s.SourceId).ToArray();
            Assert.Equal(1, stops.Count(s => s == canonical));
            Assert.DoesNotContain("old-id", stops);
        }
        finally
        {
            Environment.SetEnvironmentVariable(WalletPaths.HomeVariable, old);
        }
    }

    [Fact]
    public void ASettingsFileOfAnEarlierVersionLosesItsBuiltInEngines()
    {
        var home = Home(out var old);
        try
        {
            // what the version with GigaAM and Whisper inside left behind: the engines' switches and a route through them
            var json = """
                {"version":1,"sites":[],"sources":[{"id":"gigaam-v3","enabled":true},{"id":"whisper","enabled":false},{"id":"whisper-cpu","enabled":true},{"id":"openrouter-scribe","enabled":true}],
                 "chains":[{"skill":"audio.transcribe","stops":[{"sourceId":"gigaam-v3","enabled":true,"askBeforePaid":true},{"sourceId":"whisper","enabled":true,"askBeforePaid":true},
                 {"sourceId":"whisper-cpu","enabled":true,"askBeforePaid":true},{"sourceId":"openrouter-scribe","enabled":true,"askBeforePaid":true}]}],
                 "modelFolders":["C:\\old"],"whisperModel":"ggml-large-v3-q5_0.bin","downloadedModelFiles":["x.bin"]}
                """;
            File.WriteAllText(Path.Combine(home, "settings.json"), json);
            var store = new SettingsStore(Path.Combine(home, "settings.json"));
            using var sources = new SourceSet(store, new SecretStore(Path.Combine(home, "secrets.dat")), new HttpClient());

            var settings = store.Snapshot();
            Assert.Equal([OpenRouterSource.SourceId], settings.Chain(Vocabulary.Transcribe).Stops.Select(s => s.SourceId));
            Assert.DoesNotContain(settings.Sources, s => Router.RetiredSpeechIds.Contains(s.Id));
            Assert.Equal([OpenRouterSource.SourceId], sources.All.Select(s => s.Id));
        }
        finally
        {
            Environment.SetEnvironmentVariable(WalletPaths.HomeVariable, old);
        }
    }

    [Fact]
    public async Task TheScanMarksASpeechSourceUpOrDownAndListedOrNot()
    {
        var home = Home(out var old);
        const int port = 5342;
        try
        {
            using var runtime = new FakeHttp(port);
            runtime.Handler = call => call.Path.EndsWith("/models", StringComparison.Ordinal)
                ? (200, """{"data":[{"id":"qwen3-asr"}]}""")
                : (404, "{}");
            var endpoint = runtime.Url + "v1";
            var store = new SettingsStore(null, settings => settings.Sources.AddRange(
            [
                new SourceSetting { Id = OpenAiCompatibleSource.MakeId(SourceKinds.OpenAiCompatible, "qwen3-asr", endpoint, Vocabulary.Transcribe),
                    Kind = SourceKinds.OpenAiCompatible, Model = "qwen3-asr", Endpoint = endpoint, Skills = [Vocabulary.Transcribe] },
                new SourceSetting { Id = OpenAiCompatibleSource.MakeId(SourceKinds.OpenAiCompatible, "other-model", endpoint, Vocabulary.Transcribe),
                    Kind = SourceKinds.OpenAiCompatible, Model = "other-model", Endpoint = endpoint, Skills = [Vocabulary.Transcribe] },
            ]));
            using var sources = new SourceSet(store, new SecretStore(Path.Combine(home, "secrets.dat")), new HttpClient());

            await sources.RefreshAsync(default);

            var found = sources.All.OfType<OpenAiCompatibleSpeechSource>().Single(s => s.Model == "qwen3-asr");
            var missing = sources.All.OfType<OpenAiCompatibleSpeechSource>().Single(s => s.Model == "other-model");
            Assert.True(found.Up);
            Assert.Equal(SourceState.Ready, found.State);
            Assert.True(missing.Up);
            Assert.Equal(SourceState.Missing, missing.State);        // the program is up but does not offer that model
        }
        finally
        {
            Environment.SetEnvironmentVariable(WalletPaths.HomeVariable, old);
        }
    }
}

/// <summary>Adding, testing and ordering speech models through the registry (the window and the agent's tools use it).</summary>
public sealed class SpeechRegistryTests
{
    static (WalletRegistry Registry, SettingsStore Store, ActivityLog Log, FakeSource Speech) Registry(Func<SourceSetting, ISpeechSource?>? speechFactory = null)
    {
        var dir = TestFolder.New();
        var store = new SettingsStore(null);
        var log = new ActivityLog(null, DateTimeOffset.UtcNow);
        var paid = new FakeSource("openrouter-scribe", Runs.ApiKey, pricePerSecond: 0.00004m);
        var engine = SpeechRoutes.Engine(store, [paid], log, new ScriptedUi());
        var registry = new WalletRegistry(store, new SecretStore(Path.Combine(dir, "secrets.dat")), log, [paid], [],
            engine: engine, speechFactory: speechFactory);
        return (registry, store, log, paid);
    }

    static SourceDraft Speech(string kind = SourceKinds.OpenAiCompatible, string model = "qwen3-asr", string? endpoint = "http://127.0.0.1:9931/v1",
        IReadOnlyList<string>? languages = null) =>
        new(kind, model, endpoint, [Vocabulary.Transcribe], languages);

    [Fact]
    public void ASpeechModelGetsASuffixedIdAndAStopBeforeThePaidFallback()
    {
        var (registry, store, _, _) = Registry();

        var added = registry.AddSource(Speech(languages: ["ru", "en"]));

        Assert.True(added.Ok, added.Message);
        Assert.EndsWith(SourceIds.SpeechSuffix, added.Id);
        Assert.Equal([added.Id!, "openrouter-scribe"], store.Snapshot().Chain(Vocabulary.Transcribe).Stops.Select(s => s.SourceId));
        Assert.Empty(store.Snapshot().Chain(Vocabulary.Generate).Stops);              // nothing was put into the text route
        var listed = registry.ListSources(Vocabulary.Transcribe).Single(s => s.Id == added.Id);
        Assert.Equal(["audio.transcribe"], listed.Skills);
        Assert.Equal(["ru", "en"], listed.Languages);
        Assert.Equal("qwen3-asr", listed.Model);
    }

    [Fact]
    public void TheSameModelCanServeTextAndSpeechAsTwoSources()
    {
        var (registry, store, _, _) = Registry();

        var speech = registry.AddSource(Speech(SourceKinds.LmStudio, "qwen3-omni", null));
        var text = registry.AddSource(new SourceDraft(SourceKinds.LmStudio, "qwen3-omni", null, [Vocabulary.Generate]));

        Assert.True(speech.Ok, speech.Message);
        Assert.True(text.Ok, text.Message);
        Assert.NotEqual(speech.Id, text.Id);
        Assert.Contains(text.Id!, store.Snapshot().Chain(Vocabulary.Generate).Stops.Select(s => s.SourceId));
        Assert.DoesNotContain(text.Id!, store.Snapshot().Chain(Vocabulary.Transcribe).Stops.Select(s => s.SourceId));
    }

    [Fact]
    public void OneSourceServesOneSkillAndAPaidApiIsNotASpeechModel()
    {
        var (registry, _, _, _) = Registry();

        var both = registry.AddSource(new SourceDraft(SourceKinds.LmStudio, "qwen", null, [Vocabulary.Generate, Vocabulary.Transcribe]));
        var paid = registry.AddSource(Speech(SourceKinds.OpenRouter, "elevenlabs/scribe-v2", null));

        Assert.False(both.Ok);
        Assert.Contains("one source per skill", both.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(paid.Ok);
        Assert.Contains("Scribe v2", paid.Message);
    }

    [Fact]
    public async Task TestingADraftSendsTheShortSampleThroughTheSpeechSourceViaTheEngine()
    {
        var fake = new FakeSource("draft-asr") { Answer = "tone" };
        var (registry, _, _, _) = Registry(_ => fake);

        var result = await registry.TestDraftAsync(Speech(), visitorInitiated: true);

        Assert.True(result.Ok, result.Message);
        Assert.Equal(1, fake.Calls);
    }

    [Fact]
    public async Task ADraftThatTheProviderSetCannotBuildFailsInPlainWords()
    {
        var (registry, _, _, _) = Registry(_ => null);

        var result = await registry.TestDraftAsync(Speech(), visitorInitiated: true);

        Assert.False(result.Ok);
        Assert.Contains("could not create that speech source", result.Message);
    }

    [Fact]
    public async Task ARuntimeThatRefusesAudioFailsTheTestWithItsOwnReason()
    {
        using var server = new FakeHttp(5343);
        server.Respond(400, """{"error":{"message":"audio input is not supported by this model"}}""");
        using var http = new HttpClient();
        var (registry, _, _, _) = Registry(setting => new OpenAiCompatibleSpeechSource(setting.Kind!, setting.Model!, null, setting.Endpoint, null, http));

        var result = await registry.TestDraftAsync(Speech(endpoint: server.Url + "v1"), visitorInitiated: true);

        Assert.False(result.Ok);
        Assert.Contains("audio input is not supported", result.Message);
    }

    [Fact]
    public void ChangingASourceFromTextToSpeechMovesItToTheOtherRoute()
    {
        var (registry, store, _, _) = Registry();
        var added = registry.AddSource(new SourceDraft(SourceKinds.LmStudio, "qwen3-omni", null, [Vocabulary.Generate]));
        Assert.Contains(added.Id!, store.Snapshot().Chain(Vocabulary.Generate).Stops.Select(s => s.SourceId));

        var changed = registry.UpdateSource(added.Id!, new SourceUpdate(null, null, null, [Vocabulary.Transcribe], null, null));

        Assert.True(changed.Ok, changed.Message);
        Assert.EndsWith(SourceIds.SpeechSuffix, changed.Id);
        Assert.DoesNotContain(store.Snapshot().Chain(Vocabulary.Generate).Stops, s => s.SourceId == changed.Id || s.SourceId == added.Id);
        Assert.Equal([changed.Id!, "openrouter-scribe"], store.Snapshot().Chain(Vocabulary.Transcribe).Stops.Select(s => s.SourceId));
    }

    [Fact]
    public void ASpeechSourceIsRemovedFromSettingsAndRoute()
    {
        var (registry, store, _, _) = Registry();
        var added = registry.AddSource(Speech());

        var removed = registry.RemoveSource(added.Id!);

        Assert.True(removed.Ok, removed.Message);
        Assert.Equal(["openrouter-scribe"], store.Snapshot().Chain(Vocabulary.Transcribe).Stops.Select(s => s.SourceId));
    }
}

/// <summary>The whole way a site's clip takes: engine, route, speech source, a fake Llama on a loopback port.</summary>
public sealed class SpeechCallThroughARuntimeTests
{
    const string Site = "https://geo.example.org";

    [Fact]
    public async Task APagesClipIsTranscribedByTheModelInTheRuntimeAndTheSiteLearnsOnlyTheText()
    {
        using var server = new FakeHttp(5344);
        server.Handler = _ => (200, """{"choices":[{"message":{"content":"language Russian<asr_text>\u0424\u0440\u0430\u043d\u0446\u0438\u044f"}}]}""");   // the warm-up asks too
        using var http = new HttpClient();
        var source = new OpenAiCompatibleSpeechSource(SourceKinds.OpenAiCompatible, "qwen3-asr", null, server.Url + "v1", null, http);
        var time = new ManualTime();
        var ui = new ScriptedUi();
        var log = new ActivityLog(null, time.Now);
        var paid = new FakeSource("openrouter-scribe", Runs.ApiKey, pricePerSecond: 0.00004m);
        var engine = SpeechRoutes.Engine(new SettingsStore(null), [source, paid], log, ui, time);

        var grant = Assert.IsType<JsonObject>(await engine.HandleAsync(Clips.Request(Site, click: true)))["grantId"]!.GetValue<string>();
        var result = await engine.HandleAsync(Clips.Call(Site, grant, language: "ru"));

        Assert.True(result!["ok"]!.GetValue<bool>());
        Assert.Equal("\u0424\u0440\u0430\u043d\u0446\u0438\u044f", result["output"]!["text"]!.GetValue<string>());
        Assert.Equal("0.000 USD", result["receipt"]!["cost"]!.GetValue<string>());
        Assert.Equal("local", result["receipt"]!["provider"]!.GetValue<string>());
        Assert.Equal(0, paid.Calls);                                              // the paid fallback was never asked
        var entry = log.Recent()[0];
        Assert.Contains("qwen3-asr", entry.Source);
        Assert.DoesNotContain("\u0424\u0440\u0430\u043d\u0446\u0438\u044f", entry.Source ?? "");                     // the log never holds what was said
        Assert.Contains(server.Requests, r => r.Path == "/v1/chat/completions");
    }

    [Fact]
    public async Task WhenTheRuntimeIsDownTheCallFallsBackToTheNextSource()
    {
        using var http = new HttpClient();
        var source = new OpenAiCompatibleSpeechSource(SourceKinds.OpenAiCompatible, "qwen3-asr", null, "http://127.0.0.1:1/v1", null, http);
        var time = new ManualTime();
        var log = new ActivityLog(null, time.Now);
        var other = new FakeSource("local-any") { Answer = "francia-other" };
        var engine = SpeechRoutes.Engine(new SettingsStore(null), [source, other], log, new ScriptedUi(), time);

        var grant = Assert.IsType<JsonObject>(await engine.HandleAsync(Clips.Request(Site, click: true)))["grantId"]!.GetValue<string>();
        var result = await engine.HandleAsync(Clips.Call(Site, grant, language: "ru"));

        Assert.Equal("francia-other", result!["output"]!["text"]!.GetValue<string>());
        Assert.StartsWith(source.Name, log.Recent()[0].Fallback ?? "");
    }
}
