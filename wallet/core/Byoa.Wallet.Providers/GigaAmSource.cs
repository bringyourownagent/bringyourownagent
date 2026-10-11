using System.Diagnostics;
using Byoa.Wallet.Sources;
using SherpaOnnx;

namespace Byoa.Wallet.Providers;

/// <summary>
/// GigaAM v3 (Russian only) through sherpa-onnx on the CPU, from a model folder already on disk.
/// Very fast and accurate for Russian. The folder is only read.
/// </summary>
public sealed class GigaAmSource : ISpeechSource, IWarmable, IDisposable
{
    public const string SourceId = "gigaam-v3";

    readonly Func<string?> _findFolder;
    readonly object _gate = new();
    OfflineRecognizer? _recognizer;
    string? _loadedFrom;
    string? _error;
    string? _errorFolder;
    double _seconds = 0.2;

    /// <param name="findFolder">Returns the GigaAM folder from the visitor's model folders, or null.</param>
    public GigaAmSource(Func<string?> findFolder) => _findFolder = findFolder;

    public string Id => SourceId;
    public string Name => "GigaAM v3";
    public string Runs => Sources.Runs.ThisDevice;
    public string ProviderKind => "local-model";
    public IReadOnlyList<string>? Languages { get; } = ["ru"];
    public int LanguageCount => 1;
    public bool Paid => false;
    public double ApproxSeconds => _seconds;

    public string? Folder => _loadedFrom ?? _findFolder();

    /// <summary>
    /// Broken after a failed load of the same folder (the route then skips it and falls back); a different folder
    /// (after a rescan or a new model folder) is tried again.
    /// </summary>
    public SourceState State
    {
        get
        {
            var folder = Folder;
            if (folder is null) return SourceState.Missing;
            return _error is not null && _errorFolder == folder ? SourceState.Broken : SourceState.Ready;
        }
    }

    public string StateText => State switch
    {
        SourceState.Broken => "failed to load: " + _error,
        SourceState.Missing => "model folder not found",
        _ => _recognizer is null ? "ready · CPU (loads on first use)" : "ready · CPU",
    };

    public decimal EstimateCost(double clipSeconds) => 0;

    /// <summary>Loads the model ahead of the first call, so the first phrase is as fast as the others.</summary>
    public void Warm()
    {
        try
        {
            lock (_gate)
            {
                if (_recognizer is not null) return;
                var recognizer = Load();
                using var stream = recognizer.CreateStream();
                stream.AcceptWaveform(16000, new float[8000]);
                recognizer.Decode(stream);
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Load() recorded the failure: State is Broken and calls fall back.
        }
    }

    public Task WarmAsync(string? language) => Task.Run(Warm);

    /// <summary>True once the model is loaded.</summary>
    public bool IsWarm => _recognizer is not null;

    OfflineRecognizer Load()
    {
        if (_recognizer is not null) return _recognizer;
        var folder = _findFolder() ?? throw new InvalidOperationException("GigaAM model folder not found");
        if (_error is not null && _errorFolder == folder) throw new InvalidOperationException("GigaAM failed to load: " + _error);
        try
        {
            return Create(folder);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _error = e.Message;
            _errorFolder = folder;
            throw new InvalidOperationException("GigaAM failed to load: " + e.Message, e);
        }
    }

    OfflineRecognizer Create(string folder)
    {
        var files = GigaAmFiles.Find(folder) ?? throw new InvalidOperationException("GigaAM model files missing");
        var config = new OfflineRecognizerConfig();
        config.ModelConfig.Transducer.Encoder = files.Encoder;
        config.ModelConfig.Transducer.Decoder = files.Decoder;
        config.ModelConfig.Transducer.Joiner = files.Joiner;
        config.ModelConfig.Tokens = files.Tokens;
        config.ModelConfig.ModelType = "nemo_transducer";
        config.ModelConfig.NumThreads = Math.Clamp(Environment.ProcessorCount / 2, 2, 6);
        config.ModelConfig.Provider = "cpu";
        config.DecodingMethod = "greedy_search";
        _recognizer = new OfflineRecognizer(config);
        _loadedFrom = folder;
        _error = null;
        _errorFolder = null;
        return _recognizer;
    }

    public Task<Transcript> TranscribeAsync(float[] samples, string? language, CancellationToken ct)
    {
        if (language is not null && language != "ru") throw new InvalidOperationException("GigaAM knows only Russian");
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            lock (_gate)
            {
                var recognizer = Load();
                var watch = Stopwatch.StartNew();
                using var stream = recognizer.CreateStream();
                stream.AcceptWaveform(16000, samples);
                recognizer.Decode(stream);
                var text = stream.Result.Text.Trim();
                _seconds = _seconds * 0.7 + watch.Elapsed.TotalSeconds * 0.3;
                return new Transcript(text, 0, "CPU");
            }
        }, ct);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _recognizer?.Dispose();
            _recognizer = null;
        }
    }
}
