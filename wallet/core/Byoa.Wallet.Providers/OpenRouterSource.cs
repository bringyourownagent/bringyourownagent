using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Byoa.Wallet.Audio;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Providers;

/// <summary>
/// ElevenLabs Scribe v2 through OpenRouter (POST /audio/transcriptions), with the visitor's own API key.
/// Paid: by default it is the last fallback and asks before it runs.
/// </summary>
public sealed class OpenRouterSource : ISpeechSource
{
    public const string SourceId = "openrouter-scribe";
    public const string SecretName = "openrouter";
    public const string Model = "elevenlabs/scribe-v2";

    /// <summary>The estimate per second of audio, a little above OpenRouter's listed price; the real cost comes from the response.</summary>
    public const decimal PricePerSecond = 0.00004m;

    readonly HttpClient _http;
    readonly Func<string?> _key;
    readonly Func<bool> _hasKey;
    readonly Uri _baseUrl;
    double _seconds = 1.5;
    string? _lastError;

    /// <param name="key">Returns the API key when a request needs it (decrypted at that moment only).</param>
    /// <param name="hasKey">Whether a key is stored, without decrypting it for display.</param>
    public OpenRouterSource(HttpClient http, Func<string?> key, Func<bool> hasKey, Uri? baseUrl = null)
    {
        _http = http;
        _key = key;
        _hasKey = hasKey;
        _baseUrl = baseUrl ?? new Uri("https://openrouter.ai/api/v1/");
    }

    public string Id => SourceId;
    public string Name => "Scribe v2 (OpenRouter)";
    public string Runs => Sources.Runs.ApiKey;
    public string ProviderKind => "api-key";
    public IReadOnlyList<string>? Languages => null;
    public int LanguageCount => 99;
    public bool Paid => true;
    public double ApproxSeconds => _seconds;
    public SourceState State => _hasKey() ? SourceState.Ready : SourceState.NoKey;

    public string StateText => State == SourceState.NoKey ? "no API key added"
        : _lastError is null ? "key added · paid, about " + Money.Show(EstimateCost(5)) + " for a 5 s clip"
        : "key added · last call failed: " + _lastError;

    public decimal EstimateCost(double clipSeconds) => Math.Max(0.000001m, (decimal)Math.Ceiling(clipSeconds) * PricePerSecond);

    public async Task<Transcript> TranscribeAsync(float[] samples, string? language, CancellationToken ct)
    {
        var key = _key();
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("no OpenRouter key");
        var body = new JsonObject
        {
            ["model"] = Model,
            ["input_audio"] = new JsonObject
            {
                ["data"] = Convert.ToBase64String(Wav.Encode(samples, Wav.TargetRate)),
                ["format"] = "wav",
            },
        };
        if (language is not null) body["language"] = language;

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUrl, "audio/transcriptions"))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Headers.Add("X-Title", "BYOA agent wallet");
        key = null;

        var watch = Stopwatch.StartNew();
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // The body may echo the request; only the status goes into the error.
            _lastError = "HTTP " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture);
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
        _seconds = _seconds * 0.7 + watch.Elapsed.TotalSeconds * 0.3;
        var seconds = samples.Length / (double)Wav.TargetRate;
        var usage = json?["usage"];
        decimal cost = Number(usage?["cost"]) ?? (decimal)Math.Ceiling(seconds) * PricePerSecond;
        return new Transcript(json?["text"]?.GetValue<string>()?.Trim() ?? "", cost, "OpenRouter");
    }

    static decimal? Number(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<decimal>(out var d)) return d;
        if (v.TryGetValue<double>(out var f) && double.IsFinite(f)) return (decimal)f;
        if (v.TryGetValue<string>(out var s) && decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p)) return p;
        return null;
    }
}
