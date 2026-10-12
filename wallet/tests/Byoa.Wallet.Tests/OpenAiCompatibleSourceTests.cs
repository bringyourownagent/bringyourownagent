using System.Text.Json.Nodes;
using Byoa.Wallet.Providers;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Tests;

/// <summary>The OpenAI-compatible source against a fake runtime on a loopback port: what it sends, what it reads
/// back, and what it does when the runtime refuses the schema or burns its tokens on hidden reasoning.</summary>
public class OpenAiCompatibleSourceTests
{
    const int Port = 5321;

    static HttpClient NoRedirects() => new(new SocketsHttpHandler { AllowAutoRedirect = false });

    static OpenAiCompatibleSource Source(FakeHttp server, string kind = SourceKinds.LmStudio, string model = "qwen/qwen3.5-4b", string? label = null) =>
        new(kind, model, label, server.Url + "v1", NoRedirects());

    static TextRequest Plain(int maxTokens = 512) =>
        new([new ChatMessage("user", "What is the capital of France?")], maxTokens, 0.7, null, null);

    static TextRequest Quiz() =>
        new([new ChatMessage("user", "Is Paris the capital of France?")], 512, 0.7, null, ["yes", "no"]);

    [Fact]
    public async Task APlainCallSendsTheModelAndReadsTheAnswer()
    {
        using var server = new FakeHttp(Port);
        server.Respond(200, """{"choices":[{"message":{"role":"assistant","content":"  Paris "},"finish_reason":"stop"}],"usage":{"prompt_tokens":7,"completion_tokens":2}}""");
        var answer = await Source(server).GenerateAsync(Plain(), default);
        Assert.Equal("Paris", answer.Text);
        Assert.Equal(0, answer.Cost);
        Assert.Equal((7, 2), (answer.InputTokens, answer.OutputTokens));
        Assert.StartsWith("LM Studio · qwen/qwen3.5-4b · ", answer.Detail);

        var call = Assert.Single(server.Requests);
        Assert.Equal("/v1/chat/completions", call.Path);
        var body = JsonNode.Parse(call.Body)!.AsObject();
        Assert.Equal("qwen/qwen3.5-4b", body["model"]!.GetValue<string>());
        Assert.Equal(512, body["max_tokens"]!.GetValue<int>());
        Assert.Equal(0.7, body["temperature"]!.GetValue<double>());
        Assert.False(body["stream"]!.GetValue<bool>());
        Assert.Null(body["response_format"]);
        Assert.Equal("user", body["messages"]![0]!["role"]!.GetValue<string>());
    }

    [Fact]
    public async Task HiddenThinkingNeverReachesTheAnswer()
    {
        using var server = new FakeHttp(Port);
        server.Respond(200, """{"choices":[{"message":{"content":"<think>the capital of France is, let me see…</think>Paris"},"finish_reason":"stop"}]}""");
        Assert.Equal("Paris", (await Source(server).GenerateAsync(Plain(), default)).Text);
    }

    [Fact]
    public async Task ChoicesGoAsASchemaAndComeBackAsJson()
    {
        using var server = new FakeHttp(Port);
        server.Respond(200, """{"choices":[{"message":{"content":"{\"answer\":\"NO\"}"},"finish_reason":"stop"}]}""");
        var answer = await Source(server).GenerateAsync(Quiz(), default);
        Assert.Equal("no", answer.Text);                          // the page's exact choice, not the model's casing

        var body = JsonNode.Parse(Assert.Single(server.Requests).Body)!.AsObject();
        Assert.Equal("json_schema", body["response_format"]!["type"]!.GetValue<string>());
        var schema = body["response_format"]!["json_schema"]!["schema"]!;
        Assert.Equal("""["yes","no"]""", schema["properties"]!["answer"]!["enum"]!.ToJsonString());
        Assert.Single(body["messages"]!.AsArray());               // the choices ride in the schema, not the prompt
    }

    [Fact]
    public async Task ARuntimeThatRefusesTheSchemaGetsTheChoicesInThePrompt()
    {
        using var server = new FakeHttp(Port);
        var source = Source(server);                              // one runtime: its refusal is remembered
        server.Respond(400, """{"error":"response_format is not supported"}""");
        server.Respond(200, """{"choices":[{"message":{"content":"YES"},"finish_reason":"stop"}]}""");
        Assert.Equal("yes", (await source.GenerateAsync(Quiz(), default)).Text);

        var calls = server.Requests;
        Assert.Equal(2, calls.Count);
        Assert.NotNull(JsonNode.Parse(calls[0].Body)!["response_format"]);
        var retry = JsonNode.Parse(calls[1].Body)!.AsObject();
        Assert.Null(retry["response_format"]);
        var last = retry["messages"]!.AsArray()[^1]!.AsObject();
        Assert.Equal("system", last["role"]!.GetValue<string>());
        Assert.Contains("\"yes\" | \"no\"", last["content"]!.GetValue<string>());

        server.Respond(200, """{"choices":[{"message":{"content":"no"}}]}""");    // and never asked with a schema again
        Assert.Equal("no", (await source.GenerateAsync(Quiz(), default)).Text);
        Assert.Equal(3, server.Requests.Count);
        Assert.Null(JsonNode.Parse(server.Requests[2].Body)!["response_format"]);
    }

    [Fact]
    public async Task AThinkingModelThatBurntItsTokensGetsOneMoreTryWithRoom()
    {
        using var server = new FakeHttp(Port);
        server.Respond(200, """{"choices":[{"message":{"content":"","reasoning_content":"let me think about this"},"finish_reason":"length"}],"usage":{"prompt_tokens":11,"completion_tokens":512}}""");
        server.Respond(200, """{"choices":[{"message":{"content":"no"},"finish_reason":"stop"}],"usage":{"prompt_tokens":11,"completion_tokens":3}}""");
        var answer = await Source(server).GenerateAsync(Plain(maxTokens: 512), default);
        Assert.Equal("no", answer.Text);
        Assert.Equal((22, 515), (answer.InputTokens, answer.OutputTokens));   // both tries together

        var calls = server.Requests;
        Assert.Equal(2, calls.Count);
        Assert.Equal(512, JsonNode.Parse(calls[0].Body)!["max_tokens"]!.GetValue<int>());
        Assert.Equal(2048, JsonNode.Parse(calls[1].Body)!["max_tokens"]!.GetValue<int>());    // four times the room
    }

    [Fact]
    public async Task AModelThatBurnsItsTokensTwiceIsAPlainError()
    {
        using var server = new FakeHttp(Port);
        server.Respond(200, """{"choices":[{"message":{"content":"","reasoning_content":"thinking"},"finish_reason":"length"}]}""");
        server.Respond(200, """{"choices":[{"message":{"content":"","reasoning_content":"still thinking"},"finish_reason":"length"}]}""");
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => Source(server).GenerateAsync(Plain(), default));
        Assert.Contains("hidden reasoning", e.Message);
        Assert.Equal(2, server.Requests.Count);                   // a local model may try twice, but no more
    }

    [Fact]
    public async Task TheConstrainedAnswerCanComeBackInTheReasoningChannel()
    {
        using var server = new FakeHttp(Port);
        server.Respond(200, """{"choices":[{"message":{"content":"","reasoning_content":"{\"answer\":\"no\"}"},"finish_reason":"stop"}]}""");
        Assert.Equal("no", (await Source(server).GenerateAsync(Quiz(), default)).Text);
    }

    [Fact]
    public async Task ADeadOrBrokenRuntimeIsAPlainError()
    {
        using var server = new FakeHttp(Port);
        server.Respond(500, "boom");
        var httpError = await Assert.ThrowsAsync<HttpRequestException>(() => Source(server).GenerateAsync(Plain(), default));
        Assert.Equal("LM Studio answered HTTP 500", httpError.Message);

        server.Respond(200, "hello, not JSON");
        httpError = await Assert.ThrowsAsync<HttpRequestException>(() => Source(server).GenerateAsync(Plain(), default));
        Assert.Equal("LM Studio answered something that is not JSON", httpError.Message);

        server.Respond(200, """{"choices":[{"message":{"content":""}}]}""");
        var empty = await Assert.ThrowsAsync<TextSourceException>(() => Source(server).GenerateAsync(Plain(), default));
        Assert.Equal("LM Studio answered nothing", empty.Message);

        server.Respond(200, """{"choices":[{"message":{"content":"maybe"}}]}""");
        var missed = await Assert.ThrowsAsync<TextSourceException>(() => Source(server).GenerateAsync(Quiz(), default));
        Assert.Equal("LM Studio did not answer with one of the choices", missed.Message);
    }

    [Fact]
    public async Task ARuntimeCannotSendTheWalletToAnotherComputer()
    {
        using var server = new FakeHttp(Port);
        server.Respond(302, "{}");                                // a redirect would point off this PC; the wallet stays
        var e = await Assert.ThrowsAsync<HttpRequestException>(() => Source(server).GenerateAsync(Plain(), default));
        Assert.Equal("LM Studio answered HTTP 302", e.Message);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task AResponseBodyOverTheCapIsRefusedBeforeJsonParsing()
    {
        using var server = new FakeHttp(Port);
        server.Respond(200, new string('x', 4 * 1024 * 1024 + 1));
        var e = await Assert.ThrowsAsync<HttpRequestException>(() => Source(server).GenerateAsync(Plain(), default));
        Assert.Contains("larger than", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OllamaSpeaksUnderItsV1Path()
    {
        using var server = new FakeHttp(Port);
        server.Respond(200, """{"choices":[{"message":{"content":"Lyon"}}]}""");
        var ollama = new OpenAiCompatibleSource(SourceKinds.Ollama, "llama3.2:3b", null, server.Url, NoRedirects());
        Assert.Equal("Lyon", (await ollama.GenerateAsync(Plain(), default)).Text);
        Assert.Equal("/v1/chat/completions", Assert.Single(server.Requests).Path);

        var withV1 = new OpenAiCompatibleSource(SourceKinds.Ollama, "llama3.2:3b", null, server.Url + "v1", NoRedirects());
        server.Respond(200, """{"choices":[{"message":{"content":"Nice"}}]}""");                  // a /v1 already typed is not doubled
        Assert.Equal("Nice", (await withV1.GenerateAsync(Plain(), default)).Text);
        Assert.Equal("/v1/chat/completions", server.Requests[1].Path);
    }

    [Fact]
    public void TheStateTextTellsPeopleWhatIsWrong()
    {
        var source = new OpenAiCompatibleSource(SourceKinds.LmStudio, "qwen/qwen3.5-4b", null, "http://127.0.0.1:5321/v1", NoRedirects());
        Assert.Contains("lmstudio:qwen/qwen3.5-4b@http://127.0.0.1:5321/v1", source.Id);
        Assert.Equal("qwen/qwen3.5-4b in LM Studio", source.Name);
        Assert.Equal(SourceState.Ready, source.State);
        Assert.Equal("ready when LM Studio runs", source.StateText);          // not checked yet
        source.Up = true;
        Assert.Equal("ready · LM Studio", source.StateText);
        source.Up = false;
        Assert.Equal("LM Studio is not running", source.StateText);
        source.Listed = false;
        Assert.Equal(SourceState.Missing, source.State);
        Assert.Equal("LM Studio does not offer qwen/qwen3.5-4b", source.StateText);

        var labelled = new OpenAiCompatibleSource(SourceKinds.OpenAiCompatible, "mistral-7b", "Quiz model", "http://192.168.1.50:8000/v1", NoRedirects());
        Assert.Equal("Quiz model", labelled.Name);
        Assert.Equal("OpenAI-compatible server", labelled.RuntimeName);
        Assert.Equal("another computer (192.168.1.50)", labelled.Runs); // not a loopback address
        Assert.Equal("home-server", labelled.ProviderKind);
        Assert.Equal(Runs.ThisDevice, source.Runs);
    }

    [Theory]
    [InlineData("http://[::1]:5321/v1")]
    [InlineData("http://[::ffff:127.0.0.1]:5321/v1")]
    public void IPv6LoopbackAddressesAreLocal(string endpoint)
    {
        var source = new OpenAiCompatibleSource(SourceKinds.OpenAiCompatible, "model", null, endpoint, NoRedirects());
        Assert.Equal("local-model", source.ProviderKind);
        Assert.Equal(Runs.ThisDevice, source.Runs);
    }

    [Fact]
    public void ARemoteEndpointIsAHomeServerNotAKey()
    {
        var source = new OpenAiCompatibleSource(SourceKinds.OpenAiCompatible, "model", null, "https://home.example.test/v1", NoRedirects());
        Assert.Equal("home-server", source.ProviderKind);
        Assert.Equal("another computer (home.example.test)", source.Runs);
    }
}
