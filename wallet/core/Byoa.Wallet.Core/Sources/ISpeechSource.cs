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

    /// <summary>The model is known but not downloaded yet (it downloads on first use).</summary>
    NeedsDownload,

    /// <summary>Downloading now.</summary>
    Downloading,

    /// <summary>No model files found.</summary>
    Missing,

    /// <summary>No API key added.</summary>
    NoKey,

    /// <summary>It failed to load.</summary>
    Broken,

    /// <summary>This source is deliberately not attempted on this processor.</summary>
    Unavailable,
}

/// <summary>The result of one transcription.</summary>
/// <param name="Text">What the source heard.</param>
/// <param name="Cost">What it cost in dollars (0 for a local model).</param>
/// <param name="Detail">A short note for the activity log, e.g. "GPU (Vulkan)".</param>
public sealed record Transcript(string Text, decimal Cost, string? Detail = null);

/// <summary>
/// A source of the skill <c>audio.transcribe</c>. A source is described by properties (languages, where it runs,
/// its approximate speed and cost), and routing works from those properties, not from names.
/// </summary>
public interface ISpeechSource
{
    /// <summary>A stable id used in settings: "gigaam-v3", "whisper", "openrouter-scribe".</summary>
    string Id { get; }

    /// <summary>The name people see.</summary>
    string Name { get; }

    /// <summary><see cref="Runs.ThisDevice"/> or <see cref="Runs.ApiKey"/>.</summary>
    string Runs { get; }

    /// <summary>The provider kind for the gate: "local-model" or "api-key".</summary>
    string ProviderKind { get; }

    /// <summary>Languages it handles well (primary BCP 47 tags); null means many languages.</summary>
    IReadOnlyList<string>? Languages { get; }

    /// <summary>How many languages it knows, for showing ("99").</summary>
    int LanguageCount { get; }

    /// <summary>True when it costs money.</summary>
    bool Paid { get; }

    /// <summary>The typical seconds per short clip: measured on this computer once it has run, a default before.</summary>
    double ApproxSeconds { get; }

    SourceState State { get; }

    /// <summary>One line about its state for people: "ready · GPU (Vulkan)", "not downloaded (874 MB)".</summary>
    string StateText { get; }

    /// <summary>The estimated cost of one clip, before it runs.</summary>
    decimal EstimateCost(double clipSeconds);

    /// <param name="samples">16 kHz mono samples in -1..1.</param>
    /// <param name="language">The primary language tag the page gave, or null.</param>
    Task<Transcript> TranscribeAsync(float[] samples, string? language, CancellationToken ct);
}

/// <summary>A source that has to download its model first.</summary>
public interface IDownloadableSource
{
    /// <summary>0..1 while downloading, null otherwise.</summary>
    double? DownloadProgress { get; }

    /// <summary>The size of the download in bytes, when known.</summary>
    long? DownloadSize { get; }

    /// <summary>Starts the download if it is not running; completes when the model is there (or the download failed).</summary>
    Task EnsureDownloadedAsync(CancellationToken ct);

    /// <summary>Raised when the progress or the state changes.</summary>
    event Action? ProgressChanged;
}

/// <summary>A source that can load its model ahead of the first call, so that the first phrase is as fast as the others.</summary>
public interface IWarmable
{
    /// <summary>Loads the model and runs one short silent clip through it, for <paramref name="language"/> when that matters.</summary>
    Task WarmAsync(string? language);
}
