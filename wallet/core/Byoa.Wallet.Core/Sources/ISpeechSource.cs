namespace Byoa.Wallet.Sources;

/// <summary>Where a source runs, in the words of the vision's property vocabulary (<c>runs</c>).</summary>
public static class Runs
{
    public const string ThisDevice = "this-device";
    public const string ApiKey = "api-key";
}

/// <summary>Whether a source can answer right now.</summary>
public enum SourceState
{
    Ready,

    /// <summary>The runtime does not offer the model any more.</summary>
    Missing,

    /// <summary>No API key added.</summary>
    NoKey,

    /// <summary>It failed to load.</summary>
    Broken,

    /// <summary>The wallet refuses to use it right now (for example, an endpoint that is not allowed); the state text says why.</summary>
    Unavailable,
}

/// <summary>The result of one transcription.</summary>
/// <param name="Text">What the source heard.</param>
/// <param name="Cost">What it cost in dollars (0 for a local model).</param>
/// <param name="Detail">A short note for the activity log, e.g. "Llama · Qwen3-ASR · 0.2 s".</param>
public sealed record Transcript(string Text, decimal Cost, string? Detail = null);

/// <summary>
/// A source of the skill <c>audio.transcribe</c>: a speech model in a program on this computer (or a home server), or a
/// paid API behind the visitor's key. The wallet itself holds no model. A source is described by properties
/// (languages, where it runs, its approximate speed and cost), and routing works from those properties, not from names.
/// </summary>
public interface ISpeechSource
{
    /// <summary>A stable id used in settings and chains: "openrouter-scribe", "openai-compatible:qwen3-asr@http://127.0.0.1:9931/v1#transcribe".</summary>
    string Id { get; }

    /// <summary>The name people see.</summary>
    string Name { get; }

    /// <summary><see cref="Runs.ThisDevice"/> or <see cref="Runs.ApiKey"/>.</summary>
    string Runs { get; }

    /// <summary>The provider kind for the gate: "local-model" or "api-key".</summary>
    string ProviderKind { get; }

    /// <summary>Languages it handles well (primary BCP 47 tags); null means many languages.</summary>
    IReadOnlyList<string>? Languages { get; }

    /// <summary>How many languages it was set up for, for showing; 0 when it takes any language.</summary>
    int LanguageCount { get; }

    /// <summary>True when it costs money.</summary>
    bool Paid { get; }

    /// <summary>The typical seconds per short clip: measured once it has run, a default before.</summary>
    double ApproxSeconds { get; }

    /// <summary>True only after this wallet has measured a real clip on this computer.</summary>
    bool HasMeasuredSeconds => false;

    SourceState State { get; }

    /// <summary>One line about its state for people: "ready · Llama", "Llama is not running".</summary>
    string StateText { get; }

    /// <summary>The estimated cost of one clip, before it runs.</summary>
    decimal EstimateCost(double clipSeconds);

    /// <param name="samples">16 kHz mono samples in -1..1.</param>
    /// <param name="language">The primary language tag the page gave, or null.</param>
    Task<Transcript> TranscribeAsync(float[] samples, string? language, CancellationToken ct);
}

/// <summary>
/// A source that can be made to do its simplest job once for real when a site connects, so that the first real request is as fast as
/// the others. For speech that job is transcribing a clip of 3 seconds (silence: nothing is kept from the answer). A source of another
/// skill does its own simplest job: text writes one short answer; an image model would draw the smallest image it can.
/// </summary>
public interface IWarmable
{
    /// <summary>Runs the simplest real job through the model: a 3 second clip, for <paramref name="language"/> when that matters.</summary>
    Task WarmAsync(string? language);
}
