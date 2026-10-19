using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Providers;

/// <summary>
/// One chat model through OpenRouter, with the visitor's own API key. Paid: it asks before it runs, and it shows
/// the cost estimate before that — from OpenRouter's public price list when the model is in it, a deliberately
/// round figure when it is not. The real cost comes from the answer's <c>usage</c>.
/// </summary>
public sealed class OpenRouterTextSource : ITextSource, IStreamingTextSource
{
    public const string SecretName = "openrouter";

    /// <summary>The estimate for a short answer while the price list says nothing about the model: comfortably above it.</summary>
    public const decimal FlatEstimate = 0.02m;

    const string ModelEndpoint = "api/v1/models";       // the public price list, no key needed

    readonly HttpClient _http;
    readonly Func<string?> _key;
    readonly Func<bool> _hasKey;
    readonly Uri _baseUrl;
    readonly string _displayModel;
    readonly object _priceGate = new();
    decimal? _promptPerToken;
    decimal? _completionPerToken;
    DateTime _pricedAt;
    string? _lastError;

    /// <param name="secretRef">The name of the key in the secret store; null means the wallet's OpenRouter key.</param>
    readonly Func<IModelCatalog>? _catalog;

    /// <param name="catalog">The wallet's copy of OpenRouter's public model list: the price of a model it knows is used from the start, before this source has read the list itself.</param>
    public OpenRouterTextSource(string model, string? label, string? secretRef, HttpClient http,
        Func<string?> key, Func<bool> hasKey, Uri? baseUrl = null, Func<IModelCatalog>? catalog = null)
    {
        _catalog = catalog;
        Model = model;
        _displayModel = SourceLabels.Clean(model) ?? "model";
        // Keep the wire model id unchanged, but give every UI path a cleaned fallback before Name renders it.
        Label = SourceLabels.Clean(label) ?? _displayModel;
        SecretRef = string.IsNullOrWhiteSpace(secretRef) ? SecretName : secretRef;
        _http = http;
        _key = key;
        _hasKey = hasKey;
        _baseUrl = baseUrl ?? new Uri("https://openrouter.ai/");
    }

    public string Model { get; }
    public string? Label { get; }
    public string? SecretRef { get; }
    public string Id => SourceKinds.OpenRouter + ":" + Model;
    public string Name => (Label ?? Model) + " · " + _baseUrl.Authority;
    public string Runs => Sources.Runs.ApiKey;
    public string ProviderKind => "api-key";
    public IReadOnlyList<string>? Languages => null;
    public bool Paid => true;
    public SourceState State => _hasKey() ? SourceState.Ready : SourceState.NoKey;

    public string StateText => State == SourceState.NoKey
        ? "no API key added"
        : _lastError is null
            ? "key added · paid, about " + Money.Show(EstimateCost(Typical)) + " for a short answer"
            : "key added · last call failed: " + _lastError;

    static TextRequest Typical { get; } = new([new ChatMessage("user", "Hello!")], 512, 0.7, null, null);

    public decimal EstimateCost(TextRequest request)
    {
        lock (_priceGate)
        {
            var prompt = _promptPerToken;
            var completion = _completionPerToken;
            if ((prompt is null || completion is null) && _catalog?.Invoke().Find(ModelNames.Canon(Model)) is { PromptPerToken: { } cp, CompletionPerToken: { } cc })
            {
                prompt = cp;
                completion = cc;
            }

            if (prompt is not { } promptPrice || completion is not { } completionPrice) return FlatEstimate;
            var input = request.Messages.Sum(m => m.Content.Length) / 4m;   // rough: four characters to a token
            try
            {
                var answer = Estimate(input, promptPrice) + Estimate(request.MaxTokens, completionPrice);
                return Math.Max(0.000001m, Math.Ceiling(answer * 1_000_000) / 1_000_000);
            }
            catch (OverflowException)
            {
                return FlatEstimate;
            }
        }
    }

    static decimal Estimate(decimal tokens, decimal perToken)
    {
        try { return Math.Ceiling(tokens * perToken * 1_000_000) / 1_000_000; }
        catch (OverflowException) { return FlatEstimate; }
    }

    /// <summary>Reads the public price list for this model once an hour. Never throws; the estimate stays flat until it succeeds.</summary>
    public async Task PrimePricesAsync(CancellationToken ct = default)
    {
        lock (_priceGate)
        {
            if (DateTime.UtcNow - _pricedAt < TimeSpan.FromHours(1)) return;
            _pricedAt = DateTime.UtcNow;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var response = await _http.GetAsync(new Uri(_baseUrl, ModelEndpoint),
                HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var json = await BoundedHttp.ReadJsonAsync(response, timeout.Token).ConfigureAwait(false);
            if (JsonNode.Parse(json)?["data"] is not JsonArray models) return;
            foreach (var entry in models)
            {
                if (entry?["id"]?.GetValue<string>() != Model) continue;
                var prompt = Price(entry["pricing"]?["prompt"]);
                var completion = Price(entry["pricing"]?["completion"]);
                if (prompt is null || completion is null) return;
                lock (_priceGate)
                {
                    _promptPerToken = prompt;
                    _completionPerToken = completion;
                }

                return;
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // no prices this hour; the flat estimate stays
        }
    }

    /// <summary>Dollars per token, from the price list's string ("0.0000015").</summary>
    static decimal? Price(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<decimal>(out var decimalPrice))
            return decimalPrice is >= 0 and <= 1m ? decimalPrice : null;
        if (value.TryGetValue<double>(out var doublePrice) && double.IsFinite(doublePrice)
            && doublePrice is >= 0 and <= 1d)
            return (decimal)doublePrice;
        if (value.TryGetValue<string>(out var text) && decimal.TryParse(text, NumberStyles.Float,
            CultureInfo.InvariantCulture, out var parsed) && parsed is >= 0 and <= 1m)
            return parsed;
        return null;
    }

    public async Task<TextAnswer> GenerateAsync(TextRequest request, CancellationToken ct)
    {
        var key = _key();
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("no OpenRouter key");
        var messages = new JsonArray();
        foreach (var m in request.Messages)
        {
            messages.Add(new JsonObject { ["role"] = m.Role, ["content"] = m.Content });
        }

        if (request.Choices is not null && _schemaBlocked)
        {
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = Texts.ChoicesInstruction(request.Choices) });
        }

        var body = new JsonObject
        {
            ["model"] = Model,
            ["messages"] = messages,
            ["max_tokens"] = request.MaxTokens,
            ["temperature"] = request.Temperature,
            ["stream"] = false,
            ["usage"] = new JsonObject { ["include"] = true },
        };
        if (request.Choices is not null && !_schemaBlocked)
        {
            body["response_format"] = ChatCompletions.ChoiceSchema(request.Choices);
            body["provider"] = new JsonObject { ["require_parameters"] = true };
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUrl, "api/v1/chat/completions"))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        message.Headers.Add("X-Title", "BYOA agent wallet");
        key = null;

        using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        var text = await BoundedHttp.ReadJsonAsync(response, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // The body may echo the request; only the status goes into the error.
            _lastError = "HTTP " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture);
            if (request.Choices is not null && !_schemaBlocked && (int)response.StatusCode == 400)
            {
                _schemaBlocked = true;                       // this model will not take a schema; the prompt carries the choices
                return await GenerateAsync(request, ct).ConfigureAwait(false);
            }

            throw new HttpRequestException("OpenRouter answered " + _lastError);
        }

        JsonNode? json;
        try
        {
            json = JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            _lastError = "not JSON";
            throw new HttpRequestException("OpenRouter answered something that is not JSON");
        }

        _lastError = null;
        var usage = json?["usage"];
        // A missing or zero usage cost is not proof that the call was free. The estimate is the conservative floor
        // used by the engine and is also returned here so callers outside the engine cannot accidentally erase it.
        var estimate = EstimateCost(request);
        var reported = ChatCompletions.Cost(usage);
        // OpenRouter occasionally returns a literal zero when it omitted billing details. Treat that the same as
        // missing usage; a real positive usage value is the amount the visitor was actually charged.
        var cost = reported is > 0 ? reported.Value : estimate;
        var (input, output) = ChatCompletions.Tokens(usage);
        var reply = ChatCompletions.First(json)?["message"];
        var answer = request.Choices is null
            ? Texts.CleanAnswer(ChatCompletions.Str(reply?["content"]) ?? "")
            : ChatCompletions.ExtractChoice(reply, request.Choices) ?? "";
        if (answer.Length == 0)
        {
            throw new TextSourceException(request.Choices is null
                ? "OpenRouter answered nothing"
                : "OpenRouter did not answer with one of the choices", cost, providerCalled: true, responseReceived: true);
        }

        // Usage and cost were read before checking the answer above. Missing cost is settled by the engine's estimate floor.
        return new TextAnswer(answer, cost, input, output, Model + " · OpenRouter");
    }

    /// <summary>The same answer, handed over as the model writes it; the cost still comes from the usage report at the end of the stream.</summary>
    public async Task<TextAnswer> GenerateStreamAsync(TextRequest request, Action<string> onText, CancellationToken ct)
    {
        var key = _key();
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("no OpenRouter key");
        if (request.Choices is not null)
        {
            var whole = await GenerateAsync(request, ct).ConfigureAwait(false);
            onText(whole.Text);
            return whole;
        }

        var messages = new JsonArray();
        foreach (var m in request.Messages) messages.Add(new JsonObject { ["role"] = m.Role, ["content"] = m.Content });
        var body = new JsonObject
        {
            ["model"] = Model,
            ["messages"] = messages,
            ["max_tokens"] = request.MaxTokens,
            ["temperature"] = request.Temperature,
            ["stream"] = true,
            ["usage"] = new JsonObject { ["include"] = true },
        };
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUrl, "api/v1/chat/completions"))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        message.Headers.Add("X-Title", "BYOA agent wallet");
        key = null;
        using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _lastError = "HTTP " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture);
            throw new HttpRequestException("OpenRouter answered " + _lastError);           // the body may echo the request: only the status is kept
        }

        var streamed = await ChatStream.ReadAsync(response, onText, ct).ConfigureAwait(false);
        _lastError = null;
        var reported = ChatCompletions.Cost(streamed.Usage);
        var cost = reported is > 0 ? reported.Value : EstimateCost(request);
        var (input, output) = ChatCompletions.Tokens(streamed.Usage);
        var answer = Texts.CleanAnswer(streamed.Text);
        if (answer.Length == 0) throw new TextSourceException("OpenRouter answered nothing", cost, providerCalled: true, responseReceived: true);
        return new TextAnswer(answer, cost, input, output, Model + " · OpenRouter");
    }

    /// <summary>A model that refused the structured answer once gets plain instructions from then on.</summary>
    volatile bool _schemaBlocked;
}
