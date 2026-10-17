using Byoa.Wallet.Settings;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Tests;

public class RoutingTests
{
    static readonly FakeSource RussianOnly = new("local-ru", languages: ["ru"]);
    static readonly FakeSource General = new("local-any");
    static readonly FakeSource Paid = new("openrouter-scribe", Runs.ApiKey, pricePerSecond: 0.00004m);
    static readonly ISpeechSource[] All = [RussianOnly, General, Paid];

    static WalletSettings Defaults()
    {
        var s = new WalletSettings();
        Router.EnsureDefaults(s);
        return s;
    }

    /// <summary>The default settings with a stop for each of the fake sources, as the provider set makes them.</summary>
    static WalletSettings WithAll()
    {
        var s = Defaults();
        SpeechRoutes.Ensure(s, All);
        return s;
    }

    static RoutePlan Plan(WalletSettings s, string? language) =>
        Router.Plan(s.Chain(Vocabulary.Transcribe), All, id => s.Source(id).Enabled, language);

    [Fact]
    public void ANewWalletsSpeechRouteHoldsOnlyThePaidFallback()
    {
        Assert.Equal(["openrouter-scribe"], Defaults().Chain(Vocabulary.Transcribe).Stops.Select(s => s.SourceId));
    }

    [Fact]
    public void TheEnginesOfEarlierVersionsAreDroppedFromSettingsAndRoutes()
    {
        var settings = new WalletSettings();
        foreach (var id in Router.RetiredSpeechIds) settings.Source(id).Enabled = false;
        settings.Chain(Vocabulary.Transcribe).Stops.AddRange(
            new[] { "gigaam-v3", "whisper", "whisper-cpu", "local-extra", "openrouter-scribe" }.Select(id => new ChainStop { SourceId = id }));
        settings.Chain(Vocabulary.Transcribe, "ru").Stops.Add(new ChainStop { SourceId = "gigaam-v3" });

        Router.EnsureDefaults(settings);

        Assert.Equal(["local-extra", "openrouter-scribe"], settings.Chain(Vocabulary.Transcribe).Stops.Select(s => s.SourceId));
        Assert.Empty(settings.Chain(Vocabulary.Transcribe, "ru").Stops);
        Assert.DoesNotContain(settings.Sources, s => Router.RetiredSpeechIds.Contains(s.Id));
    }

    [Fact]
    public void ASpeechModelTheVisitorAddedGoesBeforeThePaidFallback()
    {
        var settings = Defaults();
        settings.Sources.Add(new SourceSetting
        {
            Id = "openai-compatible:qwen3-asr@http://127.0.0.1:9931/v1#transcribe", Kind = SourceKinds.OpenAiCompatible,
            Model = "qwen3-asr", Endpoint = "http://127.0.0.1:9931/v1", Skills = [Vocabulary.Transcribe],
        });
        settings.Sources.Add(new SourceSetting
        {
            Id = "lmstudio:qwen3.5@http://127.0.0.1:1234/v1", Kind = SourceKinds.LmStudio, Model = "qwen3.5", Skills = [Vocabulary.Generate],
        });

        Router.EnsureSpeechChainFromSettings(settings);
        Router.EnsureSpeechChainFromSettings(settings);                       // a second time adds nothing

        Assert.Equal(["openai-compatible:qwen3-asr@http://127.0.0.1:9931/v1#transcribe", "openrouter-scribe"],
            settings.Chain(Vocabulary.Transcribe).Stops.Select(s => s.SourceId));
    }

    [Fact]
    public void RussianGoesToTheRussianSourceFirst()
    {
        var plan = Plan(WithAll(), "ru");
        Assert.Equal(["local-ru", "local-any", "openrouter-scribe"], plan.Steps.Select(s => s.Source.Id));
        Assert.False(plan.Steps[0].AskFirst);
        Assert.True(plan.Steps[2].AskFirst);
    }

    [Theory]
    [InlineData("sk")]
    [InlineData("en")]
    [InlineData(null)]
    public void OtherLanguagesAndNoLanguageGoToTheGeneralSource(string? language)
    {
        var plan = Plan(WithAll(), language);
        Assert.Equal("local-any", plan.First!.Source.Id);
        Assert.Contains(plan.Skipped, k => k.Source.Id == "local-ru");
    }

    [Fact]
    public void ATurnedOffSourceIsSkipped()
    {
        var s = WithAll();
        s.Source("local-ru").Enabled = false;
        var plan = Plan(s, "ru");
        Assert.Equal("local-any", plan.First!.Source.Id);
        Assert.Equal("turned off", plan.Skipped.Single(k => k.Source.Id == "local-ru").Why);
    }

    [Fact]
    public void TheVisitorsOrderWins()
    {
        var s = WithAll();
        var stops = s.Chain(Vocabulary.Transcribe).Stops;
        (stops[0], stops[1]) = (stops[1], stops[0]);
        Assert.Equal("local-any", Plan(s, "ru").First!.Source.Id);
    }

    [Fact]
    public void AskBeforePaidCanBeTurnedOff()
    {
        var s = WithAll();
        s.Chain(Vocabulary.Transcribe).Stops.Single(x => x.SourceId == "openrouter-scribe").AskBeforePaid = false;
        Assert.False(Plan(s, "ru").Steps.Single(x => x.Source.Id == "openrouter-scribe").AskFirst);
    }

    [Fact]
    public void ASourceWithoutItsModelOrKeyIsSkipped()
    {
        var russian = new FakeSource("local-ru", languages: ["ru"]) { State = SourceState.Missing };
        var paid = new FakeSource("openrouter-scribe", Runs.ApiKey, pricePerSecond: 0.00004m) { State = SourceState.NoKey };
        var s = WithAll();
        var plan = Router.Plan(s.Chain(Vocabulary.Transcribe), [russian, General, paid], _ => true, "ru");
        Assert.Equal(["local-any"], plan.Steps.Select(x => x.Source.Id));
    }

    [Fact]
    public void ASourceWhoseRuntimeIsNotRunningIsSkippedWithItsReason()
    {
        var down = new FakeLiveSpeech("local-live") { Up = false };
        var s = new WalletSettings();
        SpeechRoutes.Ensure(s, [down, Paid]);
        var plan = Router.Plan(s.Chain(Vocabulary.Transcribe), [down, Paid], _ => true, "ru");
        Assert.Equal("not running", plan.Skipped.Single().Why);
        Assert.Equal(["openrouter-scribe"], plan.Steps.Select(x => x.Source.Id));

        down.Up = true;
        Assert.Equal("local-live", Router.Plan(s.Chain(Vocabulary.Transcribe), [down, Paid], _ => true, "ru").First!.Source.Id);
    }

    sealed class FakeLiveSpeech(string id) : FakeSource(id), ILiveness
    {
        public bool? Up { get; set; } = true;
    }

    [Fact]
    public void TheManagerExplainsItsChoiceFromProperties()
    {
        var ru = Router.Explain(Plan(WithAll(), "ru"), "ru");
        Assert.StartsWith("Manager suggests Fake local-ru for Russian because it is made for Russian", ru);
        Assert.Contains("no cloud bill", ru);
        Assert.Contains("(paid, asks first)", ru);
        var sk = Router.Explain(Plan(WithAll(), "sk"), "sk");
        Assert.Contains("Fake local-ru knows only Russian", sk);
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
