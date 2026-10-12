using Byoa.Wallet.Settings;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Tests;

public class RoutingTests
{
    static readonly FakeSource Giga = new("gigaam-v3", languages: ["ru"]);
    static readonly FakeSource Whisper = new("whisper");
    static readonly FakeSource Paid = new("openrouter-scribe", Runs.ApiKey, pricePerSecond: 0.00004m);
    static readonly ISpeechSource[] All = [Giga, Whisper, Paid];

    static WalletSettings Defaults()
    {
        var s = new WalletSettings();
        Router.EnsureDefaults(s);
        return s;
    }

    static RoutePlan Plan(WalletSettings s, string? language) =>
        Router.Plan(s.Chain(Vocabulary.Transcribe), All, id => s.Source(id).Enabled, language);

    [Fact]
    public void DefaultChainIsGigaAmWhisperCpuThenPaid()
    {
        Assert.Equal(["gigaam-v3", "whisper", "whisper-cpu", "openrouter-scribe"], Defaults().Chain(Vocabulary.Transcribe).Stops.Select(s => s.SourceId));
    }

    [Fact]
    public void RussianGoesToGigaAmFirst()
    {
        var plan = Plan(Defaults(), "ru");
        Assert.Equal(["gigaam-v3", "whisper", "openrouter-scribe"], plan.Steps.Select(s => s.Source.Id));
        Assert.False(plan.Steps[0].AskFirst);
        Assert.True(plan.Steps[2].AskFirst);
    }

    [Theory]
    [InlineData("sk")]
    [InlineData("en")]
    [InlineData(null)]
    public void OtherLanguagesAndNoLanguageGoToWhisper(string? language)
    {
        var plan = Plan(Defaults(), language);
        Assert.Equal("whisper", plan.First!.Source.Id);
        Assert.Contains(plan.Skipped, k => k.Source.Id == "gigaam-v3");
    }

    [Fact]
    public void ATurnedOffSourceIsSkipped()
    {
        var s = Defaults();
        s.Source("gigaam-v3").Enabled = false;
        var plan = Plan(s, "ru");
        Assert.Equal("whisper", plan.First!.Source.Id);
        Assert.Equal("turned off", plan.Skipped.Single(k => k.Source.Id == "gigaam-v3").Why);
    }

    [Fact]
    public void TheVisitorsOrderWins()
    {
        var s = Defaults();
        var stops = s.Chain(Vocabulary.Transcribe).Stops;
        (stops[0], stops[1]) = (stops[1], stops[0]);
        Assert.Equal("whisper", Plan(s, "ru").First!.Source.Id);
    }

    [Fact]
    public void AskBeforePaidCanBeTurnedOff()
    {
        var s = Defaults();
        s.Chain(Vocabulary.Transcribe).Stops.Single(x => x.SourceId == "openrouter-scribe").AskBeforePaid = false;
        Assert.False(Plan(s, "ru").Steps.Single(x => x.Source.Id == "openrouter-scribe").AskFirst);
    }

    [Fact]
    public void ASourceWithoutItsModelOrKeyIsSkipped()
    {
        var giga = new FakeSource("gigaam-v3", languages: ["ru"]) { State = SourceState.Missing };
        var paid = new FakeSource("openrouter-scribe", Runs.ApiKey, pricePerSecond: 0.00004m) { State = SourceState.NoKey };
        var s = Defaults();
        var plan = Router.Plan(s.Chain(Vocabulary.Transcribe), [giga, Whisper, paid], _ => true, "ru");
        Assert.Equal(["whisper"], plan.Steps.Select(x => x.Source.Id));
    }

    [Theory]
    [InlineData(SourceState.Ready)]
    public void TheCpuWhisperStepsAsideWhileTheBigWhisperWorks(SourceState gpuState)
    {
        var cpu = new FakeSource(Router.CpuWhisperId);
        var gpu = new FakeSource(Router.WhisperId) { State = gpuState };
        var plan = Router.Plan(Defaults().Chain(Vocabulary.Transcribe), [Giga, gpu, cpu, Paid], _ => true, "sk");
        Assert.Equal("the big Whisper already works on this PC", plan.Skipped.Single(k => k.Source.Id == Router.CpuWhisperId).Why);
        Assert.DoesNotContain(cpu.Id, plan.Steps.Select(x => x.Source.Id));

        gpu.State = SourceState.NeedsDownload;                    // no GPU Whisper here: the CPU one is the fallback
        var without = Router.Plan(Defaults().Chain(Vocabulary.Transcribe), [Giga, gpu, cpu, Paid], _ => true, "sk");
        Assert.Contains(cpu.Id, without.Steps.Select(x => x.Source.Id));
    }

    [Fact]
    public void TheManagerExplainsItsChoiceFromProperties()
    {
        var ru = Router.Explain(Plan(Defaults(), "ru"), "ru");
        Assert.StartsWith("Manager suggests Fake gigaam-v3 for Russian because it is made for Russian", ru);
        Assert.Contains("no cloud bill", ru);
        Assert.Contains("(paid, asks first)", ru);
        var sk = Router.Explain(Plan(Defaults(), "sk"), "sk");
        Assert.Contains("Fake gigaam-v3 knows only Russian", sk);
        var none = Router.Explain(new RoutePlan([], []), "sk");
        Assert.StartsWith("None of your sources can do this for Slovak", none);
    }
}

public class MoneyTests
{
    [Theory]
    [InlineData("0.05 USD", 0.05)]
    [InlineData("0 USD", 0)]
    [InlineData("12 USD", 12)]
    [InlineData("0.000001 USD", 0.000001)]
    public void ParsesSpecMoney(string text, double value)
    {
        Assert.True(Money.TryParse(text, out var amount));
        Assert.Equal((decimal)value, amount);
    }

    [Theory]
    [InlineData("$0.05")]
    [InlineData("0.05usd")]
    [InlineData("0.05 EUR")]
    [InlineData("01 USD")]
    [InlineData("0.1234567 USD")]
    [InlineData(null)]
    public void RefusesAnythingElse(string? text) => Assert.False(Money.TryParse(text, out _));

    [Fact]
    public void FormatsForTheGateTheReceiptAndPeople()
    {
        Assert.Equal("0 USD", Money.ToSpec(0));
        Assert.Equal("0.000153 USD", Money.ToSpec(0.000153m));
        Assert.Equal("0.000154 USD", Money.ToSpec(0.0001531m));   // rounded up, never down
        Assert.Equal("0.000 USD", Money.ToReceipt(0));
        Assert.Equal("0.000064 USD", Money.ToReceipt(0.000064m));
        Assert.Equal("$0.05", Money.Show(0.05m));
        Assert.Equal("$0.00015", Money.Show(0.00015m));
        Assert.Equal(0.10m, Money.StepUp(0.05m));
        Assert.Equal(0.02m, Money.StepDown(0.05m));
    }
}
