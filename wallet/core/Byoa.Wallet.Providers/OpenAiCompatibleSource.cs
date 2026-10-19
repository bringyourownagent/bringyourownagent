using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Providers;

/// <summary>
/// One model in an OpenAI-compatible runtime: LM Studio, Ollama, or any other chat/completions server (llama.cpp,
/// Jan, vLLM...). The visitor (or his agent) adds it in the settings; the runtime scanner watches whether the
/// server is up and still offers the model. A local runtime answers one request at a time — the wallet keeps a
/// second call from even starting while one runs.
/// </summary>
public sealed class OpenAiCompatibleSource : ITextSource, IStreamingTextSource, ILiveness, ITextSourceWarmable
{
    /// <summary>The room a local model may use for one answer, its own hidden reasoning included.</summary>
    const int MaxLocalTokens = 32_768;

    readonly HttpClient _http;
    readonly string _kind;
    readonly Uri _base;
    readonly bool _loopback;
    readonly string _displayModel;

    /// <summary>A runtime that refused the JSON schema once gets plain instructions from then on.</summary>
    volatile bool _schemaBlocked;

    /// <param name="endpoint">The runtime's base URL from the settings; null means the kind's default.</param>
    public OpenAiCompatibleSource(string kind, string model, string? label, string? endpoint, HttpClient http, string? sourceId = null)
    {
        _kind = kind;
        Model = model;
        _displayModel = SourceLabels.Clean(model) ?? "model";
        _http = http;
        var address = endpoint?.Trim() is { Length: > 0 } given ? given : DefaultEndpoint(kind);
        // Ollama speaks the OpenAI protocol under /v1; the others give the /v1 in their endpoint already.
        if (kind.Equals(SourceKinds.Ollama, StringComparison.OrdinalIgnoreCase) &&
            !address.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            address = address.TrimEnd('/') + "/v1";
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(uri.Host))
            throw new ArgumentException("The endpoint must be an absolute http or https URL.", nameof(endpoint));
        _loopback = IsLoopback(uri.Host);
        _base = new Uri(address.TrimEnd('/') + "/", UriKind.Absolute);
        // Keep the wire model id unchanged, but give every UI path a cleaned fallback before Name renders it.
        Label = SourceLabels.Clean(label) ?? _displayModel;
    }

    public static string DefaultEndpoint(string kind) => kind switch
    {
        SourceKinds.LmStudio => "http://127.0.0.1:1234/v1",
        SourceKinds.Ollama => "http://127.0.0.1:11434",
        _ => "",
    };

    public string Model { get; }
    public string? Label { get; }
    public string Endpoint => _base.ToString().TrimEnd('/');
    // The endpoint is part of the identity. Do not trust an old/custom setting id here: two servers may offer
    // the same model, and the route and scanner must still keep them apart.
    public string Id => MakeId(_kind, Model, Endpoint);

    /// <summary>Stable id for two settings that offer the same model at different endpoints.</summary>
    public static string MakeId(string kind, string model, string? endpoint, string skill = Vocabulary.Generate)
        => SourceIds.OpenAiCompatible(kind, model, endpoint, skill);

    public static bool IsLoopback(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (!IPAddress.TryParse(host, out var address)) return false;
        if (IPAddress.IsLoopback(address)) return true;
        // IsLoopback has not treated every IPv4-mapped IPv6 spelling consistently across supported Windows builds.
        return address.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(address.MapToIPv4());
    }
    public string RuntimeName => LocalRuntimes.Name(_kind, Endpoint);
    public string Name => (Label ?? Model) + " · " + (_loopback ? "this computer" : DisplayAuthority(_base));

    public string Runs => _loopback ? Sources.Runs.ThisDevice : "another computer (" + _base.IdnHost + ")";
    public string ProviderKind => _loopback ? "local-model" : "home-server";
    public IReadOnlyList<string>? Languages => null;
    public bool Paid => false;

    /// <summary>Whether the runtime answered just now: the scanner sets it, null until the first check.</summary>
    public bool? Up { get; set; }

    /// <summary>Whether the runtime still offers this model: false means the source waits for a better choice.</summary>
    public bool? Listed { get; set; }

    /// <summary>A use-time endpoint policy refusal, kept readable for routing and the Sources page.</summary>
    public string? UnavailableReason { get; private set; }

    public SourceState State =>
        !string.IsNullOrWhiteSpace(UnavailableReason) ? SourceState.Unavailable
        : string.IsNullOrWhiteSpace(Model) || Listed == false ? SourceState.Missing : SourceState.Ready;

    public string StateText => State == SourceState.Unavailable
        ? "unavailable: " + UnavailableReason
        : State == SourceState.Missing
        ? string.IsNullOrWhiteSpace(Model) ? "no model chosen yet" : RuntimeName + " does not offer " + Model
        : Up == false ? (_loopback ? RuntimeName : "another computer (" + _base.IdnHost + ")") + " is not running"
        : !_loopback && Up == true ? "ready on another computer (" + _base.IdnHost + ")"
        : Up == true ? "ready · " + RuntimeName
        : "ready when " + RuntimeName + " runs";

    internal void MarkUnavailable(string reason) => UnavailableReason = reason;
    internal void MarkAvailable() => UnavailableReason = null;

    static string DisplayAuthority(Uri uri)
    {
        var host = uri.IdnHost;
        if (host.Contains(':', StringComparison.Ordinal) && !host.StartsWith("[", StringComparison.Ordinal))
            host = "[" + host + "]";
        return host + (uri.IsDefaultPort ? "" : ":" + uri.Port.ToString(CultureInfo.InvariantCulture));
    }

    public decimal EstimateCost(TextRequest request) => 0;

    /// <summary>
    /// Makes a local model do its simplest job for real before a site receives its grant: write one short answer. That loads it in
    /// LM Studio, Ollama, Llama and other OpenAI-compatible servers, and shows that it can answer at all.
    /// </summary>
    public async Task WarmAsync(string? language, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Model)) throw new InvalidOperationException("no model chosen in the settings");
        if (!_loopback) return;
        var request = new TextRequest([new ChatMessage("user", "Reply with one short word: ready.")], 24, 0, language, null);
        // The answer itself is not kept: this is a local warm-up request, not a site call and not a paid source. A model that
        // used its whole budget on hidden reasoning (finish "length") still counts as loaded; one that stopped and wrote nothing does not.
        var (json, _, finish) = await ChatAsync(request, useSchema: false, tokenFactor: 1, ct).ConfigureAwait(false);
        if (AnswerOf(request, ChatCompletions.First(json)?["message"]).Length == 0 && finish != "length")
            throw new InvalidOperationException(RuntimeName + " answered nothing to the warm-up request");
    }

    public async Task<TextAnswer> GenerateAsync(TextRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Model)) throw new InvalidOperationException("no model chosen in the settings");
        var watch = Stopwatch.StartNew();
        var (json, tokens, finish) = await ChatAsync(request, useSchema: !_schemaBlocked, tokenFactor: 1, ct).ConfigureAwait(false);
        var message = ChatCompletions.First(json)?["message"];
        var reasoning = ChatCompletions.Str(message?["reasoning_content"]) ?? ChatCompletions.Str(message?["reasoning"]);
        var text = AnswerOf(request, message);

        if (text.Length == 0 && finish == "length" && !string.IsNullOrEmpty(reasoning))
        {
            // A thinking model can spend the whole budget on its hidden reasoning. A local model tries once more with
            // more room; a paid one would spend the visitor's money twice, so the route hears it straight away.
            if (Paid) throw new InvalidOperationException(RuntimeName + " spent all its tokens on hidden reasoning; raise maxTokens");
            var (retry, more, retryFinish) = await ChatAsync(request, useSchema: !_schemaBlocked, tokenFactor: 4, ct).ConfigureAwait(false);
            message = ChatCompletions.First(retry)?["message"];
            text = AnswerOf(request, message);
            tokens = (tokens.In + more.In, tokens.Out + more.Out);
            if (text.Length == 0 && retryFinish == "length" && !string.IsNullOrEmpty(ChatCompletions.Str(message?["reasoning_content"])))
            {
                throw new InvalidOperationException(RuntimeName + " spent all its tokens on hidden reasoning; raise maxTokens");
            }
        }

        if (text.Length == 0)
        {
            throw new TextSourceException(request.Choices is null
                ? RuntimeName + " answered nothing"
                : RuntimeName + " did not answer with one of the choices",
                cost: 0, providerCalled: true, responseReceived: true);
        }

        var seconds = watch.Elapsed.TotalSeconds;
        return new TextAnswer(text, 0, tokens.In, tokens.Out, RuntimeName + " · " + Model + " · " + seconds.ToString("0.0", CultureInfo.InvariantCulture) + " s");
    }

    /// <summary>The same answer, handed over as the model writes it. A constrained answer (choices) is a word or two and goes whole.</summary>
    public async Task<TextAnswer> GenerateStreamAsync(TextRequest request, Action<string> onText, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Model)) throw new InvalidOperationException("no model chosen in the settings");
        if (request.Choices is not null)
        {
            var whole = await GenerateAsync(request, ct).ConfigureAwait(false);
            onText(whole.Text);
            return whole;
        }

        var watch = Stopwatch.StartNew();
        var body = Body(request, useSchema: false, tokenFactor: 1);
        body["stream"] = true;
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(_base, "chat/completions"))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(RuntimeName + " answered HTTP " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture));
        var streamed = await ChatStream.ReadAsync(response, onText, ct).ConfigureAwait(false);
        var text = Texts.CleanAnswer(streamed.Text);
        if (text.Length == 0 && streamed.Finish == "length" && streamed.Reasoning.Length > 0)
        {
            // The model spent its whole budget thinking and showed nothing: the same second try with more room as without a stream.
            var again = await GenerateAsync(request, ct).ConfigureAwait(false);
            onText(again.Text);
            return again;
        }

        if (text.Length == 0) throw new TextSourceException(RuntimeName + " answered nothing", cost: 0, providerCalled: true, responseReceived: true);
        var tokens = ChatCompletions.Tokens(streamed.Usage);
        return new TextAnswer(text, 0, tokens.In, tokens.Out,
            RuntimeName + " · " + Model + " · " + watch.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s");
    }

    /// <summary>The answer only: hidden reasoning stays in the runtime. With choices, the exact string the page listed.</summary>
    static string AnswerOf(TextRequest request, JsonNode? message) =>
        request.Choices is null
            ? Texts.CleanAnswer(ChatCompletions.Str(message?["content"]) ?? "")
            : ChatCompletions.ExtractChoice(message, request.Choices) ?? "";

    /// <summary>One POST chat/completions. A runtime that refuses the JSON schema gets the choices in the prompt instead.</summary>
    async Task<(JsonNode? Json, (int In, int Out) Tokens, string? Finish)> ChatAsync(TextRequest request, bool useSchema, int tokenFactor, CancellationToken ct, int attempt = 0)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(_base, "chat/completions"))
        {
            Content = new StringContent(Body(request, useSchema, tokenFactor).ToJsonString(), Encoding.UTF8, "application/json"),
        };
        using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        var text = await BoundedHttp.ReadJsonAsync(response, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            if (request.Choices is not null && useSchema && (int)response.StatusCode == 400 && attempt == 0)
            {
                _schemaBlocked = true;
                return await ChatAsync(request, useSchema: false, tokenFactor, ct, attempt: 1).ConfigureAwait(false);
            }

            throw new HttpRequestException(RuntimeName + " answered HTTP " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture));
        }

        JsonNode? json;
        try
        {
            json = JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            throw new HttpRequestException(RuntimeName + " answered something that is not JSON");
        }

        return (json, ChatCompletions.Tokens(json?["usage"]), ChatCompletions.Str(ChatCompletions.First(json)?["finish_reason"]));
    }

    JsonObject Body(TextRequest request, bool useSchema, int tokenFactor)
    {
        var messages = new JsonArray();
        foreach (var m in request.Messages)
        {
            messages.Add(new JsonObject { ["role"] = m.Role, ["content"] = m.Content });
        }

        if (request.Choices is not null && !useSchema)
        {
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = Texts.ChoicesInstruction(request.Choices) });
        }

        var body = new JsonObject
        {
            ["model"] = Model,
            ["messages"] = messages,
            ["max_tokens"] = Math.Min(request.MaxTokens * tokenFactor, MaxLocalTokens),
            ["temperature"] = request.Temperature,
            ["stream"] = false,
        };
        if (useSchema && request.Choices is not null) body["response_format"] = ChatCompletions.ChoiceSchema(request.Choices);
        return body;
    }
}
