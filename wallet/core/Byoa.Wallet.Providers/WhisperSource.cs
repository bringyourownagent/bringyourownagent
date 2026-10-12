using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Byoa.Wallet.Protocol;
using Byoa.Wallet.Sources;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace Byoa.Wallet.Providers;

/// <summary>The immutable process-wide choice made before Whisper.net loads a native library.</summary>
public sealed record WhisperRuntimeDecision(bool Avx, bool UseGpu, VulkanProbeResult Probe, IReadOnlyList<RuntimeLibrary> Order);

/// <summary>A small, independently testable latch for the process-wide native runtime choice.</summary>
internal sealed class RuntimeDecisionLatch
{
    readonly object _gate = new();
    WhisperRuntimeDecision? _value;

    public WhisperRuntimeDecision GetOrCreate(Func<WhisperRuntimeDecision> factory)
    {
        lock (_gate) return _value ??= factory();
    }

    public WhisperRuntimeDecision? Value
    {
        get { lock (_gate) return _value; }
    }
}

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

    // SHA-256 content hashes from the Hugging Face LFS metadata for ggerganov/whisper.cpp, recorded from the
    // repository tree/API at revision "main" while this source was written (each key below is the model file path).
    // The size and the tree metadata remain a second check; these pins are what the wallet trusts.
    static readonly IReadOnlyDictionary<string, string> PinnedSha256 = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["ggml-large-v3-q5_0.bin"] = "d75795ecff3f83b5faa89d1900604ad8c780abd5739fae406de19f23ecd98ad1",
        ["ggml-large-v3-turbo-q8_0.bin"] = "317eb69c11673c9de1e1f0d459b253999804ec71ac4c23c17ecf5fbe24e259a1",
        ["ggml-large-v3-turbo-q5_0.bin"] = "394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2",
        ["ggml-medium-q8_0.bin"] = "42a1ffcbe4167d224232443396968db4d02d4e8e87e213d3ee2e03095dea6502",
        ["ggml-small-q8_0.bin"] = "49c8fb02b65e6049d5fa6c04f81f53b867b5ec9540406812c643f177317f779f",
        ["ggml-small-q5_1.bin"] = "ae85e4a935d7a567bd102fe55afc16bb595bdb618e11b2fc7591bc08120411bb",
    };

    static readonly RuntimeDecisionLatch RuntimeLatch = new();

    readonly Func<string> _modelFile;
    readonly Func<IReadOnlyList<FoundModel>> _found;
    readonly HttpClient _http;
    readonly CpuFeatureSet _cpuFeatures;
    readonly bool? _processGpuOverride;
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
    double _seconds;

    /// <param name="modelFile">The ggml file name chosen in the settings.</param>
    /// <param name="found">The models found in the visitor's model folders.</param>
    public WhisperSource(Func<string> modelFile, Func<IReadOnlyList<FoundModel>> found, HttpClient http,
        CpuFeatureSet? cpuFeatures = null, bool? processGpuOverride = null)
    {
        _modelFile = modelFile;
        _found = found;
        _http = http;
        _cpuFeatures = cpuFeatures ?? CpuFeatures.Current;
        _processGpuOverride = processGpuOverride;
        _seconds = InitialSeconds;
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

    /// <summary>The first consent estimate before this source has measured a real phrase.</summary>
    protected virtual double InitialSeconds => 0.8;

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

    public virtual SourceState State =>
        !_cpuFeatures.Avx ? SourceState.Unavailable
        : !UseGpu && ProcessGpuSelected ? SourceState.Unavailable
        : _download is { IsCompleted: false } ? SourceState.Downloading
        : _error is not null ? SourceState.Broken
        : ModelPath is null ? SourceState.NeedsDownload
        : SourceState.Ready;

    public virtual string StateText => State switch
    {
        SourceState.Unavailable => !_cpuFeatures.Avx
            ? "unavailable: this processor has no AVX (too slow)"
            : "unavailable: the GPU build is in use",
        SourceState.Downloading => "downloading " + (_progress ?? 0).ToString("P0", CultureInfo.InvariantCulture),
        SourceState.Broken => "failed: " + _error,
        SourceState.NeedsDownload => "not downloaded yet (" + Mb(DownloadSize) + "), downloads on first use",
        _ => _runtime is null ? "ready (loads on first use)" : "ready · " + _runtime,
    };

    bool ProcessGpuSelected => _processGpuOverride ?? ProcessUsesGpu;

    /// <summary>True when the one process-wide decision selected the Vulkan-capable build for the large source.</summary>
    public static bool ProcessUsesGpu
    {
        get => RuntimeLatch.Value?.UseGpu == true;
    }

    /// <summary>The decision made before any Whisper factory is created, or null before the source set starts.</summary>
    public static WhisperRuntimeDecision? ProcessRuntime
    {
        get => RuntimeLatch.Value;
    }

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
        string? part = null;
        try
        {
            var (expectedSize, expectedSha) = await ExpectedAsync(file).ConfigureAwait(false);
            var dir = ModelDir;
            Directory.CreateDirectory(dir);
            var target = Path.Combine(dir, file);
            part = target + ".part";
            using var response = await _http.GetAsync(BaseUrl + file, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? expectedSize;
            if (expectedSize is { } known && response.Content.Headers.ContentLength is { } given && given != known)
                throw new IOException($"downloaded content is {given} bytes, expected {known}");
            _size = total;
            _progress = 0;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long done = 0;
            var lastShown = -1;
            await using (var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
            await using (var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                // Report zero only after the .part file exists. The toast and the ownership ledger then describe
                // the same download, rather than showing a finished source with a stale zero-byte staging file.
                ProgressChanged?.Invoke();
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
            if (_error is not null && part is not null) DeleteOwnedPart(part);
            ProgressChanged?.Invoke();
        }
    }

    static void DeleteOwnedPart(string part)
    {
        try
        {
            var root = Path.GetFullPath(Path.Combine(WalletPaths.Models, "whisper"));
            var candidate = Path.GetFullPath(part);
            if (!candidate.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return;
            if (File.Exists(candidate)) File.Delete(candidate);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // A partial file in the wallet's own folder is harmless and remains unclaimable until a full download.
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
                    var size = lfs?["size"]?.GetValue<long>() ?? item["size"]?.GetValue<long>();
                    var sha = PinnedSha256.TryGetValue(file, out var pinned) ? pinned : lfs?["oid"]?.GetValue<string>();
                    return (size ?? (KnownSizes.TryGetValue(file, out var known) ? known : null), sha);
                }
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
        {
            // verify by size from the response only
        }

        return (KnownSizes.TryGetValue(file, out var knownSize) ? knownSize : null,
            PinnedSha256.TryGetValue(file, out var knownSha) ? knownSha : null);
    }

    // ------------------------------------------------------------------------------------------------
    // transcription

    /// <summary>
    /// Whisper.net's library order is process-global. Decide it once before either the GPU or CPU factory exists.
    /// The automatic choice uses the Vulkan probe; the environment override is intended for diagnostics and people
    /// who know their driver better than the probe does.
    /// </summary>
    public static void ConfigureProcessRuntime(CpuFeatureSet features, bool cpuSourceEnabled,
        Func<VulkanProbeResult>? probe = null, string? runtimePreference = null)
    {
        _ = RuntimeLatch.GetOrCreate(() =>
        {
            var result = features.Avx
                ? (probe ?? VulkanProbe.Probe)()
                : new VulkanProbeResult(false, "no AVX");
            var decision = DecideRuntime(features, result, runtimePreference ?? Environment.GetEnvironmentVariable("BYOA_WHISPER_RUNTIME"));
            RuntimeOptions.RuntimeLibraryOrder = decision.Order.ToList();
            return decision;
        });
    }

    /// <summary>Pure, injectable hardware decision used by the startup code and tests.</summary>
    public static WhisperRuntimeDecision DecideRuntime(CpuFeatureSet features, VulkanProbeResult probe, string? preference = null)
    {
        var choice = preference?.Trim().ToLowerInvariant() switch
        {
            "cpu" => false,
            "gpu" => true,
            _ => probe.Usable,
        };
        var useGpu = features.Avx && choice;
        return new WhisperRuntimeDecision(features.Avx, useGpu, probe, RuntimeOrderFor(useGpu, features.Avx));
    }

    /// <summary>
    /// The native builds to try, in order. A processor without AVX gets the no-AVX build only: the Vulkan build and
    /// the AVX CPU build both need AVX (the CPU build is refused with an exception, the Vulkan build ends the process).
    /// With AVX: GPU through Vulkan first, the CPU build as the automatic fallback. A source that runs without the GPU
    /// never loads Vulkan at all: on some machines its driver breaks the CPU run instead.
    /// </summary>
    public static List<RuntimeLibrary> RuntimeOrderFor(bool useGpu, bool avx) =>
        !avx ? [RuntimeLibrary.CpuNoAvx]
        : useGpu ? [RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx]
        : [RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx];

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
        ConfigureProcessRuntime(_cpuFeatures, cpuSourceEnabled: true, runtimePreference: null);
        if (!_cpuFeatures.Avx) throw new InvalidOperationException("unavailable: this processor has no AVX (too slow)");
        var useGpu = UseGpu;
        try
        {
            _factory = WhisperFactory.FromPath(path, new WhisperFactoryOptions { UseGpu = useGpu });
        }
        catch (PlatformNotSupportedException e)
        {
            // Whisper.net refuses a build this processor cannot run: the source is unavailable, the wallet keeps running.
            throw new InvalidOperationException("this PC cannot run Whisper: " + e.Message, e);
        }
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
