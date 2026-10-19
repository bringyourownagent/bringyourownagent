using System.Text;
using System.Text.Json.Nodes;
using Byoa.Wallet.Activity;
using Byoa.Wallet.Direct;
using Byoa.Wallet.Engine;
using Byoa.Wallet.Protocol;
using Byoa.Wallet.Providers;
using Byoa.Wallet.Settings;
using Byoa.Wallet.Sources;
using System.Buffers.Text;
using System.Net.WebSockets;

namespace Byoa.Wallet.Tests;

/// <summary>An answer that arrives in pieces: the reasoning is taken out even when a tag is cut in two.</summary>
public class ThinkFilterTests
{
    static string Run(params string[] pieces)
    {
        var filter = new ThinkFilter();
        var shown = new StringBuilder();
        foreach (var piece in pieces) shown.Append(filter.Push(piece));
        shown.Append(filter.Flush());
        return shown.ToString();
    }

    [Fact]
    public void ReasoningInOnePieceIsTakenOut() => Assert.Equal("Alice by 60 to 40.", Run("<think>she is right?</think>\n\nAlice by 60 to 40."));

    [Fact]
    public void ATagCutInTwoIsStillTaken() =>
        Assert.Equal("Answer.", Run("<thi", "nk>hidden", " thoughts</th", "ink>", "\n", "Answer."));

    [Fact]
    public void ReasoningThatNeverClosesIsAllHidden() => Assert.Equal("Before ", Run("Before <think>and then it never ends", " and on"));

    [Fact]
    public void ALessThanSignThatIsNoTagIsText() => Assert.Equal("a < b and <b>bold</b>", Run("a <", " b and <b>bold</b>"));

    [Fact]
    public void BothKindsOfBlockAndTheirCaseAreKnown() =>
        Assert.Equal("done", Run("<REASONING>x</reasoning><Think>y</THINK>done"));

    [Fact]
    public void AnAnswerThatBeginsWithBlankLinesBeginsAtItsFirstWord() => Assert.Equal("hello world", Run("\n\n  ", "hello", " world"));
}

/// <summary>The sources hand their answers over as they are written, and the cost still arrives at the end.</summary>
public class StreamedSourceTests
{
    const int Port = 5371;

    static HttpClient Plain() => new(new SocketsHttpHandler { AllowAutoRedirect = false });

    static string Sse(params string[] events) => string.Concat(events.Select(e => "data: " + e + "\n\n")) + "data: [DONE]\n\n";

    static TextRequest Ask() => new([new ChatMessage("user", "Who is right?")], 800, 0.7, null, null);

    [Fact]
    public async Task ALocalModelStreamsItsPiecesAndTheHiddenReasoningStaysHidden()
    {
        using var server = new FakeHttp(Port);
        server.Respond(200, Sse(
            """{"choices":[{"delta":{"reasoning_content":"hm, who?"}}]}""",
            """{"choices":[{"delta":{"content":"<think>x</think>Al"}}]}""",
            """{"choices":[{"delta":{"content":"ice, "}}]}""",
            """{"choices":[{"delta":{"content":"by 60."},"finish_reason":"stop"}],"usage":{"prompt_tokens":12,"completion_tokens":5}}"""));
        var source = new OpenAiCompatibleSource(SourceKinds.OpenAiCompatible, "m", null, server.Url.TrimEnd('/'), Plain());
        var pieces = new List<string>();
        var answer = await source.GenerateStreamAsync(Ask(), pieces.Add, default);
        Assert.Equal("Alice, by 60.", answer.Text);
        Assert.Equal("Alice, by 60.", string.Concat(pieces));
        Assert.True(pieces.Count >= 3);
        Assert.Equal(0m, answer.Cost);
        Assert.Equal((12, 5), (answer.InputTokens, answer.OutputTokens));
        var body = JsonNode.Parse(Assert.Single(server.Requests).Body)!;
        Assert.True(body["stream"]!.GetValue<bool>());
    }

    [Fact]
    public async Task AConstrainedAnswerIsAWordAndGoesWhole()
    {
        using var server = new FakeHttp(Port);
        server.Respond(200, """{"choices":[{"message":{"content":"{\"answer\":\"yes\"}"}}]}""");
        var source = new OpenAiCompatibleSource(SourceKinds.OpenAiCompatible, "m", null, server.Url.TrimEnd('/'), Plain());
        var pieces = new List<string>();
        var answer = await source.GenerateStreamAsync(new TextRequest([new ChatMessage("user", "Is it?")], 16, 0, null, ["yes", "no"]), pieces.Add, default);
        Assert.Equal("yes", answer.Text);
        Assert.Equal(["yes"], pieces);
    }

    [Fact]
    public async Task AModelThatSpentEverythingThinkingGetsTheSameSecondTryAsWithoutAStream()
    {
        using var server = new FakeHttp(Port);
        server.Respond(200, Sse("""{"choices":[{"delta":{"reasoning_content":"thinking and thinking"},"finish_reason":"length"}]}"""));
        server.Respond(200, """{"choices":[{"message":{"content":"Alice"},"finish_reason":"stop"}],"usage":{}}""");
        var source = new OpenAiCompatibleSource(SourceKinds.OpenAiCompatible, "m", null, server.Url.TrimEnd('/'), Plain());
        var pieces = new List<string>();
        var answer = await source.GenerateStreamAsync(Ask(), pieces.Add, default);
        Assert.Equal("Alice", answer.Text);
        Assert.Equal(["Alice"], pieces);
        Assert.Equal(2, server.Requests.Count);
    }

    [Fact]
    public async Task OpenRouterStreamsAndTheCostComesFromTheUsageAtTheEnd()
    {
        using var server = new FakeHttp(Port);
        server.Respond(200, ": OPENROUTER PROCESSING\n\n" + Sse(
            """{"choices":[{"delta":{"content":"Alice"}}]}""",
            """{"choices":[{"delta":{"content":" wins"},"finish_reason":"stop"}]}""",
            """{"choices":[],"usage":{"prompt_tokens":20,"completion_tokens":3,"cost":0.00042}}"""));
        var source = new OpenRouterTextSource("x-ai/grok-4.1-fast", null, null, Plain(), () => "sk-test", () => true, new Uri(server.Url));
        var pieces = new List<string>();
        var answer = await source.GenerateStreamAsync(Ask(), pieces.Add, default);
        Assert.Equal("Alice wins", answer.Text);
        Assert.Equal(0.00042m, answer.Cost);
        Assert.Equal("Alice wins", string.Concat(pieces));
        var call = Assert.Single(server.Requests);
        Assert.Equal("Bearer sk-test", call.Headers["Authorization"]);
        Assert.True(JsonNode.Parse(call.Body)!["stream"]!.GetValue<bool>());
    }

    [Fact]
    public async Task AServiceBehindAKeyStreamsAndALearnedRefusalOfStreamOptionsSticks()
    {
        using var server = new FakeHttp(Port);
        server.Respond(400, """{"error":{"message":"Unknown parameter: stream_options"}}""");
        server.Respond(200, Sse("""{"choices":[{"delta":{"content":"fine"},"finish_reason":"stop"}]}"""));
        var source = new CloudChatSource("deepseek", "deepseek-v4-flash", null, server.Url.TrimEnd('/'), Plain(), () => "sk-test", () => true);
        var pieces = new List<string>();
        var answer = await source.GenerateStreamAsync(Ask(), pieces.Add, default);
        Assert.Equal("fine", answer.Text);
        Assert.Equal(["fine"], pieces);
        Assert.NotNull(JsonNode.Parse(server.Requests[0].Body)!["stream_options"]);
        Assert.Null(JsonNode.Parse(server.Requests[1].Body)!["stream_options"]);
        Assert.Equal(CloudChatSource.FlatEstimate, answer.Cost);          // no price known, no usage: the estimate is the floor
    }

    [Fact]
    public async Task AnErrorInTheStreamEndsTheReadWithoutTheServicesWords()
    {
        using var server = new FakeHttp(Port);
        server.Respond(200, Sse("""{"choices":[{"delta":{"content":"Ali"}}]}""", """{"error":{"message":"rate limited, key sk-test"}}"""));
        var source = new CloudChatSource("openai", "gpt-5-mini", null, server.Url.TrimEnd('/'), Plain(), () => "sk-test", () => true);
        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => source.GenerateStreamAsync(Ask(), _ => { }, default));
        Assert.DoesNotContain("sk-test", failure.Message);
    }
}

/// <summary>A site's call that asks to stream: pieces first, the whole answer after, and the models and limits that travel with the grant.</summary>
public class StreamingDirectTests : IDisposable
{
    readonly ManualTime _time = new();
    readonly ScriptedUi _ui = new();
    readonly FakeTextSource _alpha = new("lm:alpha") { Answers = new Queue<string>(Enumerable.Repeat("Alice wins by sixty", 20)) };
    readonly FakeTextSource _beta = new("lm:beta") { Answers = new Queue<string>(Enumerable.Repeat("Bob wins by seventy", 20)) };
    readonly SettingsStore _store = new(null);
    readonly ActivityLog _log;
    readonly WalletEngine _engine;
    readonly PairingStore _pairings = new(null);
    readonly TestSite _site = new();
    readonly List<DirectTransport> _transports = [];

    public StreamingDirectTests()
    {
        _log = new ActivityLog(null, _time.Now);
        _engine = new WalletEngine(_store, [], _log, _ui, _time, [_alpha, _beta]);
        _store.Update(s =>
        {
            s.Sources.Add(new SourceSetting { Id = "lm:alpha", Kind = SourceKinds.LmStudio, Model = "google/gemma-3-1b-it", Skills = [Vocabulary.Generate] });
            s.Sources.Add(new SourceSetting { Id = "lm:beta", Kind = SourceKinds.LmStudio, Model = "qwen/qwen3-4b", Skills = [Vocabulary.Generate] });
            Router.EnsureTextChain(s, [_alpha, _beta]);
        });
    }

    public void Dispose()
    {
        foreach (var t in _transports) t.Dispose();
        _site.Dispose();
    }

    const string ModelsManifest = """
        {"byoa":"0.1-draft","purpose":{"summary":"Debate judge","limits":{"maxTextChars":50000,"maxParallelCalls":2}},
         "skills":{"text.generate":{"required":true,"models":{"count":{"min":2,"max":3},"names":true,"why":"Different AIs judge each side."}}},
         "privacy":"personal","expectedCost":{"perSession":"0.05 USD"}}
        """;

    async Task<(DirectConnection Connection, WebSocket Socket, JsonObject Hello)> PairAsync()
    {
        _ui.Consent = new ConsentAnswer(true, 0.05m, AutoDecide: false, []);
        var transport = new DirectTransport(_engine, _pairings, new DirectOptions
        {
            DevLocal = true,
            Backoff = [TimeSpan.FromMilliseconds(50)],
            WelcomeTimeout = TimeSpan.FromSeconds(5),
        }, _time);
        _transports.Add(transport);
        var manifest64 = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(ModelsManifest));
        var link = "bringyourownagent://connect?v=0.1&endpoint=" + Uri.EscapeDataString($"ws://localhost:{_site.Port}/byoa/ws")
            + "&manifest=" + manifest64 + "&session=" + TestSite.NewCode();
        var connection = await transport.HandleLinkAsync(link);
        Assert.NotNull(connection);
        var socket = await _site.NextSocketAsync();
        var hello = await TestSite.ReceiveAsync(socket);
        await TestSite.SendAsync(socket, _site.Welcome("pairing-token-0123456789abcdef", manifest64));
        Assert.True(await connection!.Welcomed.WaitAsync(TimeSpan.FromSeconds(10)));
        return (connection, socket, hello);
    }

    static JsonObject Call(int id, string model, bool stream) => new()
    {
        ["type"] = "call", ["id"] = id, ["skill"] = "text.generate",
        ["input"] = new JsonObject { ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "Who is right?" }) },
        ["params"] = stream ? new JsonObject { ["model"] = model, ["stream"] = true } : new JsonObject { ["model"] = model },
    };

    [Fact]
    public async Task TheHelloCarriesTheModelsAndTheLimitsTheSiteWasGiven()
    {
        var (_, _, hello) = await PairAsync();
        var grant = hello["grant"]!;
        var models = grant["models"]!["text.generate"]!.AsArray();
        Assert.Equal(2, models.Count);
        Assert.Equal("m1", models[0]!["ref"]!.GetValue<string>());
        Assert.Equal("google/gemma-3-1b-it", models[0]!["id"]!.GetValue<string>());           // it asked for the names and the visitor left them on
        Assert.Equal(50_000, grant["limits"]!["maxTextChars"]!.GetValue<int>());
        Assert.Equal(2, grant["limits"]!["maxParallelCalls"]!.GetValue<int>());                // the site's own, below the visitor's 8
    }

    [Fact]
    public async Task AStreamedCallSendsChunksBeforeTheResultAndTheResultHasTheWholeAnswer()
    {
        var (_, socket, _) = await PairAsync();
        await TestSite.SendAsync(socket, Call(7, "m2", stream: true));
        var text = new StringBuilder();
        JsonObject result;
        while (true)
        {
            var frame = await TestSite.ReceiveAsync(socket);
            if (frame["type"]!.GetValue<string>() == "chunk")
            {
                Assert.Equal(7, frame["id"]!.GetValue<int>());
                text.Append(frame["text"]!.GetValue<string>());
                continue;
            }

            result = frame;
            break;
        }

        Assert.Equal("result", result["type"]!.GetValue<string>());
        Assert.Equal("Bob wins by seventy", result["output"]!["text"]!.GetValue<string>());
        Assert.Equal("Bob wins by seventy", text.ToString());
        Assert.Equal("m2", result["receipt"]!["model"]!.GetValue<string>());
        Assert.Equal(0, _alpha.Calls);
    }

    [Fact]
    public async Task ACallThatDoesNotAskToStreamGetsOnlyTheResult()
    {
        var (_, socket, _) = await PairAsync();
        await TestSite.SendAsync(socket, Call(8, "m1", stream: false));
        var frame = await TestSite.ReceiveAsync(socket);
        Assert.Equal("result", frame["type"]!.GetValue<string>());
        Assert.Equal("Alice wins by sixty", frame["output"]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task WhatTheVisitorChangesInSitesReachesTheConnectedSiteInAGrantFrame()
    {
        var (_, socket, _) = await PairAsync();
        _engine.SetSiteModels(_site.Origin, Vocabulary.Generate, ["lm:beta"]);
        var frame = await TestSite.ReceiveAsync(socket);
        Assert.Equal("grant", frame["type"]!.GetValue<string>());
        var models = frame["models"]!["text.generate"]!.AsArray();
        Assert.Single(models);
        Assert.Equal("qwen/qwen3-4b", models[0]!["id"]!.GetValue<string>());
        Assert.Equal(2, frame["limits"]!["maxParallelCalls"]!.GetValue<int>());

        _engine.SetShowModelNames(_site.Origin, false);
        var hidden = await TestSite.ReceiveAsync(socket);
        Assert.Equal("Model A", hidden["models"]!["text.generate"]![0]!["name"]!.GetValue<string>());
        Assert.Null(hidden["models"]!["text.generate"]![0]!["id"]);
    }

    [Fact]
    public async Task OnlyAsManyCallsRunAtOnceAsTheSiteDeclared()
    {
        var (_, socket, _) = await PairAsync();
        _alpha.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        for (var i = 1; i <= 3; i++) await TestSite.SendAsync(socket, Call(i, "m1", stream: false));
        var refused = await TestSite.ReceiveAsync(socket);                       // the third, at once: the site said two
        Assert.Equal(3, refused["id"]!.GetValue<int>());
        Assert.False(refused["ok"]!.GetValue<bool>());
        _alpha.Hold.SetResult();
        for (var i = 0; i < 2; i++) Assert.True((await TestSite.ReceiveAsync(socket))["ok"]!.GetValue<bool>());
    }
}
