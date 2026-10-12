using Byoa.Wallet.Engine;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Tests;

public sealed class ConsentRouteTests
{
    [Fact]
    public void PreparingSpeechRouteExplainsGigaAmDownloadAndCpuLatency()
    {
        var giga = new SlowDownloadSource(Router.GigaAmId, ["ru"]);
        var cpu = new SlowDownloadSource(Router.CpuWhisperId);

        Assert.Equal("1 MB download", WalletEngine.SpeechRouteDetail(giga, "ru"));
        Assert.Equal("1 MB download; on this computer, about 3 s per phrase",
            WalletEngine.SpeechRouteDetail(cpu, "sk"));
    }
}
