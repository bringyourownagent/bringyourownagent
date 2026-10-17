using Byoa.Wallet.Engine;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Tests;

public sealed class ConsentRouteTests
{
    [Fact]
    public void TheSpeechRouteDetailSaysSpeedIsNotMeasuredBeforeARealClip()
    {
        var source = new FakeSource("local-ru", languages: ["ru"]);

        Assert.Equal("speed not measured yet", WalletEngine.SpeechRouteDetail(source, "ru"));
    }

    [Fact]
    public void ManagerDoesNotPromiseTheDefaultSpeedBeforeARealClip()
    {
        var source = new FakeSource("local-ru", languages: ["ru"]);
        var text = Router.Explain(new RoutePlan([new RouteStep(source, false)], []), "ru");

        Assert.Contains("speed not measured yet", text, StringComparison.Ordinal);
        Assert.DoesNotContain("0.2 s", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AMeasuredSourceMayShowItsObservedSpeed()
    {
        var source = new FakeSource("local-ru", languages: ["ru"]) { HasMeasuredSeconds = true };

        Assert.Contains("about 0.1 s a clip", Router.Explain(new RoutePlan([new RouteStep(source, false)], []), "ru"), StringComparison.Ordinal);
        Assert.Equal("about 0.1 s a clip", WalletEngine.SpeechRouteDetail(source, "ru"));
    }
}
