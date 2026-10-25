using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Providers;

/// <summary>
/// One chat model at a company that sells models behind an API key and speaks the OpenAI chat protocol (OpenAI, Anthropic, Google, MiniMax,
/// DeepSeek, xAI, Z.ai, Mistral, or another service the visitor names). Paid: it uses the visitor's own key, shows its price before a call
/// and settles the real one after. The address must be https (a key is never sent in the clear) and redirects are never followed. The
/// price comes from the public catalog by the model's name, or a flat estimate when the catalog does not know the model.
/// </summary>
public sealed class CloudChatSource : ITextSource, IStreamingTextSource
{
    /// <summary>The estimate for a short answer when nothing is known about the model's price: comfortably above it.</summary>
    public const decimal FlatEstimate = 0.02m;

    const int MaxAttempts = 6;

    readonly HttpClient _http;
    readonly Func<string?> _key;
    readonly Func<bool> _hasKey;
    readonly Func<IModelCatalog> _catalog;
    readonly Uri _base;
    readonly string _displayModel;
    string? _lastError;

    // What this service has told the source it does not take: from then on the request is made without it.
    volatile bool _schemaBlocked;
    volatile bool _completionTokens;
    volatile bool _noTemperature;
    volatile bool _noStreamOptions;
    volatile bool _levelsNotSwitch;      // GLM-5.3 always reasons: "answer at once" is asked as the lowest level, not as a switch
    volatile bool _noReasoningLevel;
    volatile bool _noThinkingSwitch;

    public CloudChatSource(string service, string model, string? label, string endpoint, HttpClient http,
        Func<string?> key, Func<bool> hasKey, Func<IModelCatalog>? catalog = null)
    {
        Service = service;
        Model = model;
        _displayModel = SourceLabels.Clean(model) ?? "model";
        Label = SourceLabels.Clean(label) ?? _displayModel;
        // A key is never sent in the clear over a network. A gateway on this very computer (LiteLLM, a proxy) is not a network.
        if (!Uri.TryCreate(endpoint?.Trim(), UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host)
            || !(uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp && OpenAiCompatibleSource.IsLoopback(uri.Host)))
            throw new ArgumentException("A source behind an API key needs a complete https address: the key is never sent in the clear.", nameof(endpoint));
        _base = new Uri(endpoint.Trim().TrimEnd('/') + "/", UriKind.Absolute);
        _http = http;
        _key = key;
        _hasKey = hasKey;
        _catalog = catalog ?? (() => EmptyCatalog.Instance);
        // OpenAI's newer models take the length as max_completion_tokens and refuse max_tokens; the others are the other way round.
        _completionTokens = service == "openai";
    }

    public string Service { get; }
    public string Model { get; }
    public string? Label { get; }
    public string Endpoint => _base.ToString().TrimEnd('/');
    public string Id => SourceIds.Api(Service, Model, Endpoint);
    public string Name => (Label ?? Model) + " · " + (Service == ApiServices.Custom ? _base.IdnHost : ApiServices.NameOf(Service));
    public string Runs => Sources.Runs.ApiKey;
    public string ProviderKind => "api-key";
    public IReadOnlyList<string>? Languages => null;

    /// <summary>A free public demo service: no key is needed, nothing it answers costs money, but it is not private.</summary>
    public bool Demo => ApiServices.IsDemo(Service);

    public bool Paid => !Demo;
    public SourceState State => Demo || _hasKey() ? SourceState.Ready : SourceState.NoKey;

    /// <summary>The model's name as the catalog spells it: the service's vendor, then the model.</summary>
    public string Canonical
    {
        get
        {
            var tail = ModelNames.Canon(Model);
            var vendor = ApiServices.Find(Service)?.Vendor;
            return vendor is null || tail.Contains('/') ? tail : vendor + "/" + tail;
        }
    }

    public string StateText => State == SourceState.NoKey
        ? "no " + (ApiServices.Find(Service)?.Name ?? "API") + " key added"
        : Demo && !_hasKey()
            ? "free public service: no key needed · may keep what you send; slow and limited; not for private text"
            : _lastError is null
            ? "key added · " + (Service == "zai-coding" ? "paid by your Coding Plan, counted here at the public price, about " : "paid, about ")
              + Money.Show(EstimateCost(Typical)) + " for a short answer"
            : "key added · last call failed: " + _lastError;

    static TextRequest Typical { get; } = new([new ChatMessage("user", "Hello!")], 512, 0.7, null, null);

    public decimal EstimateCost(TextRequest request)
    {
        if (Demo) return 0m;                                    // a public demo answers for free; the risk is privacy, not money
        var entry = _catalog().Find(Canonical);
        if (entry?.PromptPerToken is not { } prompt || entry.CompletionPerToken is not { } completion) return FlatEstimate;
        return Cost(prompt, completion, request.Messages.Sum(m => m.Content.Length) / 4m, request.MaxTokens) ?? FlatEstimate;
    }

    static decimal? Cost(decimal promptPerToken, decimal completionPerToken, decimal inputTokens, decimal outputTokens)
    {
        try
        {
            var cost = inputTokens * promptPerToken + outputTokens * completionPerToken;
            return Math.Max(0.000001m, Math.Ceiling(cost * 1_000_000) / 1_000_000);
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    public async Task<TextAnswer> GenerateAsync(TextRequest request, CancellationToken ct)
    {
        var key = _key();
        if (string.IsNullOrWhiteSpace(key) && !Demo) throw new InvalidOperationException("no API key for " + (ApiServices.Find(Service)?.Name ?? "this service"));
        for (var attempt = 1; ; attempt++)
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(_base, "chat/completions"))
            {
                Content = new StringContent(Body(request).ToJsonString(), Encoding.UTF8, "application/json"),
            };
            if (!string.IsNullOrWhiteSpace(key)) message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            var text = await BoundedHttp.ReadJsonAsync(response, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                // The body may echo the request, so only the status goes into an error (and, for a refusal about money or a limit, the
                // service's own short reason). It is read here, to learn what this service refuses.
                if (status == 400 && attempt < MaxAttempts && LearnFromRefusal(text, request)) continue;
                _lastError = "HTTP " + status.ToString(CultureInfo.InvariantCulture) + Reason(status, text);
                throw new HttpRequestException((ApiServices.Find(Service)?.Name ?? "The service") + " answered " + _lastError);
            }

            return Settle(text, request);
        }
    }

    /// <summary>
    /// For a refusal about money or a limit (402, 429) the service's own short reason ("Insufficient balance or no resource package"): it is what the
    /// visitor needs to read, and without it a bare "HTTP 429" cannot be told from a busy service. Anything that looks like a key or a token is left
    /// out, and nothing else is ever taken from a body.
    /// </summary>
    static string Reason(int status, string body)
    {
        if (status is not (402 or 429) || string.IsNullOrWhiteSpace(body)) return "";
        try
        {
            var error = JsonNode.Parse(body)?["error"];
            var message = error is JsonValue v && v.TryGetValue<string>(out var plain) ? plain : ChatCompletions.Str(error?["message"]);
            if (string.IsNullOrWhiteSpace(message)) return "";
            message = Regex.Replace(message.Trim(), @"[A-Za-z0-9_.\-]{24,}", "…");
            return ": " + (message.Length > 160 ? message[..157] + "…" : message);
        }
        catch (JsonException)
        {
            return "";
        }
    }

    /// <summary>Remembers what a 400 says the service does not take, so the next try leaves it out. False when it says nothing the wallet can act on.</summary>
    bool LearnFromRefusal(string body, TextRequest request)
    {
        var said = body.ToLowerInvariant();
        if (said.Contains("max_completion_tokens", StringComparison.Ordinal) && !_completionTokens)
        {
            _completionTokens = true;
            return true;
        }

        if (said.Contains("max_tokens", StringComparison.Ordinal) && _completionTokens && !said.Contains("max_completion_tokens", StringComparison.Ordinal))
        {
            _completionTokens = false;
            return true;
        }

        if (said.Contains("stream_options", StringComparison.Ordinal) && !_noStreamOptions)
        {
            _noStreamOptions = true;
            return true;
        }

        if (said.Contains("cannot be disabled", StringComparison.Ordinal) && !_levelsNotSwitch)
        {
            _levelsNotSwitch = true;                     // this model reasons always: the next try asks for the lowest level instead
            return true;
        }

        if (said.Contains("reasoning_effort", StringComparison.Ordinal) && !_noReasoningLevel)
        {
            _noReasoningLevel = true;
            return true;
        }

        if (said.Contains("thinking", StringComparison.Ordinal) && !request.Thinking && !_levelsNotSwitch && !_noThinkingSwitch)
        {
            _noThinkingSwitch = true;
            return true;
        }

        if (said.Contains("temperature", StringComparison.Ordinal) && !_noTemperature)
        {
            _noTemperature = true;
            return true;
        }

        if (request.Choices is not null && !_schemaBlocked)
        {
            _schemaBlocked = true;                       // this model will not take a schema; the prompt carries the choices
            return true;
        }

        return false;
    }

    /// <summary>The same answer, handed over as the model writes it; the cost is the catalog's price for the tokens the stream's last report counts.</summary>
    public async Task<TextAnswer> GenerateStreamAsync(TextRequest request, Action<string> onText, CancellationToken ct)
    {
        var key = _key();
        if (string.IsNullOrWhiteSpace(key) && !Demo) throw new InvalidOperationException("no API key for " + (ApiServices.Find(Service)?.Name ?? "this service"));
        if (request.Choices is not null)
        {
            var whole = await GenerateAsync(request, ct).ConfigureAwait(false);
            onText(whole.Text);
            return whole;
        }

        for (var attempt = 1; ; attempt++)
        {
            var body = Body(request);
            body["stream"] = true;
            if (!_noStreamOptions) body["stream_options"] = new JsonObject { ["include_usage"] = true };
            using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(_base, "chat/completions"))
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            if (!string.IsNullOrWhiteSpace(key)) message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                var said = status == 400 && attempt < MaxAttempts || status is 402 or 429
                    ? await BoundedHttp.ReadJsonAsync(response, ct).ConfigureAwait(false) : "";
                if (status == 400 && said.Length > 0 && LearnFromRefusal(said, request)) continue;
                _lastError = "HTTP " + status.ToString(CultureInfo.InvariantCulture) + Reason(status, said);
                throw new HttpRequestException((ApiServices.Find(Service)?.Name ?? "The service") + " answered " + _lastError);
            }

            var streamed = await ChatStream.ReadAsync(response, onText, ct).ConfigureAwait(false);
            _lastError = null;
            var (input, output) = ChatCompletions.Tokens(streamed.Usage);
            var estimate = EstimateCost(request);
            var entry = _catalog().Find(Canonical);
            var cost = entry is { PromptPerToken: { } p, CompletionPerToken: { } c } && (input > 0 || output > 0) ? Cost(p, c, input, output) ?? estimate : estimate;
            var answer = Texts.CleanAnswer(streamed.Text);
            if (answer.Length == 0) throw new TextSourceException("The service answered nothing", cost, providerCalled: true, responseReceived: true);
            return new TextAnswer(answer, cost, input, output, Model + " · " + (ApiServices.Find(Service)?.Name ?? "API"));
        }
    }

    JsonObject Body(TextRequest request)
    {
        var messages = new JsonArray();
        foreach (var m in request.Messages) messages.Add(new JsonObject { ["role"] = m.Role, ["content"] = m.Content });
        if (request.Choices is not null && _schemaBlocked)
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = Texts.ChoicesInstruction(request.Choices) });
        var body = new JsonObject { ["model"] = Model, ["messages"] = messages, ["stream"] = false };
        body[_completionTokens ? "max_completion_tokens" : "max_tokens"] = request.MaxTokens;
        if (!_noTemperature) body["temperature"] = request.Temperature;
        if (request.Choices is not null && !_schemaBlocked) body["response_format"] = ChatCompletions.ChoiceSchema(request.Choices);
        if (!request.Thinking && IsZai) AskForNoThinking(body);
        return body;
    }

    bool IsZai => Service is "zai" or "zai-coding";

    /// <summary>
    /// "Answer at once" for a GLM model. GLM-5.3 (Flash too) reasons always and refuses to be switched off with an error, but takes a level: the
    /// lowest is as near to an immediate answer as it gets. Older GLM models take a switch. A service that refuses either is not asked again.
    /// </summary>
    void AskForNoThinking(JsonObject body)
    {
        if (_levelsNotSwitch || Model.StartsWith("glm-5.3", StringComparison.OrdinalIgnoreCase))
        {
            if (!_noReasoningLevel) body["reasoning_effort"] = "low";
        }
        else if (!_noThinkingSwitch)
        {
            body["thinking"] = new JsonObject { ["type"] = "disabled" };
        }
    }

    TextAnswer Settle(string text, TextRequest request)
    {
        JsonNode? json;
        try
        {
            json = JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            _lastError = "not JSON";
            throw new HttpRequestException("The service answered something that is not JSON");
        }

        _lastError = null;
        var usage = json?["usage"];
        var (input, output) = ChatCompletions.Tokens(usage);
        var entry = _catalog().Find(Canonical);
        var estimate = EstimateCost(request);
        // A real count of tokens at the catalog's price is the cost; without either, the estimate is the conservative floor.
        var cost = entry is { PromptPerToken: { } p, CompletionPerToken: { } c } && (input > 0 || output > 0)
            ? Cost(p, c, input, output) ?? estimate
            : estimate;
        var reply = ChatCompletions.First(json)?["message"];
        var answer = request.Choices is null
            ? Texts.CleanAnswer(ChatCompletions.Str(reply?["content"]) ?? "")
            : ChatCompletions.ExtractChoice(reply, request.Choices) ?? "";
        if (answer.Length == 0)
        {
            throw new TextSourceException(request.Choices is null
                ? "The service answered nothing"
                : "The service did not answer with one of the choices", cost, providerCalled: true, responseReceived: true);
        }

        return new TextAnswer(answer, cost, input, output, Model + " · " + (ApiServices.Find(Service)?.Name ?? "API"));
    }
}
