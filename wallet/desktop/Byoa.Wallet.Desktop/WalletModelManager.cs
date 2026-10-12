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

    public WalletModelManager(SettingsStore store, WhisperSource whisper)
    {
        _store = store;
        _whisper = whisper;
    }

    public IReadOnlyList<DownloadableModelInfo> Models
    {
        get
        {
            var list = WhisperModels.Select(m => new DownloadableModelInfo(
                "whisper:" + m.File,
                "Whisper " + WhisperSource.ModelLabel(m.File),
                Vocabulary.Transcribe,
                m.Size,
                Vocabulary.WhisperLanguages,
                m.Hardware,
                File.Exists(Path.Combine(WalletPaths.Models, "whisper", m.File)),
                Path.Combine(WalletPaths.Models, "whisper", m.File))).ToList();
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
        _store.Update(s => s.WhisperModel = model.File);
        void Report() => progress?.Report(new ModelDownloadProgress(modelId, _whisper.DownloadProgress,
            _whisper.DownloadProgress is { } f ? (long)(f * model.Size) : null, _whisper.DownloadSize ?? model.Size));
        _whisper.ProgressChanged += Report;
        try
        {
            Report();
            await _whisper.EnsureDownloadedAsync(ct).ConfigureAwait(false);
            Report();
            return _whisper.State == SourceState.Ready
                ? RegistryOperationResult.Success("Downloaded " + _whisper.Name + ".", modelId)
                : RegistryOperationResult.Failure("The download did not finish: " + _whisper.StateText);
        }
        finally
        {
            _whisper.ProgressChanged -= Report;
        }
    }

    public Task<RegistryOperationResult> RemoveAsync(DownloadableModelInfo model, CancellationToken ct) =>
        // The owner-level no-delete rule means the wallet never removes a file on its own. The registry has already
        // checked the path is inside WalletPaths.Models before reaching this adapter.
        Task.FromResult(RegistryOperationResult.Failure("The wallet will not delete model files automatically. Remove it yourself from " + WalletPaths.Models + "."));
}
