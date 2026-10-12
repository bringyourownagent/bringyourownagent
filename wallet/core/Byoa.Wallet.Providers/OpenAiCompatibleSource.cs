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
public sealed class OpenAiCompatibleSource : ITextSource, ILiveness, ITextSourceWarmable
{
    /// <summary>The room a local model may use for one answer, its own hidden reasoning included.</summary>
    const int MaxLocalTokens = 4096;

    readonly HttpClient _http;
    readonly string _kind;
    readonly Uri _base;
    readonly bool _loopback;

    /// <summary>A runtime that refused the JSON schema once gets plain instructions from then on.</summary>
    volatile bool _schemaBlocked;

    /// <param name="endpoint">The runtime's base URL from the settings; null means the kind's default.</param>
    public OpenAiCompatibleSource(string kind, string model, string? label, string? endpoint, HttpClient http, string? sourceId = null)
    {
        _kind = kind;
        Model = model;
        _http = http;
        var address = endpoint?.Trim() is { Length: > 0 } given ? given : DefaultEndpoint(kind);
        // Ollama speaks the OpenAI protocol under /v1; the others give the /v1 in their endpoint already.
        if (kind == SourceKinds.Ollama && !address.EndsWith("/v1", StringComparison.Ordinal)) address = address.TrimEnd('/') + "/v1";
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(uri.Host))
            throw new ArgumentException("The endpoint must be an absolute http or https URL.", nameof(endpoint));
        _loopback = IsLoopback(uri.Host);
        _base = new Uri(address.TrimEnd('/') + "/", UriKind.Absolute);
        Label = label;
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
    public static string MakeId(string kind, string model, string? endpoint)
    {
        var address = endpoint?.Trim().TrimEnd('/') ?? DefaultEndpoint(kind);
        if (kind == SourceKinds.Ollama && !address.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            address += "/v1";
        return kind.Trim().ToLowerInvariant() + ":" + model.Trim() + "@" + address.ToLowerInvariant();
    }

    public static bool IsLoopback(string host)
    {
        if (!IPAddress.TryParse(host, out var address)) return false;
        if (IPAddress.IsLoopback(address)) return true;
        // IsLoopback has not treated every IPv4-mapped IPv6 spelling consistently across supported Windows builds.
        return address.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(address.MapToIPv4());
    }
    public string RuntimeName => _kind switch
    {
        SourceKinds.LmStudio => "LM Studio",
        SourceKinds.Ollama => "Ollama",
        _ => "OpenAI-compatible server",
    };
    public string Name => Label is { Length: > 0 } ? Label : Model + " in " + RuntimeName;

    public string Runs => _loopback ? Sources.Runs.ThisDevice : "another computer (" + _base.Host + ")";
    public string ProviderKind => _loopback ? "local-model" : "home-server";
    public IReadOnlyList<string>? Languages => null;
    public bool Paid => false;

    /// <summary>Whether the runtime answered just now: the scanner sets it, null until the first check.</summary>
    public bool? Up { get; set; }

    /// <summary>Whether the runtime still offers this model: false means the source waits for a better choice.</summary>
    public bool? Listed { get; set; }

    public SourceState State =>
        string.IsNullOrWhiteSpace(Model) || Listed == false ? SourceState.Missing : SourceState.Ready;

    public string StateText => State == SourceState.Missing
        ? string.IsNullOrWhiteSpace(Model) ? "no model chosen yet" : RuntimeName + " does not offer " + Model
        : Up == false ? (_loopback ? RuntimeName : "another computer (" + _base.Host + ")") + " is not running"
        : !_loopback && Up == true ? "ready on another computer (" + _base.Host + ")"
        : Up == true ? "ready · " + RuntimeName
        : "ready when " + RuntimeName + " runs";

    public decimal EstimateCost(TextRequest request) => 0;

    /// <summary>Asks a local runtime to load this model before a site receives its grant.</summary>
    public async Task WarmAsync(string? language, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Model)) throw new InvalidOperationException("no model chosen in the settings");
        if (!_loopback) return;
        var request = new TextRequest([new ChatMessage("user", "Reply with OK when ready.")], 1, 0, language, null);
        // The response is deliberately discarded. This is a local load/warm-up request, not a site call and not a
        // paid source. It is enough for LM Studio, Ollama and other OpenAI-compatible servers to load the model.
        await ChatAsync(request, useSchema: false, tokenFactor: 1, ct).ConfigureAwait(false);
    }

    public async Task<TextAnswer> GenerateAsync(TextRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Model)) throw new InvalidOperationException("no model chosen in the settings");
        var watch = Stopwatch.StartNew();
        var (json, tokens, finish) = await ChatAsync(request, useSchema: !_schemaBlocked, tokenFactor: 1, ct).ConfigureAwait(false);
        var message = json?["choices"]?[0]?["message"];
        var reasoning = ChatCompletions.Str(message?["reasoning_content"]) ?? ChatCompletions.Str(message?["reasoning"]);
        var text = AnswerOf(request, message);

        if (text.Length == 0 && finish == "length" && !string.IsNullOrEmpty(reasoning))
        {
            // A thinking model can spend the whole budget on its hidden reasoning. A local model tries once more with
            // more room; a paid one would spend the visitor's money twice, so the route hears it straight away.
            if (Paid) throw new InvalidOperationException(RuntimeName + " spent all its tokens on hidden reasoning; raise maxTokens");
            var (retry, more, retryFinish) = await ChatAsync(request, useSchema: !_schemaBlocked, tokenFactor: 4, ct).ConfigureAwait(false);
            message = retry?["choices"]?[0]?["message"];
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

        return (json, ChatCompletions.Tokens(json?["usage"]), ChatCompletions.Str(json?["choices"]?[0]?["finish_reason"]));
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
