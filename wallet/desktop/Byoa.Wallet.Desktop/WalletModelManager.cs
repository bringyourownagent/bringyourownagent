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
    readonly string _modelsRoot;
    readonly string _whisperRoot;

    // A model is removable only when both the settings ledger and this marker say that the wallet created it.
    // The marker also keeps a visitor's own model folder from becoming a deletion target.
    internal const string OwnershipMarker = ".byoa-wallet-owned";

    public WalletModelManager(SettingsStore store, WhisperSource whisper, string? modelsRoot = null)
    {
        _store = store;
        _whisper = whisper;
        _modelsRoot = Path.GetFullPath(modelsRoot ?? WalletPaths.Models);
        _whisperRoot = Path.Combine(_modelsRoot, "whisper");
        _whisper.ProgressChanged += TrackAutomaticDownload;
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
                null,
                ["ru"],
                "CPU; put the model folder in a model folder",
                false,
                null,
                CanDownload: false));
            return list;
        }
    }

    public async Task<RegistryOperationResult> DownloadAsync(string modelId, IProgress<ModelDownloadProgress>? progress, CancellationToken ct)
    {
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

    public Task<RegistryOperationResult> RemoveAsync(DownloadableModelInfo model, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
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

    /// <summary>
    /// Whisper can download on first use without going through the model catalog button. Once its progress event
    /// proves that a download is in flight, claim only the exact file that appeared in the active wallet folder.
    /// </summary>
    void TrackAutomaticDownload()
    {
        if (_whisper.State != SourceState.Downloading) return;
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
