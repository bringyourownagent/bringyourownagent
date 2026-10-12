using System.Runtime.Intrinsics.X86;

namespace Byoa.Wallet.Providers;

/// <summary>The CPU feature decision used by every Whisper source in this process.</summary>
public sealed record CpuFeatureSet(bool Avx);

public static class CpuFeatures
{
    public static CpuFeatureSet Current => new(Avx.IsSupported);

    /// <summary>Creates an injectable feature set for routing and state tests.</summary>
    public static CpuFeatureSet ForTests(bool avx) => new(avx);
}
