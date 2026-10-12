using System.Diagnostics;
using System.Globalization;
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
public sealed class OpenAiCompatibleSource : ITextSource, ILiveness
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
    public OpenAiCompatibleSource(string kind, string model, string? label, string? endpoint, HttpClient http)
    {
        _kind = kind;
        Model = model;
        _http = http;
        var address = endpoint?.Trim() is { Length: > 0 } given ? given : DefaultEndpoint(kind);
        // Ollama speaks the OpenAI protocol under /v1; the others give the /v1 in their endpoint already.
        if (kind == SourceKinds.Ollama && !address.EndsWith("/v1", StringComparison.Ordinal)) address = address.TrimEnd('/') + "/v1";
        _loopback = Uri.TryCreate(address, UriKind.Absolute, out var uri) &&
            uri.Host is "127.0.0.1" or "localhost" or "[::1]" or "::1";
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
    public string Id => _kind + ":" + Model;
    public string RuntimeName => _kind switch
    {
        SourceKinds.LmStudio => "LM Studio",
        SourceKinds.Ollama => "Ollama",
        _ => "OpenAI-compatible server",
    };
    public string Name => Label is { Length: > 0 } ? Label : Model + " in " + RuntimeName;

    public string Runs => _loopback ? Sources.Runs.ThisDevice : "another computer";
    public string ProviderKind => "local-model";
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
        : Up == false ? RuntimeName + " is not running"
        : Up == true ? "ready · " + RuntimeName
        : "ready when " + RuntimeName + " runs";

    public decimal EstimateCost(TextRequest request) => 0;

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
            throw new InvalidOperationException(request.Choices is null
                ? RuntimeName + " answered nothing"
                : RuntimeName + " did not answer with one of the choices");
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
        using var response = await _http.SendAsync(message, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
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
        if (useSchema) body["response_format"] = ChatCompletions.ChoiceSchema(request.Choices!);
        return body;
    }
}
