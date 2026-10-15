using Byoa.Wallet.Settings;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Tests;

/// <summary>How the text chain is built and walked: new sources slot in before the paid stop, and a source that
/// cannot run right now is skipped with a reason people can read.</summary>
public class TextRoutingTests
{
    static readonly FakeTextSource Local1 = new("lmstudio:qwen/qwen3.5-4b");
    static readonly FakeTextSource Local2 = new("ollama:llama3.2:3b");
    static readonly FakeTextSource Paid = new("openrouter:google/gemma-3-12b-it", Runs.ApiKey, costPerCall: 0.004m);
    static readonly ITextSource[] All = [Local1, Local2, Paid];

    static WalletSettings Defaults()
    {
        var s = new WalletSettings();
        Router.EnsureTextChain(s, All);
        return s;
    }

    static TextRoutePlan Plan(WalletSettings s, string? language = null) =>
        Router.PlanText(s.Chain(Vocabulary.Generate), All, id => s.Source(id).Enabled, language);

    [Fact]
    public void AChainWithoutTextStopsTakesEverySourceInTheOrderTheyCame()
    {
        Assert.Equal([Local1.Id, Local2.Id, Paid.Id], Defaults().Chain(Vocabulary.Generate).Stops.Select(s => s.SourceId));
        var plan = Plan(Defaults());
        Assert.Equal(All.Select(a => a.Id), plan.Steps.Select(x => x.Source.Id));
        Assert.False(plan.Steps[0].AskFirst);
        Assert.True(plan.Steps[2].AskFirst);                      // the paid stop asks before it runs
        Assert.Empty(plan.Skipped);
    }

    [Fact]
    public void ALaterLocalSourceSlotsInBeforeTheFirstPaidStop()
    {
        var s = new WalletSettings();
        Router.EnsureTextChain(s, [Paid]);                        // the paid one was added first
        var late = new FakeTextSource("lmstudio:late-model");
        Router.EnsureTextChain(s, [Paid, late]);
        Assert.Equal([late.Id, Paid.Id], s.Chain(Vocabulary.Generate).Stops.Select(x => x.SourceId));   // the local one before the paid stop

        Router.EnsureTextChain(s, [Paid, late]);                  // adding again adds nothing
        Assert.Equal(2, s.Chain(Vocabulary.Generate).Stops.Count);
    }

    [Fact]
    public void ATurnedOffSourceIsSkippedWithAReason()
    {
        var s = Defaults();
        s.Source(Local1.Id).Enabled = false;
        var plan = Plan(s);
        Assert.Equal([Local2.Id, Paid.Id], plan.Steps.Select(x => x.Source.Id));
        Assert.Equal("turned off", plan.Skipped.Single(k => k.Source.Id == Local1.Id).Why);

        s.Source(Local1.Id).Enabled = true;
        s.Chain(Vocabulary.Generate).Stops[1].Enabled = false;    // the stop itself can be off too
        Assert.Equal("turned off", Plan(s).Skipped.Single(k => k.Source.Id == Local2.Id).Why);
    }

    [Fact]
    public void ALanguageSpecificSourceWaitsForItsLanguage()
    {
        var german = new FakeTextSource("lmstudio:de-only") { Languages = ["de"] };
        var s = new WalletSettings();
        Router.EnsureTextChain(s, [german]);

        var none = Router.PlanText(s.Chain(Vocabulary.Generate), [german], _ => true, null);
        Assert.Equal("needs to know the language", none.Skipped.Single().Why);
        Assert.StartsWith("None of your sources", Router.ExplainText(none));

        var ru = Router.PlanText(s.Chain(Vocabulary.Generate), [german], _ => true, "ru");
        Assert.Equal("does not know Russian", ru.Skipped.Single().Why);

        Assert.Equal(german.Id, Router.PlanText(s.Chain(Vocabulary.Generate), [german], _ => true, "de").First!.Source.Id);   // the engine asks with the primary tag
    }

    [Fact]
    public void ASourceThatCannotRunRightNowIsSkippedWithTheReason()
    {
        var s = Defaults();

        Local1.Up = false;                                        // nothing is listening on its port
        Assert.Equal("not running", Plan(s).Skipped.Single(k => k.Source.Id == Local1.Id).Why);
        Local1.Up = true;

        Local1.State = SourceState.Missing;
        Assert.Equal("model not found", Plan(s).Skipped.Single(k => k.Source.Id == Local1.Id).Why);
        Local1.State = SourceState.NoKey;
        Assert.Equal("no API key", Plan(s).Skipped.Single(k => k.Source.Id == Local1.Id).Why);
        Local1.State = SourceState.Broken;
        Assert.Equal("failed to load", Plan(s).Skipped.Single(k => k.Source.Id == Local1.Id).Why);

        Local1.State = SourceState.Ready;                         // and back in the route when it can again
        Assert.Contains(Local1.Id, Plan(s).Steps.Select(x => x.Source.Id));
    }

    [Fact]
    public void TheManagerExplainsItsTextChoiceFromProperties()
    {
        var local = Router.ExplainText(Plan(Defaults()));
        Assert.StartsWith("Manager suggests FakeText lmstudio:qwen/qwen3.5-4b because it runs on this PC with no cloud bill.", local);
        Assert.Contains("If it cannot answer: FakeText ollama:llama3.2:3b, then FakeText openrouter:google/gemma-3-12b-it (paid, asks first).", local);

        var s = new WalletSettings();
        Router.EnsureTextChain(s, [Paid]);
        var paidFirst = Router.PlanText(s.Chain(Vocabulary.Generate), [Paid], _ => true, null);
        Assert.StartsWith("Manager suggests FakeText openrouter:google/gemma-3-12b-it because it is paid, about $0.004 for a short answer, and it asks you first.", Router.ExplainText(paidFirst));

        Assert.Equal("None of your sources can do this. Add a model in LM Studio, Ollama or Llama, or an API key, in Detailed settings.",
            Router.ExplainText(new TextRoutePlan([], [])));
    }
}
