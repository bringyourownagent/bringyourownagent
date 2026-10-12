using Byoa.Wallet.Providers;
using Byoa.Wallet.Protocol;
using Byoa.Wallet.Registry;
using Byoa.Wallet.Settings;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Desktop;

/// <summary>Connects the registry's model catalog to the existing Whisper downloader.</summary>
sealed class WalletModelManager : IWalletModelManager
{
    static readonly (string File, string Note, long Size, string Hardware)[] WhisperModels =
    [
        ("ggml-large-v3-q5_0.bin", "best multilingual quality", 1_081_140_203, "GPU recommended; CPU works"),
        ("ggml-large-v3-turbo-q8_0.bin", "faster multilingual model", 874_188_075, "GPU recommended; CPU works"),
        ("ggml-large-v3-turbo-q5_0.bin", "smaller turbo model", 574_041_195, "GPU recommended; CPU works"),
        ("ggml-medium-q8_0.bin", "smaller multilingual model", 1_533_763_059, "GPU or a patient CPU"),
        ("ggml-small-q8_0.bin", "small computer-friendly model", 487_601_967, "CPU works; GPU helps"),
    ];

    readonly SettingsStore _store;
    readonly WhisperSource _whisper;
    readonly GigaAmSource? _gigaAm;
    readonly string _modelsRoot;
    readonly string _whisperRoot;
    readonly string _gigaAmRoot;

    // A model is removable only when both the settings ledger and this marker say that the wallet created it.
    // The marker also keeps a visitor's own model folder from becoming a deletion target.
    internal const string OwnershipMarker = ".byoa-wallet-owned";
    const string GigaAmLedger = "gigaam:" + GigaAmSource.ModelFolderName;

    public WalletModelManager(SettingsStore store, WhisperSource whisper, string? modelsRoot = null)
        : this(store, whisper, null, modelsRoot)
    {
    }

    public WalletModelManager(SettingsStore store, WhisperSource whisper, GigaAmSource? gigaAm, string? modelsRoot = null)
    {
        _store = store;
        _whisper = whisper;
        _gigaAm = gigaAm;
        _modelsRoot = Path.GetFullPath(modelsRoot ?? WalletPaths.Models);
        _whisperRoot = Path.Combine(_modelsRoot, "whisper");
        _gigaAmRoot = Path.Combine(_modelsRoot, "gigaam");
        _whisper.ProgressChanged += TrackAutomaticDownload;
        if (_gigaAm is not null) _gigaAm.ProgressChanged += TrackAutomaticGigaAmDownload;
    }

    public IReadOnlyList<DownloadableModelInfo> Models
    {
        get
        {
            var settings = _store.Snapshot();
            var list = WhisperModels.Select(m => new DownloadableModelInfo(
                "whisper:" + m.File,
                "Whisper " + WhisperSource.ModelLabel(m.File),
                Vocabulary.Transcribe,
                m.Size,
                Vocabulary.WhisperLanguages,
                m.Hardware,
                IsOwnedModel(Path.Combine(_whisperRoot, m.File), settings),
                Path.Combine(_whisperRoot, m.File))).ToList();
            list.Add(new DownloadableModelInfo(
                "gigaam:gigaam-v3",
                "GigaAM v3",
                Vocabulary.Transcribe,
                GigaAmSource.ModelSizeBytes,
                ["ru"],
                "CPU; Russian only",
                IsOwnedGigaAm(_store.Snapshot()),
                IsOwnedGigaAm(_store.Snapshot()) ? GigaAmFolder : null,
                CanDownload: true));
            return list;
        }
    }

    string GigaAmFolder => Path.Combine(_gigaAmRoot, GigaAmSource.ModelFolderName);

    public async Task<RegistryOperationResult> DownloadAsync(string modelId, IProgress<ModelDownloadProgress>? progress, CancellationToken ct)
    {
        if (modelId == "gigaam:gigaam-v3") return await DownloadGigaAmAsync(progress, ct).ConfigureAwait(false);
        var model = WhisperModels.FirstOrDefault(m => "whisper:" + m.File == modelId);
        if (model.File is null) return RegistryOperationResult.Failure("That model is not in the wallet's download catalog.");
        var folder = EnsureOwnedFolder();
        if (!folder.Ok) return folder;
        var target = Path.Combine(_whisperRoot, model.File);
        if (File.Exists(target) && !IsOwnedModel(target, _store.Snapshot()))
            return RegistryOperationResult.Failure("A file with that model name exists, but this wallet did not download it.");
        if (_whisper.ModelPath is { } existing && !PathsEqual(existing, target))
            return RegistryOperationResult.Failure("A model with that name was found outside this wallet's own models folder.");

        _store.Update(s => s.WhisperModel = model.File);
        if (IsOwnedModel(target, _store.Snapshot()))
            return RegistryOperationResult.Success("Downloaded " + model.File + ".", modelId);

        void Report() => progress?.Report(new ModelDownloadProgress(modelId, _whisper.DownloadProgress,
            _whisper.DownloadProgress is { } f ? (long)(f * model.Size) : null, _whisper.DownloadSize ?? model.Size));
        _whisper.ProgressChanged += Report;
        try
        {
            Report();
            await _whisper.EnsureDownloadedAsync(ct).ConfigureAwait(false);
            Report();
            if (_whisper.State == SourceState.Ready && IsWalletFile(target))
            {
                _store.Update(s =>
                {
                    if (!s.DownloadedModelFiles.Contains(model.File, StringComparer.Ordinal))
                        s.DownloadedModelFiles.Add(model.File);
                });
                return RegistryOperationResult.Success("Downloaded " + _whisper.Name + ".", modelId);
            }

            return _whisper.State == SourceState.Ready
                ? RegistryOperationResult.Failure("The model is ready, but the wallet could not verify that it downloaded the file.")
                : RegistryOperationResult.Failure("The download did not finish: " + _whisper.StateText);
        }
        finally
        {
            _whisper.ProgressChanged -= Report;
        }
    }

    async Task<RegistryOperationResult> DownloadGigaAmAsync(IProgress<ModelDownloadProgress>? progress, CancellationToken ct)
    {
        if (_gigaAm is null) return RegistryOperationResult.Failure("GigaAM is not available in this wallet build.");
        if (_gigaAm.Folder is { } visitorFolder && !PathsEqual(visitorFolder, GigaAmFolder))
            return RegistryOperationResult.Success("GigaAM v3 is already available in your model folders; nothing was downloaded.",
                "gigaam:gigaam-v3");
        var folder = EnsureGigaAmFolder();
        if (!folder.Ok) return folder;
        var settings = _store.Snapshot();
        if (IsOwnedGigaAm(settings)) return RegistryOperationResult.Success("GigaAM v3 is already downloaded.", "gigaam:gigaam-v3");

        void Report() => progress?.Report(new ModelDownloadProgress("gigaam:gigaam-v3", _gigaAm.DownloadProgress,
            _gigaAm.DownloadProgress is { } fraction ? (long)(fraction * GigaAmSource.ModelSizeBytes) : null,
            _gigaAm.DownloadSize ?? GigaAmSource.ModelSizeBytes));
        _gigaAm.ProgressChanged += Report;
        try
        {
            Report();
            await _gigaAm.EnsureDownloadedAsync(ct).ConfigureAwait(false);
            Report();
            if (_gigaAm.State == SourceState.Ready && IsWalletGigaAmFolder(GigaAmFolder))
            {
                _store.Update(s =>
                {
                    if (!s.DownloadedModelFiles.Contains(GigaAmLedger, StringComparer.OrdinalIgnoreCase))
                        s.DownloadedModelFiles.Add(GigaAmLedger);
                });
                return RegistryOperationResult.Success("Downloaded GigaAM v3.", "gigaam:gigaam-v3");
            }

            return RegistryOperationResult.Failure("The GigaAM download did not finish: " + _gigaAm.StateText);
        }
        finally
        {
            _gigaAm.ProgressChanged -= Report;
        }
    }

    public Task<RegistryOperationResult> RemoveAsync(DownloadableModelInfo model, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (model.Id == "gigaam:gigaam-v3") return RemoveGigaAmAsync(model);
        if (string.IsNullOrWhiteSpace(model.OwnedPath) || !IsOwnedModel(model.OwnedPath, _store.Snapshot()))
            return Task.FromResult(RegistryOperationResult.Failure("The wallet refused that file because it did not download it itself."));

        try
        {
            File.Delete(model.OwnedPath);
            _store.Update(s => s.DownloadedModelFiles.RemoveAll(file =>
                file.Equals(Path.GetFileName(model.OwnedPath), StringComparison.OrdinalIgnoreCase)));
            return Task.FromResult(RegistryOperationResult.Success("Removed " + model.Name + ". You can download it again.", model.Id));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return Task.FromResult(RegistryOperationResult.Failure("The wallet could not remove that downloaded model: " + e.Message));
        }
    }

    Task<RegistryOperationResult> RemoveGigaAmAsync(DownloadableModelInfo model)
    {
        if (!PathsEqual(model.OwnedPath, GigaAmFolder) || !IsOwnedGigaAm(_store.Snapshot()))
            return Task.FromResult(RegistryOperationResult.Failure("The wallet refused that folder because it did not download it itself."));
        try
        {
            Directory.Delete(GigaAmFolder, recursive: true);
            _gigaAm?.ForgetDownloadedFolder(GigaAmFolder);
            _store.Update(s => s.DownloadedModelFiles.RemoveAll(file =>
                file.Equals(GigaAmLedger, StringComparison.OrdinalIgnoreCase)));
            return Task.FromResult(RegistryOperationResult.Success("Removed GigaAM v3. You can download it again.", model.Id));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return Task.FromResult(RegistryOperationResult.Failure("The wallet could not remove that downloaded model: " + e.Message));
        }
    }

    RegistryOperationResult EnsureOwnedFolder()
    {
        try
        {
            if (Directory.Exists(_whisperRoot))
            {
                if (HasReparsePoint(_modelsRoot) || HasReparsePoint(_whisperRoot) || !File.Exists(Path.Combine(_whisperRoot, OwnershipMarker)))
                    return RegistryOperationResult.Failure("The wallet will not use that model folder because it did not create it.");
                return RegistryOperationResult.Success("The wallet model folder is ready.");
            }

            if (HasReparsePoint(_modelsRoot))
                return RegistryOperationResult.Failure("The wallet will not use that model folder because it is a redirected folder.");
            Directory.CreateDirectory(_whisperRoot);
            File.WriteAllText(Path.Combine(_whisperRoot, OwnershipMarker), "BYOA wallet-owned model folder\n");
            return RegistryOperationResult.Success("The wallet model folder is ready.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return RegistryOperationResult.Failure("The wallet could not prepare its model folder: " + e.Message);
        }
    }

    RegistryOperationResult EnsureGigaAmFolder()
    {
        try
        {
            if (HasReparsePoint(_modelsRoot))
                return RegistryOperationResult.Failure("The wallet will not use that model folder because it is redirected.");
            Directory.CreateDirectory(_modelsRoot);
            if (HasReparsePoint(_gigaAmRoot))
                return RegistryOperationResult.Failure("The wallet will not use that GigaAM folder because it is redirected.");
            Directory.CreateDirectory(_gigaAmRoot);
            return RegistryOperationResult.Success("The wallet GigaAM folder is ready.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return RegistryOperationResult.Failure("The wallet could not prepare its GigaAM folder: " + e.Message);
        }
    }

    bool IsOwnedGigaAm(WalletSettings settings) =>
        settings.DownloadedModelFiles.Contains(GigaAmLedger, StringComparer.OrdinalIgnoreCase)
        && IsWalletGigaAmFolder(GigaAmFolder);

    bool IsWalletGigaAmFolder(string path)
    {
        try
        {
            var candidate = Path.GetFullPath(path);
            if (!PathsEqual(candidate, GigaAmFolder)
                || !WalletRegistry.IsInsideModels(candidate, _modelsRoot)
                || !Directory.Exists(candidate)
                || !File.Exists(Path.Combine(candidate, OwnershipMarker))
                || GigaAmFiles.Find(candidate) is null
                || HasReparsePoint(_modelsRoot)
                || HasReparsePoint(_gigaAmRoot)
                || HasReparsePoint(candidate)) return false;
            return Directory.EnumerateFileSystemEntries(candidate, "*", SearchOption.TopDirectoryOnly)
                .All(entry => File.Exists(entry) && !HasReparsePoint(entry));
        }
        catch (Exception e) when (e is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whisper can download on first use without going through the model catalog button. Once its progress event
    /// proves that a download is in flight, claim only the exact file that appeared in the active wallet folder.
    /// </summary>
    void TrackAutomaticDownload()
    {
        if (_whisper.State is not (SourceState.Downloading or SourceState.Ready)) return;
        var file = _store.Snapshot().WhisperModel;
        if (string.IsNullOrWhiteSpace(file)) return;
        var target = Path.Combine(_whisperRoot, file);
        var folder = EnsureOwnedFolder();
        if (!folder.Ok) return;
        try
        {
            if (!File.Exists(target)) return;
            _store.Update(s =>
            {
                if (!s.DownloadedModelFiles.Contains(file, StringComparer.OrdinalIgnoreCase)) s.DownloadedModelFiles.Add(file);
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // The source will report the download result; an unclaimable file remains non-removable by the wallet.
        }
    }

    void TrackAutomaticGigaAmDownload()
    {
        if (_gigaAm?.State != SourceState.Ready || !IsWalletGigaAmFolder(GigaAmFolder)) return;
        _store.Update(s =>
        {
            if (!s.DownloadedModelFiles.Contains(GigaAmLedger, StringComparer.OrdinalIgnoreCase))
                s.DownloadedModelFiles.Add(GigaAmLedger);
        });
    }

    bool IsOwnedModel(string path, WalletSettings settings)
    {
        try
        {
            var candidate = Path.GetFullPath(path);
            var file = Path.GetFileName(candidate);
            return IsWalletFile(candidate)
                && settings.DownloadedModelFiles.Contains(file, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    bool IsWalletFile(string path)
    {
        var candidate = Path.GetFullPath(path);
        return WalletRegistry.IsInsideModels(candidate, _modelsRoot)
            && PathsEqual(Path.GetDirectoryName(candidate), _whisperRoot)
            && File.Exists(candidate)
            && File.Exists(Path.Combine(_whisperRoot, OwnershipMarker))
            && !HasReparsePoint(_modelsRoot)
            && !HasReparsePoint(_whisperRoot)
            && !HasReparsePoint(Path.Combine(_whisperRoot, OwnershipMarker))
            && !HasReparsePoint(candidate);
    }

    static bool PathsEqual(string? left, string? right) => left is not null && right is not null
        && string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    static bool HasReparsePoint(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }
}
