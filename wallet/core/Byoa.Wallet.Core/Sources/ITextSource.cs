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

/// <summary>One message of a text.generate call, as the page sent it.</summary>
/// <param name="Role">"system", "user" or "assistant".</param>
public sealed record ChatMessage(string Role, string Content);

/// <summary>One text.generate call after the wallet has checked it.</summary>
/// <param name="MaxTokens">The most tokens to write (the page's maxTokens, capped by the wallet).</param>
/// <param name="Temperature">0..1.</param>
/// <param name="Language">The primary language tag the page gave, or null.</param>
/// <param name="Choices">When set, the answer must be exactly one of these strings (the wallet constrains or normalises it).</param>
public sealed record TextRequest(
    IReadOnlyList<ChatMessage> Messages,
    int MaxTokens,
    double Temperature,
    string? Language,
    IReadOnlyList<string>? Choices);

/// <summary>The result of one text.generate call.</summary>
/// <param name="Text">The answer only: never the model's hidden reasoning.</param>
/// <param name="Cost">What it cost in dollars (0 for a local model).</param>
/// <param name="Detail">A short note for the activity log, e.g. "LM Studio · qwen3.5-4b".</param>
public sealed record TextAnswer(string Text, decimal Cost, int InputTokens, int OutputTokens, string? Detail = null);

/// <summary>A source of the skill <c>text.generate</c>: a model in a local runtime or behind an API key.</summary>
public interface ITextSource
{
    /// <summary>A stable id used in settings and chains, e.g. "lmstudio:qwen/qwen3.5-4b", "openrouter:google/gemma-3-12b-it".</summary>
    string Id { get; }

    /// <summary>The name people see.</summary>
    string Name { get; }

    /// <summary><see cref="Runs.ThisDevice"/> or <see cref="Runs.ApiKey"/>.</summary>
    string Runs { get; }

    /// <summary>The provider kind for the gate: "local-model" or "api-key".</summary>
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

/// <summary>A local runtime found on this computer.</summary>
/// <param name="Kind">One of <see cref="SourceKinds"/>.</param>
/// <param name="Endpoint">Its base URL.</param>
/// <param name="Running">Whether it answered just now.</param>
public sealed record RuntimeInfo(string Kind, string Endpoint, bool Running, IReadOnlyList<RuntimeModel> Models);

/// <summary>A model a runtime offers.</summary>
/// <param name="Id">The runtime's own model id ("qwen/qwen3.5-4b", "llama3.2:3b").</param>
/// <param name="Loaded">Whether it is in memory now.</param>
public sealed record RuntimeModel(string Id, string? Name, long? SizeBytes, bool Loaded);

/// <summary>Finds local runtimes (LM Studio, Ollama, other OpenAI-compatible servers) and the models they offer.</summary>
public interface IRuntimeScanner
{
    Task<IReadOnlyList<RuntimeInfo>> ScanAsync(CancellationToken ct);
}
