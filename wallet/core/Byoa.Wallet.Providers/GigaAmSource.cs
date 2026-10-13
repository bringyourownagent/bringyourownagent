using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Byoa.Wallet.Protocol;
using Byoa.Wallet.Sources;
using SherpaOnnx;

namespace Byoa.Wallet.Providers;

/// <summary>
/// GigaAM v3 (Russian only) through sherpa-onnx on the CPU. A visitor's model folders are read only; when no
/// visitor folder is found the wallet can fetch the pinned public files into its own models folder.
/// </summary>
public sealed class GigaAmSource : ISpeechSource, IDownloadableSource, IWarmable, IDisposable
{
    public const string SourceId = "gigaam-v3";
    public const string OwnershipMarker = ".byoa-wallet-owned";
    public const string OwnershipMarkerText = "BYOA wallet-owned model folder\n";
    public const string ModelFolderName = "sherpa-onnx-nemo-transducer-giga-am-v3-russian-2025-12-16";
    public const long ModelSizeBytes = 229_344_179;
    public const string DefaultDownloadUrl =
        "https://huggingface.co/csukuangfj/sherpa-onnx-nemo-transducer-giga-am-v3-russian-2025-12-16/resolve/main/";

    /// <summary>The exact files and hashes published by Hugging Face for the wallet's GigaAM download.</summary>
    public static readonly IReadOnlyList<GigaAmFileSpec> DownloadFiles =
    [
        new("encoder.int8.onnx", 224_570_814, "0ec54e5f130a91c0f228d21795ede10b31c992a1f73e45dcd827e843a71a9b50"),
        new("decoder.onnx", 3_331_651, "5d1cfef155d8e07f213bd2f3229b5f6c5c29355c7fa5c1b9eb62cb1925725254"),
        new("joiner.onnx", 1_440_448, "fd1d02f45c2ad3d6b67cc149811ad794ab4b020ed49a0a9e2790a8619d1cddd8"),
        new("tokens.txt", 196, "17cc514451bcceac9c280068c71502f8448f99e9fb1456b8d0761651fd0392f2"),
        new("LICENSE", 1_070, "f00de6715714c7a63d08639cdbfaa40224eefc407302614bd19f1a8b98c875aa"),
    ];

    readonly Func<string?> _findFolder;
    readonly HttpClient _http;
    readonly bool _ownsHttp;
    readonly string _modelsRoot;
    readonly Uri _downloadBase;
    readonly IReadOnlyList<GigaAmFileSpec> _downloadFiles;
    readonly object _gate = new();
    OfflineRecognizer? _recognizer;
    string? _loadedFrom;
    string? _ownedFolder;
    string? _error;
    string? _errorFolder;
    Task? _download;
    double? _progress;
    string? _downloadError;
    double _seconds = 0.2;
    bool _hasMeasuredSeconds;

    /// <param name="findFolder">Returns the GigaAM folder from the visitor's model folders, or null.</param>
    /// <param name="http">The wallet HTTP client. It is used only for the pinned public model files.</param>
    /// <param name="modelsRoot">The active wallet models folder; injectable for isolated tests.</param>
    /// <param name="downloadBaseUri">The folder containing the pinned files; injectable for a fake server.</param>
    /// <param name="downloadFiles">The pinned file list; injectable for small deterministic tests.</param>
    public GigaAmSource(Func<string?> findFolder, HttpClient? http = null, string? modelsRoot = null,
        Uri? downloadBaseUri = null, IReadOnlyList<GigaAmFileSpec>? downloadFiles = null)
    {
        _findFolder = findFolder;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _ownsHttp = http is null;
        _modelsRoot = Path.GetFullPath(modelsRoot ?? WalletPaths.Models);
        _downloadBase = downloadBaseUri ?? new Uri(DefaultDownloadUrl, UriKind.Absolute);
        _downloadFiles = downloadFiles ?? DownloadFiles;
    }

    public string Id => SourceId;
    public string Name => "GigaAM v3";
    public string Runs => Sources.Runs.ThisDevice;
    public string ProviderKind => "local-model";
    public IReadOnlyList<string>? Languages { get; } = ["ru"];
    public int LanguageCount => 1;
    public bool Paid => false;
    public double ApproxSeconds => _seconds;
    public bool HasMeasuredSeconds => _hasMeasuredSeconds;

    public event Action? ProgressChanged;

    public double? DownloadProgress => _progress;
    public long? DownloadSize => _downloadFiles.Sum(file => file.SizeBytes);

    public string? Folder => (_loadedFrom is { } loaded && Directory.Exists(loaded) ? loaded : null) ?? ExistingFolder();

    string? ExistingFolder()
    {
        if (_ownedFolder is { } owned && Directory.Exists(owned)) return owned;
        var found = _findFolder();
        return found is not null && Directory.Exists(found) && !IsStagingPath(found) ? found : null;
    }

    bool IsStagingPath(string path) => string.Equals(Path.GetFullPath(path),
        Path.Combine(_modelsRoot, "gigaam", ModelFolderName + ".part"), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Broken after a failed load of the same folder (the route then skips it and falls back); a different folder
    /// (after a rescan or a new model folder) is tried again.
    /// </summary>
    public SourceState State
    {
        get
        {
            var folder = Folder;
            if (folder is not null)
                return _error is not null && _errorFolder == folder ? SourceState.Broken : SourceState.Ready;
            if (_download is { IsCompleted: false }) return SourceState.Downloading;
            if (_downloadError is not null) return SourceState.Broken;
            return SourceState.NeedsDownload;
        }
    }

    public string StateText => State switch
    {
        SourceState.Broken when _downloadError is not null => "download failed: " + _downloadError,
        SourceState.Broken => "failed to load: " + _error,
        SourceState.Downloading => "downloading " + (_progress ?? 0).ToString("P0", CultureInfo.InvariantCulture),
        SourceState.NeedsDownload => "not downloaded yet (230 MB), downloads on first use",
        _ => _recognizer is null ? "ready · CPU (loads on first use)" : "ready · CPU",
    };

    public decimal EstimateCost(double clipSeconds) => 0;

    // ------------------------------------------------------------------------------------------------
    // wallet-owned download

    public Task EnsureDownloadedAsync(CancellationToken ct)
    {
        if (Folder is not null) return Task.CompletedTask;
        lock (_gate)
        {
            if (Folder is not null) return Task.CompletedTask;
            if (_download is null || _download.IsCompleted)
            {
                _downloadError = null;
                _download = DownloadAsync(ct);
            }

            return _download.WaitAsync(ct);
        }
    }

    async Task DownloadAsync(CancellationToken ct)
    {
        var staging = Path.Combine(_modelsRoot, "gigaam", ModelFolderName + ".part");
        var target = Path.Combine(_modelsRoot, "gigaam", ModelFolderName);
        try
        {
            PrepareFolders(staging, target);
            if (_ownedFolder == target)
            {
                _progress = null;
                return;
            }

            var total = DownloadSize ?? 0;
            long done = 0;
            _progress = 0;
            ProgressChanged?.Invoke();

            foreach (var file in _downloadFiles)
            {
                ct.ThrowIfCancellationRequested();
                var part = Path.Combine(staging, file.Name + ".part");
                var final = Path.Combine(staging, file.Name);
                using var response = await _http.GetAsync(new Uri(_downloadBase, file.Name),
                    HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is { } contentLength
                    && contentLength != file.SizeBytes)
                    throw new IOException($"{file.Name} is {contentLength} bytes, expected {file.SizeBytes}");

                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long fileDone = 0;
                await using (var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                await using (var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None,
                    1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    var buffer = new byte[1 << 20];
                    while (true)
                    {
                        var read = await input.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                        if (read == 0) break;
                        fileDone += read;
                        if (fileDone > file.SizeBytes) throw new IOException($"{file.Name} is larger than expected");
                        await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                        hash.AppendData(buffer, 0, read);
                        done += read;
                        _progress = total > 0 ? Math.Clamp((double)done / total, 0, 1) : null;
                        ProgressChanged?.Invoke();
                    }
                }

                if (fileDone != file.SizeBytes) throw new IOException($"downloaded {fileDone} bytes for {file.Name}, expected {file.SizeBytes}");
                var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                if (!actual.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException(file.Name + " failed its SHA-256 check");
                File.Move(part, final, overwrite: false);
            }

            File.WriteAllText(Path.Combine(staging, OwnershipMarker), OwnershipMarkerText);
            if (Directory.Exists(target))
            {
                if (GigaAmFiles.Find(target) is not null) throw new IOException("the wallet model folder already exists");
                throw new IOException("a model folder with the wallet's name already exists");
            }

            Directory.Move(staging, target);
            _ownedFolder = target;
            _downloadError = null;
            _progress = null;
        }
        catch (OperationCanceledException)
        {
            _downloadError = null;
            _progress = null;
            throw;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _downloadError = e.Message;
            _progress = null;
        }
        finally
        {
            RemoveOwnedStaging(staging);
            ProgressChanged?.Invoke();
        }
    }

    void PrepareFolders(string staging, string target)
    {
        if (HasReparsePoint(_modelsRoot)) throw new IOException("the wallet models folder is redirected");
        Directory.CreateDirectory(_modelsRoot);
        var root = Path.Combine(_modelsRoot, "gigaam");
        if (HasReparsePoint(root)) throw new IOException("the wallet GigaAM folder is redirected");
        Directory.CreateDirectory(root);
        if (Directory.Exists(target) && GigaAmFiles.Find(target) is not null)
        {
            _ownedFolder = target;
            return;
        }

        if (Directory.Exists(target)) throw new IOException("a model folder with the wallet's name already exists");
        if (File.Exists(staging)) throw new IOException("the wallet staging path is a file");
        if (Directory.Exists(staging))
        {
            if (!File.Exists(Path.Combine(staging, OwnershipMarker)))
                throw new IOException("the wallet will not use an unowned staging folder");
            Directory.Delete(staging, recursive: true);
        }
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, OwnershipMarker), OwnershipMarkerText);
    }

    static void RemoveOwnedStaging(string staging)
    {
        try
        {
            if (Directory.Exists(staging) && File.Exists(Path.Combine(staging, OwnershipMarker)))
                Directory.Delete(staging, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // A failed cleanup is still safe: the folder has the ownership marker and is never treated as a model.
        }
    }

    static bool HasReparsePoint(string path)
    {
        try { return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint); }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

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
        var folder = ExistingFolder() ?? throw new InvalidOperationException("GigaAM model folder not found");
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
                _seconds = _hasMeasuredSeconds
                    ? _seconds * 0.7 + watch.Elapsed.TotalSeconds * 0.3
                    : watch.Elapsed.TotalSeconds;
                _hasMeasuredSeconds = true;
                return new Transcript(text, 0, "CPU");
            }
        }, ct);
    }

    /// <summary>Clears the in-memory reference after the wallet removes its own model folder.</summary>
    public void ForgetDownloadedFolder(string folder)
    {
        lock (_gate)
        {
            if (!string.Equals(Path.GetFullPath(folder), Path.GetFullPath(_ownedFolder ?? ""), StringComparison.OrdinalIgnoreCase)) return;
            _recognizer?.Dispose();
            _recognizer = null;
            _loadedFrom = null;
            _ownedFolder = null;
            _error = null;
            _errorFolder = null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _recognizer?.Dispose();
            _recognizer = null;
        }
        if (_ownsHttp) _http.Dispose();
    }
}

/// <summary>One pinned GigaAM file used by the wallet downloader and its deterministic tests.</summary>
public sealed record GigaAmFileSpec(string Name, long SizeBytes, string Sha256);
