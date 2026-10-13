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

        Assert.Equal("1 MB download; speed not measured yet", WalletEngine.SpeechRouteDetail(giga, "ru"));
        Assert.Equal("1 MB download; speed not measured yet",
            WalletEngine.SpeechRouteDetail(cpu, "sk"));
    }

    [Fact]
    public void ManagerDoesNotPromiseTheDefaultSpeedBeforeARealClip()
    {
        var source = new SlowDownloadSource(Router.GigaAmId, ["ru"]);
        var text = Router.Explain(new RoutePlan([new RouteStep(source, false)], []), "ru");

        Assert.Contains("speed not measured yet", text, StringComparison.Ordinal);
        Assert.DoesNotContain("0.2 s", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AMeasuredSourceMayShowItsObservedSpeed()
    {
        var source = new FakeSource(Router.GigaAmId, languages: ["ru"]) { HasMeasuredSeconds = true };

        Assert.Contains("about 0.1 s a clip", Router.Explain(new RoutePlan([new RouteStep(source, false)], []), "ru"), StringComparison.Ordinal);
    }
}
