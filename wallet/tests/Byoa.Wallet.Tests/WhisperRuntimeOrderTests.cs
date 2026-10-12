using Byoa.Wallet.Providers;
using Whisper.net.LibraryLoader;

namespace Byoa.Wallet.Tests;

/// <summary>The Whisper native builds to try: a processor without AVX never gets the Vulkan or the AVX CPU build.</summary>
public class WhisperRuntimeOrderTests
{
    [Fact]
    public void WithoutAvxOnlyTheNoAvxBuildIsTried()
    {
        Assert.Equal(new[] { RuntimeLibrary.CpuNoAvx }, WhisperSource.RuntimeOrderFor(useGpu: true, avx: false));
        Assert.Equal(new[] { RuntimeLibrary.CpuNoAvx }, WhisperSource.RuntimeOrderFor(useGpu: false, avx: false));
    }

    [Fact]
    public void WithAvxTheGpuSourceTriesVulkanFirstAndTheCpuBuildAfterIt()
    {
        Assert.Equal(
            new[] { RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx },
            WhisperSource.RuntimeOrderFor(useGpu: true, avx: true));
    }

    [Fact]
    public void WithAvxTheCpuSourceNeverLoadsVulkan()
    {
        Assert.Equal(
            new[] { RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx },
            WhisperSource.RuntimeOrderFor(useGpu: false, avx: true));
    }
}
