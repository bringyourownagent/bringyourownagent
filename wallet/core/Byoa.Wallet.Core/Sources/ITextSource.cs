namespace Byoa.Wallet.Sources;

// The shared contract for the skill text.generate and for sources the visitor adds himself (a model in LM Studio or
// Ollama, a model through an API key). Interfaces only: the text sources, the scanner of local runtimes, the
// management of sources (window, MCP) are built against these types. See spec: text.generate in 0.1.

/// <summary>The kinds of sources a visitor (or his agent) can add. Built-in speech sources have no kind.</summary>
public static class SourceKinds
{
    /// <summary>LM Studio's OpenAI-compatible server, by default http://127.0.0.1:1234/v1.</summary>
    public const string LmStudio = "lmstudio";

    /// <summary>Ollama, by default http://127.0.0.1:11434.</summary>
    public const string Ollama = "ollama";

    /// <summary>Any other OpenAI-compatible server on this computer (llama.cpp server, Jan, vLLM...).</summary>
    public const string OpenAiCompatible = "openai-compatible";

    /// <summary>OpenRouter with the visitor's own API key (paid).</summary>
    public const string OpenRouter = "openrouter";
}

/// <summary>
/// The local model apps the wallet knows by name, so that the window and its messages can say which one they mean.
/// None of them is a source kind of its own: LM Studio and Ollama have kinds, Llama is an OpenAI-compatible server.
/// </summary>
public static class LocalRuntimes
{
    /// <summary>The port the Llama app (llama.cpp team and Hugging Face, https://llama.app) serves on.</summary>
    public const int LlamaPort = 9931;

    /// <summary>The Llama app's OpenAI-compatible address.</summary>
    public const string LlamaEndpoint = "http://127.0.0.1:9931/v1";

    /// <summary>Where a visitor who has no local model app yet can get Llama (free, open source).</summary>
    public const string LlamaPage = "https://llama.app";

    /// <summary>Whether an endpoint is the Llama app's port on this computer.</summary>
    public static bool IsLlama(string? endpoint) =>
        Uri.TryCreate(endpoint?.Trim(), UriKind.Absolute, out var uri) && uri.IsLoopback && uri.Port == LlamaPort;

    /// <summary>The name a person knows the runtime by.</summary>
    public static string Name(string kind, string? endpoint) => kind switch
    {
        SourceKinds.LmStudio => "LM Studio",
        SourceKinds.Ollama => "Ollama",
        SourceKinds.OpenAiCompatible when IsLlama(endpoint) => "Llama",
        _ => "OpenAI-compatible server",
    };
}

/// <summary>One message of a text.generate call, as the page sent it.</summary>
/// <param name="Role">"system", "user" or "assistant".</param>
public sealed record ChatMessage(string Role, string Content);

/// <summary>One text.generate call after the wallet has checked it.</summary>
/// <param name="MaxTokens">The most tokens to write (the page's maxTokens, capped by the wallet).</param>
/// <param name="Temperature">0..1.</param>
/// <param name="Language">The primary language tag the page gave, or null.</param>
/// <param name="Choices">When set, the answer must be exactly one of these strings (the wallet constrains or normalises it).</param>
/// <param name="Thinking">False when the model must answer straight away, without reasoning first: the page asked for it (<c>params.thinking: false</c>)
/// or the visitor switched thinking off for the source. A source that cannot switch reasoning off ignores it.</param>
/// <param name="ThinkingBudget">The most tokens the model may spend on reasoning before it must answer (the visitor's limit for the source);
/// 0 means no limit. The answer keeps the room the page asked for in <paramref name="MaxTokens"/>. A source that cannot limit reasoning ignores it.</param>
public sealed record TextRequest(
    IReadOnlyList<ChatMessage> Messages,
    int MaxTokens,
    double Temperature,
    string? Language,
    IReadOnlyList<string>? Choices,
    bool Thinking = true,
    int ThinkingBudget = 0);

/// <summary>The result of one text.generate call.</summary>
/// <param name="Text">The answer only: never the model's hidden reasoning.</param>
/// <param name="Cost">What it cost in dollars (0 for a local model).</param>
/// <param name="Detail">A short note for the activity log, e.g. "LM Studio · qwen3.5-4b".</param>
public sealed record TextAnswer(string Text, decimal Cost, int InputTokens, int OutputTokens, string? Detail = null);

/// <summary>
/// A text provider was called but could not produce a usable answer. A provider may still know what that attempt
/// cost (for example, an empty or choice-mismatched paid response). The engine settles that cost before trying a
/// fallback; a null cost means only the reservation is returned.
/// </summary>
public sealed class TextSourceException : InvalidOperationException
{
    public TextSourceException(string message, decimal? cost = null, bool providerCalled = true,
        bool responseReceived = false, Exception? inner = null)
        : base(message, inner)
    {
        Cost = cost;
        ProviderCalled = providerCalled;
        ResponseReceived = responseReceived;
    }

    public decimal? Cost { get; }
    public bool ProviderCalled { get; }

    /// <summary>True when an HTTP/provider response was received, even if it contained no usable answer or cost.</summary>
    public bool ResponseReceived { get; }
}

/// <summary>A source of the skill <c>text.generate</c>: a model in a local runtime or behind an API key.</summary>
public interface ITextSource
{
    /// <summary>A stable id used in settings and chains, e.g. "lmstudio:qwen/qwen3.5-4b", "openrouter:google/gemma-3-12b-it".</summary>
    string Id { get; }

    /// <summary>The name people see.</summary>
    string Name { get; }

    /// <summary><see cref="Runs.ThisDevice"/>, a home server, or <see cref="Runs.ApiKey"/>.</summary>
    string Runs { get; }

    /// <summary>The provider kind for the gate: "local-model", "home-server", or "api-key".</summary>
    string ProviderKind { get; }

    /// <summary>Languages it handles well (primary BCP 47 tags); null means many languages.</summary>
    IReadOnlyList<string>? Languages { get; }

    bool Paid { get; }

    SourceState State { get; }

    /// <summary>One line about its state for people: "ready · LM Studio", "LM Studio is not running".</summary>
    string StateText { get; }

    /// <summary>The estimated cost of one call, before it runs.</summary>
    decimal EstimateCost(TextRequest request);

    Task<TextAnswer> GenerateAsync(TextRequest request, CancellationToken ct);
}

/// <summary>
/// A text source that can hand an answer over as it is written. The wallet uses it for a call that asked to stream (over the direct link): the
/// pieces go to the site as they arrive, and the answer that comes back is the whole text, with the model's reasoning left out.
/// </summary>
public interface IStreamingTextSource : ITextSource
{
    /// <summary>Like <see cref="ITextSource.GenerateAsync"/>; <paramref name="onText"/> gets each piece of visible text as it arrives.</summary>
    Task<TextAnswer> GenerateStreamAsync(TextRequest request, Action<string> onText, CancellationToken ct);
}

/// <summary>A local text source that can be made to write one short answer before the first page call.</summary>
public interface ITextSourceWarmable
{
    /// <summary>Has the model write one short answer (which loads it). The wallet never uses this for a paid source.</summary>
    Task WarmAsync(string? language, CancellationToken ct);
}

/// <summary>A local runtime found on this computer.</summary>
/// <param name="Kind">One of <see cref="SourceKinds"/>.</param>
/// <param name="Endpoint">Its base URL.</param>
/// <param name="Running">Whether it answered just now.</param>
public sealed record RuntimeInfo(string Kind, string Endpoint, bool Running, IReadOnlyList<RuntimeModel> Models,
    long? LatencyMs = null, int? HttpStatus = null);

/// <summary>A model a runtime offers.</summary>
/// <param name="Id">The runtime's own model id ("qwen/qwen3.5-4b", "llama3.2:3b").</param>
/// <param name="Loaded">Whether it is in memory now (only meaningful when <paramref name="LoadedKnown"/>).</param>
/// <param name="LoadedKnown">True when the runtime says what is in memory (LM Studio, Ollama, the Llama app); false for a plain model list that says nothing about it.</param>
public sealed record RuntimeModel(string Id, string? Name, long? SizeBytes, bool Loaded, bool LoadedKnown = false);

/// <summary>
/// A source whose runtime can say whether its model is in memory right now, so that the wallet never has to start the model
/// ("Never start a model for me" in Sources): a model that is not loaded is left out of the route, and nothing is sent to it.
/// </summary>
public interface ILoadCheck
{
    /// <summary>The scanner's last answer: true in memory, false not loaded, null the runtime does not say (or was not asked yet).</summary>
    bool? Loaded { get; }

    /// <summary>Asks the runtime now, with a small request that cannot start a model: true loaded (or loading), false not loaded, null unknown.</summary>
    Task<bool?> LoadedNowAsync(CancellationToken ct);
}

/// <summary>Finds local runtimes (LM Studio, Ollama, other OpenAI-compatible servers) and the models they offer.</summary>
public interface IRuntimeScanner
{
    Task<IReadOnlyList<RuntimeInfo>> ScanAsync(CancellationToken ct);
}
