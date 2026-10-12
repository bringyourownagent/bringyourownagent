using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Byoa.Wallet.Protocol;
using Byoa.Wallet.Sources;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace Byoa.Wallet.Providers;

/// <summary>
/// Whisper (multilingual) through Whisper.net / whisper.cpp. The ggml model downloads into
/// <c>%LOCALAPPDATA%\BYOA\models\whisper</c> on first use, with progress shown. It runs on the GPU through Vulkan
/// when the driver offers it and falls back to the CPU on its own. The CPU-only fallback (<see cref="WhisperCpuSource"/>) is the same engine with the GPU switched off.
/// </summary>
public class WhisperSource : ISpeechSource, IDownloadableSource, IWarmable, IDisposable
{
    public const string SourceId = "whisper";
    const string BaseUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/";
    const string TreeUrl = "https://huggingface.co/api/models/ggerganov/whisper.cpp/tree/main";

    /// <summary>Approximate sizes of the models offered, for showing before the download starts.</summary>
    static readonly Dictionary<string, long> KnownSizes = new(StringComparer.Ordinal)
    {
        ["ggml-large-v3-q5_0.bin"] = 1_081_140_203,
        ["ggml-large-v3-turbo-q8_0.bin"] = 874_188_075,
        ["ggml-large-v3-turbo-q5_0.bin"] = 574_041_195,
        ["ggml-large-v3-turbo.bin"] = 1_624_555_275,
        ["ggml-medium.bin"] = 1_533_763_059,
        ["ggml-small.bin"] = 487_601_967,
        ["ggml-small-q5_1.bin"] = 190_085_487,
        ["ggml-base-q5_1.bin"] = 59_707_625,
    };

    static int _gpuRuntimeConfigured;
    static int _cpuRuntimeConfigured;

    readonly Func<string> _modelFile;
    readonly Func<IReadOnlyList<FoundModel>> _found;
    readonly HttpClient _http;
    readonly SemaphoreSlim _gate = new(1, 1);
    readonly Dictionary<string, WhisperProcessor> _processors = new(StringComparer.Ordinal);
    readonly object _downloadGate = new();
    WhisperFactory? _factory;
    string? _factoryPath;
    Task? _download;
    double? _progress;
    long? _size;
    string? _error;
    string? _runtime;
    double _seconds = 0.8;

    /// <param name="modelFile">The ggml file name chosen in the settings.</param>
    /// <param name="found">The models found in the visitor's model folders.</param>
    public WhisperSource(Func<string> modelFile, Func<IReadOnlyList<FoundModel>> found, HttpClient http)
    {
        _modelFile = modelFile;
        _found = found;
        _http = http;
    }

    public event Action? ProgressChanged;

    /// <summary>Beam search width; 0 or 1 is greedy decoding (the default: on single Slovak words beam 5 was no better and 1.5x slower).</summary>
    public int BeamSize { get; set; } = 1;

    public virtual string Id => SourceId;
    public virtual string Name => "Whisper " + ModelLabel(_modelFile());
    public string Runs => Sources.Runs.ThisDevice;
    public string ProviderKind => "local-model";
    public IReadOnlyList<string>? Languages => null;
    public int LanguageCount => Vocabulary.WhisperLanguages.Length;
    public bool Paid => false;
    public double ApproxSeconds => _seconds;
    public double? DownloadProgress => _progress;
    public long? DownloadSize => _size ?? (KnownSizes.TryGetValue(_modelFile(), out var s) ? s : null);

    /// <summary>"GPU, Vulkan" or "CPU" once loaded.</summary>
    public string? Runtime => _runtime;

    /// <summary>The folder the wallet downloads its ggml file into; the CPU source keeps its own copy in the wallet home.</summary>
    protected virtual string ModelDir => Path.Combine(WalletPaths.Models, "whisper");

    /// <summary>The model file: in the wallet's own models folder, or found in one of the visitor's model folders.</summary>
    public string? ModelPath
    {
        get
        {
            var file = _modelFile();
            var own = Path.Combine(ModelDir, file);
            if (File.Exists(own)) return own;
            return _found().FirstOrDefault(m => m.Kind == "ggml" && Path.GetFileName(m.Path) == file)?.Path;
        }
    }

    public SourceState State =>
        _download is { IsCompleted: false } ? SourceState.Downloading
        : _error is not null ? SourceState.Broken
        : ModelPath is null ? SourceState.NeedsDownload
        : SourceState.Ready;

    public string StateText => State switch
    {
        SourceState.Downloading => "downloading " + (_progress ?? 0).ToString("P0", CultureInfo.InvariantCulture),
        SourceState.Broken => "failed: " + _error,
        SourceState.NeedsDownload => "not downloaded yet (" + Mb(DownloadSize) + "), downloads on first use",
        _ => _runtime is null ? "ready (loads on first use)" : "ready · " + _runtime,
    };

    static string Mb(long? bytes) => bytes is null ? "size unknown" : (bytes.Value / 1_000_000).ToString(CultureInfo.InvariantCulture) + " MB";

    public static string ModelLabel(string file) =>
        Path.GetFileNameWithoutExtension(file).Replace("ggml-", "", StringComparison.Ordinal);

    public decimal EstimateCost(double clipSeconds) => 0;

    // ------------------------------------------------------------------------------------------------
    // download

    public Task EnsureDownloadedAsync(CancellationToken ct)
    {
        if (ModelPath is not null) return Task.CompletedTask;
        lock (_downloadGate)
        {
            if (_download is null || _download.IsCompleted)
            {
                _error = null;
                _download = Task.Run(() => DownloadAsync(_modelFile()), CancellationToken.None);
            }

            return _download.WaitAsync(ct);
        }
    }

    async Task DownloadAsync(string file)
    {
        try
        {
            var (expectedSize, expectedSha) = await ExpectedAsync(file).ConfigureAwait(false);
            var dir = ModelDir;
            Directory.CreateDirectory(dir);
            var target = Path.Combine(dir, file);
            var part = target + ".part";
            using var response = await _http.GetAsync(BaseUrl + file, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? expectedSize;
            _size = total;
            _progress = 0;
            ProgressChanged?.Invoke();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long done = 0;
            var lastShown = -1;
            await using (var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
            await using (var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
            {
                var buffer = new byte[1 << 20];
                int n;
                while ((n = await source.ReadAsync(buffer).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, n)).ConfigureAwait(false);
                    hash.AppendData(buffer, 0, n);
                    done += n;
                    if (total is > 0)
                    {
                        _progress = (double)done / total.Value;
                        var percent = (int)(_progress * 100);
                        if (percent != lastShown)
                        {
                            lastShown = percent;
                            ProgressChanged?.Invoke();
                        }
                    }
                }
            }

            if (expectedSize is { } size && size != done) throw new IOException($"downloaded {done} bytes, expected {size}");
            var sha = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (expectedSha is not null && sha != expectedSha) throw new IOException("the download does not match its SHA-256");
            File.Move(part, target, overwrite: false);
            _progress = null;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException or UnauthorizedAccessException)
        {
            _error = "download: " + e.Message;
            _progress = null;
        }
        finally
        {
            ProgressChanged?.Invoke();
        }
    }

    /// <summary>The size and SHA-256 Hugging Face publishes for the file (null when it cannot be read).</summary>
    async Task<(long? Size, string? Sha)> ExpectedAsync(string file)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var json = await _http.GetStringAsync(TreeUrl, cts.Token).ConfigureAwait(false);
            if (JsonNode.Parse(json) is JsonArray items)
            {
                foreach (var item in items)
                {
                    if (item?["path"]?.GetValue<string>() != file) continue;
                    var lfs = item["lfs"];
                    return (lfs?["size"]?.GetValue<long>() ?? item["size"]?.GetValue<long>(), lfs?["oid"]?.GetValue<string>());
                }
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
        {
            // verify by size from the response only
        }

        return (null, null);
    }

    // ------------------------------------------------------------------------------------------------
    // transcription

    static void ConfigureRuntime(bool useGpu)
    {
        if (Interlocked.Exchange(ref useGpu ? ref _gpuRuntimeConfigured : ref _cpuRuntimeConfigured, 1) == 1) return;
        // GPU through Vulkan first; the CPU build is the automatic fallback when Vulkan cannot load. A source that
        // runs without the GPU never loads Vulkan at all: on some machines its driver breaks the CPU run instead.
        RuntimeOptions.RuntimeLibraryOrder = useGpu
            ? [RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx]
            : [RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx];
    }

    /// <summary>Whether this source may use the GPU; the CPU source loads without it.</summary>
    protected virtual bool UseGpu => true;

    /// <summary>The note the state shows once the model is loaded: which runtime answered.</summary>
    protected virtual string? RuntimeLabel() => RuntimeOptions.LoadedLibrary switch
    {
        RuntimeLibrary.Vulkan => "GPU, Vulkan",
        RuntimeLibrary.Cuda or RuntimeLibrary.Cuda12 => "GPU, CUDA",
        null => null,
        var other => other.ToString(),
    };

    WhisperFactory Factory()
    {
        var path = ModelPath ?? throw new InvalidOperationException("the Whisper model is not downloaded yet");
        if (_factory is not null && _factoryPath == path) return _factory;
        DisposeFactory();
        ConfigureRuntime(UseGpu);
        _factory = WhisperFactory.FromPath(path, new WhisperFactoryOptions { UseGpu = UseGpu });
        _factoryPath = path;
        _runtime = RuntimeLabel();
        if (_runtime is "Cpu" or "CpuNoAvx") _runtime = "CPU";
        return _factory;
    }

    /// <summary>How many threads the transcription runs on: half of the cores while the GPU does the work.</summary>
    protected virtual int ThreadCount => Math.Clamp(Environment.ProcessorCount / 2, 2, 8);

    WhisperProcessor Processor(string? language)
    {
        var key = language ?? "auto";
        if (_processors.TryGetValue(key, out var p)) return p;
        var builder = Factory().CreateBuilder()
            .WithLanguage(key)
            .WithNoContext()
            .WithSingleSegment()
            .WithTemperature(0f)
            .WithThreads(ThreadCount);
        if (BeamSize > 1) builder = builder.WithBeamSearchSamplingStrategy(b => b.WithBeamSize(BeamSize));
        p = builder.Build();
        _processors[key] = p;
        return p;
    }

    public async Task<Transcript> TranscribeAsync(float[] samples, string? language, CancellationToken ct)
    {
        if (language is not null && !Vocabulary.WhisperLanguages.Contains(language)) throw new InvalidOperationException("Whisper does not know " + language);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var processor = await Task.Run(() => Processor(language), ct).ConfigureAwait(false);
            var watch = Stopwatch.StartNew();
            var text = new System.Text.StringBuilder();
            await foreach (var segment in processor.ProcessAsync(samples, ct).ConfigureAwait(false))
            {
                text.Append(segment.Text);
            }

            if (_warm.Contains(language ?? "auto")) _seconds = _seconds * 0.7 + watch.Elapsed.TotalSeconds * 0.3;
            _warm.Add(language ?? "auto");
            return new Transcript(text.ToString().Trim(), 0, _runtime);
        }
        finally
        {
            _gate.Release();
        }
    }

    readonly HashSet<string> _warm = new(StringComparer.Ordinal);

    /// <summary>
    /// Loads the model (when it is on disk) and runs half a second of silence through the processor of
    /// <paramref name="language"/>: the first inference on the GPU also prepares its shaders.
    /// </summary>
    public async Task WarmAsync(string? language)
    {
        if (ModelPath is null) return;
        var key = language ?? "auto";
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_warm.Contains(key)) return;
            await Task.Run(async () =>
            {
                var processor = Processor(language);
                await foreach (var _ in processor.ProcessAsync(new float[8000]).ConfigureAwait(false))
                {
                }
            }).ConfigureAwait(false);
            _warm.Add(key);
        }
        catch (Exception e) when (e is WhisperModelLoadException or InvalidOperationException or IOException or WhisperProcessingException)
        {
            _error = e.Message;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>True once the processor for this language has run.</summary>
    public bool IsWarm(string? language) => _warm.Contains(language ?? "auto");

    void DisposeFactory()
    {
        foreach (var p in _processors.Values) p.Dispose();
        _processors.Clear();
        _warm.Clear();
        _factory?.Dispose();
        _factory = null;
        _factoryPath = null;
    }

    public void Dispose()
    {
        _gate.Wait();
        try
        {
            DisposeFactory();
        }
        finally
        {
            _gate.Release();
        }
    }
}
