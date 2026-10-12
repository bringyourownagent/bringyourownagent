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
public sealed class OpenRouterTextSource : ITextSource
{
    public const string SecretName = "openrouter";

    /// <summary>The estimate for a short answer while the price list says nothing about the model: comfortably above it.</summary>
    public const decimal FlatEstimate = 0.02m;

    const string ModelEndpoint = "api/v1/models";       // the public price list, no key needed

    readonly HttpClient _http;
    readonly Func<string?> _key;
    readonly Func<bool> _hasKey;
    readonly Uri _baseUrl;
    readonly object _priceGate = new();
    decimal? _promptPerToken;
    decimal? _completionPerToken;
    DateTime _pricedAt;
    string? _lastError;

    /// <param name="secretRef">The name of the key in the secret store; null means the wallet's OpenRouter key.</param>
    public OpenRouterTextSource(string model, string? label, string? secretRef, HttpClient http,
        Func<string?> key, Func<bool> hasKey, Uri? baseUrl = null)
    {
        Model = model;
        Label = label;
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
    public string Name => Label is { Length: > 0 } ? Label : Model + " (OpenRouter)";
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
            if (_promptPerToken is not { } prompt || _completionPerToken is not { } completion) return FlatEstimate;
            var input = request.Messages.Sum(m => m.Content.Length) / 4m;   // rough: four characters to a token
            var answer = Estimate(input, prompt) + Estimate(request.MaxTokens, completion);
            return Math.Max(0.000001m, Math.Ceiling(answer * 1_000_000) / 1_000_000);
        }
    }

    static decimal Estimate(decimal tokens, decimal perToken) => Math.Ceiling(tokens * perToken * 1_000_000) / 1_000_000;

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
            using var response = await _http.GetAsync(new Uri(_baseUrl, ModelEndpoint), timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
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
    static decimal? Price(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) &&
        decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) && p >= 0 ? p : null;

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

        using var response = await _http.SendAsync(message, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
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
        var reply = json?["choices"]?[0]?["message"];
        var answer = request.Choices is null
            ? Texts.CleanAnswer(ChatCompletions.Str(reply?["content"]) ?? "")
            : ChatCompletions.ExtractChoice(reply, request.Choices) ?? "";
        if (answer.Length == 0)
        {
            throw new InvalidOperationException(request.Choices is null
                ? "OpenRouter answered nothing"
                : "OpenRouter did not answer with one of the choices");
        }

        var usage = json?["usage"];
        var cost = ChatCompletions.Cost(usage) ?? 0;
        var (input, output) = ChatCompletions.Tokens(usage);
        return new TextAnswer(answer, cost, input, output, Model + " · OpenRouter");
    }

    /// <summary>A model that refused the structured answer once gets plain instructions from then on.</summary>
    volatile bool _schemaBlocked;
}
