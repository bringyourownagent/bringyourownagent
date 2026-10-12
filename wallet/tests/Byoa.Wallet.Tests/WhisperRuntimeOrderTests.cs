using Byoa.Wallet.Providers;
using Whisper.net.LibraryLoader;

namespace Byoa.Wallet.Tests;

/// <summary>The Whisper native builds to try: a processor without AVX never gets the Vulkan or the AVX CPU build.</summary>
public class WhisperRuntimeOrderTests
{
    [Theory]
    [InlineData(true, true, false, true)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, false, false)]
    public void HardwareDecisionUsesTheInjectedAvxAndVulkanFacts(bool avx, bool gpu, bool forceCpu, bool expectedGpu)
    {
        var decision = WhisperSource.DecideRuntime(CpuFeatures.ForTests(avx), new VulkanProbeResult(gpu), forceCpu ? "cpu" : null);

        Assert.Equal(expectedGpu, decision.UseGpu);
        Assert.Equal(avx, decision.Avx);
        Assert.Equal(avx && expectedGpu
            ? new[] { RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx }
            : avx
            ? new[] { RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx }
            : new[] { RuntimeLibrary.CpuNoAvx }, decision.Order);
    }

    [Fact]
    public void RuntimeDecisionIsLatchedOnce()
    {
        var latch = new RuntimeDecisionLatch();
        var calls = 0;
        var first = latch.GetOrCreate(() =>
        {
            calls++;
            return WhisperSource.DecideRuntime(CpuFeatures.ForTests(true), new VulkanProbeResult(true));
        });
        var second = latch.GetOrCreate(() =>
        {
            calls++;
            return WhisperSource.DecideRuntime(CpuFeatures.ForTests(true), new VulkanProbeResult(false), "cpu");
        });

        Assert.Same(first, second);
        Assert.Equal(1, calls);
        Assert.True(latch.Value!.UseGpu);
    }

    [Fact]
    public void DiagnosticOverrideCanForceEitherRuntime()
    {
        Assert.False(WhisperSource.DecideRuntime(CpuFeatures.ForTests(true), new VulkanProbeResult(true), "cpu").UseGpu);
        Assert.True(WhisperSource.DecideRuntime(CpuFeatures.ForTests(true), new VulkanProbeResult(false), "gpu").UseGpu);
        Assert.False(WhisperSource.DecideRuntime(CpuFeatures.ForTests(false), new VulkanProbeResult(true), "gpu").UseGpu);
    }

    [Theory]
    [InlineData(1, "Intel integrated", true)]
    [InlineData(2, "NVIDIA GeForce", true)]
    [InlineData(2, "llvmpipe (LLVM)", false)]
    [InlineData(3, "Microsoft Basic Render Driver", false)]
    public void ProbeRejectsSoftwareAndNonGpuPhysicalDevices(int type, string name, bool usable) =>
        Assert.Equal(usable, VulkanProbe.IsUsablePhysicalDevice(type, name));

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
