using Byoa.Wallet.Protocol;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Providers;

/// <summary>
/// A small Whisper that always runs on the CPU: the fallback for machines where the big GPU Whisper does not work
/// (no Vulkan in the driver, or the large model not downloaded and not chosen). The router skips it while the big
/// Whisper is ready. The small model downloads into the models folder of the wallet home on first use — the same
/// ggml file works on every computer, so the download is the only setup there is.
/// </summary>
public sealed class WhisperCpuSource : WhisperSource
{
    /// <summary>Small beats base on non-English words (measured, see docs/text-generate.md) and still fits a first download.</summary>
    public const string ModelFile = "ggml-small-q5_1.bin";

    public WhisperCpuSource(Func<IReadOnlyList<FoundModel>> found, HttpClient http, CpuFeatureSet? cpuFeatures = null)
        : base(() => ModelFile, found, http, cpuFeatures)
    {
    }

    public override string Id => Router.CpuWhisperId;
    public override string Name => base.Name + " (CPU)";
    protected override string ModelDir => Path.Combine(WalletPaths.Home, "models", "whisper");
    protected override bool UseGpu => false;
    protected override string? RuntimeLabel() => "this PC (CPU)";
    protected override int ThreadCount => Math.Clamp(Environment.ProcessorCount, 2, 16);
}
