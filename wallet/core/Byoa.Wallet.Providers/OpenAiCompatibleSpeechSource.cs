using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Byoa.Wallet.Audio;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Providers;

/// <summary>
/// A speech model in an OpenAI-compatible runtime on this computer or on a home server: the Llama app (llama.cpp),
/// LM Studio, or any server that takes audio in a chat request. The clip goes to <c>/chat/completions</c> as
/// <c>input_audio</c> and the model's answer is the text. The wallet holds no model: it only hands the clip over, and
/// the runtime must have a speech model loaded (Qwen3-ASR on Llama is the one we recommend).
/// </summary>
public sealed class OpenAiCompatibleSpeechSource : ISpeechSource, ILiveness, IWarmable
{
    /// <summary>Room for the answer of one clip: a few tokens per second of speech, never unbounded.</summary>
    const int MaxAnswerTokens = 2048;

    /// <summary>The tag a speech model such as Qwen3-ASR puts in front of the text: "language Russian&lt;asr_text&gt;".</summary>
    static readonly Regex AsrTag = new(@"^\s*language\s+[^<\s]*\s*<asr_text>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>A hidden-reasoning block some runtimes leave in the answer; what was heard never starts with one.</summary>
    static readonly Regex Think = new(@"<think>.*?</think>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    readonly HttpClient _http;
    readonly string _kind;
    readonly Uri _base;
    readonly bool _loopback;
    readonly string _displayModel;
    readonly IReadOnlyList<string>? _languages;
    readonly SemaphoreSlim _one = new(1, 1);
    double _seconds = 1.0;
    bool _hasMeasuredSeconds;
    string? _lastError;

    /// <param name="endpoint">The runtime's base URL from the settings; null means the kind's default.</param>
    /// <param name="languages">The primary languages the visitor set it up for; empty means any.</param>
    public OpenAiCompatibleSpeechSource(string kind, string model, string? label, string? endpoint, IReadOnlyList<string>? languages, HttpClient http)
    {
        _kind = kind;
        Model = model;
        _displayModel = SourceLabels.Clean(model) ?? "model";
        _http = http;
        var address = endpoint?.Trim() is { Length: > 0 } given ? given : OpenAiCompatibleSource.DefaultEndpoint(kind);
        if (kind.Equals(SourceKinds.Ollama, StringComparison.OrdinalIgnoreCase) &&
            !address.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            address = address.TrimEnd('/') + "/v1";
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(uri.Host))
            throw new ArgumentException("The endpoint must be an absolute http or https URL.", nameof(endpoint));
        _loopback = OpenAiCompatibleSource.IsLoopback(uri.Host);
        _base = new Uri(address.TrimEnd('/') + "/", UriKind.Absolute);
        Label = SourceLabels.Clean(label) ?? _displayModel;
        _languages = languages is { Count: > 0 } ? languages.ToArray() : null;
    }

    public string Model { get; }
    public string? Label { get; }
    public string Endpoint => _base.ToString().TrimEnd('/');

    // The endpoint is part of the identity, and the suffix keeps it apart from a text source of the same model.
    public string Id => OpenAiCompatibleSource.MakeId(_kind, Model, Endpoint, Vocabulary.Transcribe);

    public string RuntimeName => LocalRuntimes.Name(_kind, Endpoint);
    public string Name => (Label ?? Model) + " · " + (_loopback ? "this computer" : _base.IdnHost);
    public string Runs => _loopback ? Sources.Runs.ThisDevice : "another computer (" + _base.IdnHost + ")";
    public string ProviderKind => _loopback ? "local-model" : "home-server";
    public IReadOnlyList<string>? Languages => _languages;
    public int LanguageCount => _languages?.Count ?? 0;
    public bool Paid => false;
    public double ApproxSeconds => _seconds;
    public bool HasMeasuredSeconds => _hasMeasuredSeconds;

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
        : _lastError is not null ? "last call failed: " + _lastError
        : !_loopback && Up == true ? "ready on another computer (" + _base.IdnHost + ")"
        : Up == true ? "ready · " + RuntimeName
        : "ready when " + RuntimeName + " runs";

    internal void MarkUnavailable(string reason) => UnavailableReason = reason;
    internal void MarkAvailable() => UnavailableReason = null;

    public decimal EstimateCost(double clipSeconds) => 0;

    /// <summary>
    /// Makes the model do its simplest job for real: transcribe a clip of 3 seconds (silence), so the runtime has loaded it and the
    /// first real phrase is as quick as the others. Nothing is kept from the answer, and this first (slow) run is not counted in the
    /// speed the wallet measures.
    /// </summary>
    public async Task WarmAsync(string? language)
    {
        if (string.IsNullOrWhiteSpace(Model)) throw new InvalidOperationException("no model chosen in the settings");
        if (!_loopback) return;
        await _one.WaitAsync().ConfigureAwait(false);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            await ChatAsync(new float[Wav.TargetRate * 3], language, cts.Token).ConfigureAwait(false);
        }
        finally
        {
            _one.Release();
        }
    }

    public async Task<Transcript> TranscribeAsync(float[] samples, string? language, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Model)) throw new InvalidOperationException("no model chosen in the settings");
        // A local runtime answers one request at a time; a second clip waits instead of failing.
        await _one.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var watch = Stopwatch.StartNew();
            var json = await ChatAsync(samples, language, ct).ConfigureAwait(false);
            var text = Clean(ChatCompletions.Str(ChatCompletions.First(json)?["message"]?["content"]) ?? "");
            _lastError = null;
            var took = watch.Elapsed.TotalSeconds;
            _seconds = _hasMeasuredSeconds ? _seconds * 0.7 + took * 0.3 : took;
            _hasMeasuredSeconds = true;
            return new Transcript(text, 0, RuntimeName + " · " + _displayModel + " · " + took.ToString("0.0", CultureInfo.InvariantCulture) + " s");
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>The answer without the tag some speech models put in front ("language Russian&lt;asr_text&gt;").</summary>
    internal static string Clean(string answer)
    {
        return AsrTag.Replace(Think.Replace(answer, "").Trim(), "").Trim();
    }

    async Task<JsonNode?> ChatAsync(float[] samples, string? language, CancellationToken ct)
    {
        var seconds = samples.Length / (double)Wav.TargetRate;
        var parts = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "input_audio",
                ["input_audio"] = new JsonObject
                {
                    ["data"] = Convert.ToBase64String(Wav.Encode(samples, Wav.TargetRate)),
                    ["format"] = "wav",
                },
            },
        };
        if (Instruction(language) is { } instruction) parts.Add(new JsonObject { ["type"] = "text", ["text"] = instruction });
        var body = new JsonObject
        {
            ["model"] = Model,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = parts }),
            ["temperature"] = 0,
            ["max_tokens"] = Math.Clamp((int)Math.Ceiling(seconds * 16) + 64, 64, MaxAnswerTokens),
            ["stream"] = false,
            ["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = false },
        };

        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(_base, "chat/completions"))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException e)
        {
            _lastError = "no answer from " + RuntimeName;
            throw new HttpRequestException(RuntimeName + " did not answer: " + e.Message, e);
        }

        using (response)
        {
            var text = await BoundedHttp.ReadJsonAsync(response, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var reason = ErrorOf(text);
                _lastError = "HTTP " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture);
                throw new HttpRequestException(RuntimeName + " answered HTTP " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)
                    + (reason is null ? "" : ": " + reason));
            }

            try
            {
                return JsonNode.Parse(text);
            }
            catch (JsonException)
            {
                _lastError = "not JSON";
                throw new HttpRequestException(RuntimeName + " answered something that is not JSON");
            }
        }
    }

    /// <summary>The runtime's own explanation ("audio input is not supported"), one short line.</summary>
    static string? ErrorOf(string body)
    {
        try
        {
            var node = JsonNode.Parse(body)?["error"];
            var message = ChatCompletions.Str(node?["message"]) ?? ChatCompletions.Str(node);
            if (string.IsNullOrWhiteSpace(message)) return null;
            // One plain line: whatever control characters a program puts in its error never reach a window or an agent.
            message = string.Join(' ', new string(message.Select(c => char.IsControl(c) ? ' ' : c).ToArray())
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            return message.Length > 200 ? message[..200] : message;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// What to ask next to the audio: the language when the page gave one (so the model neither translates nor guesses),
    /// and, for Russian and the other Cyrillic languages, the script.
    /// </summary>
    internal static string? Instruction(string? language)
    {
        if (language is null) return "Transcribe this audio exactly as spoken, in the language of the speaker. Output only the transcription, nothing else.";
        var name = Vocabulary.LanguageName(language);
        var script = language is "ru" or "uk" or "be" ? " (write it in Cyrillic)" : "";
        return $"The audio is in {name}{script}. Transcribe it word for word in {name}, exactly as spoken. Do not translate. Output only the transcription, nothing else.";
    }
}
