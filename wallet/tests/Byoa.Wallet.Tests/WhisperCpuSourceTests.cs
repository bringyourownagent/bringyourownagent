using Byoa.Wallet.Providers;
using Byoa.Wallet.Protocol;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Tests;

/// <summary>The CPU Whisper: the small model, its own state line, and its place as the fallback.</summary>
public class WhisperCpuSourceTests
{
    [Fact]
    public void ItIsTheLocalCpuFallbackWithItsOwnSmallModel()
    {
        var oldHome = Environment.GetEnvironmentVariable(WalletPaths.HomeVariable);
        Environment.SetEnvironmentVariable(WalletPaths.HomeVariable, Path.Combine(AppContext.BaseDirectory, "no-wallet-here"));
        try
        {
            using var source = new WhisperCpuSource(() => [], new HttpClient());
            Assert.Equal(Router.CpuWhisperId, source.Id);
            Assert.Equal("Whisper small-q5_1 (CPU)", source.Name);
            Assert.Equal(Runs.ThisDevice, source.Runs);
            Assert.Equal("local-model", source.ProviderKind);
            Assert.False(source.Paid);
            Assert.Null(source.Languages);                        // every language Whisper knows
            Assert.Equal(SourceState.NeedsDownload, source.State);
            Assert.Equal("not downloaded yet (191 MB), downloads on first use", source.StateText);
            Assert.Equal(191_073_507, source.DownloadSize);
        }
        finally
        {
            Environment.SetEnvironmentVariable(WalletPaths.HomeVariable, oldHome);
        }
    }

    [Fact]
    public void TheSmallModelIsInTheCatalogOfKnownDownloads()
    {
        Assert.Equal("ggml-small-q5_1.bin", WhisperCpuSource.ModelFile);
        Assert.Equal("small-q5_1", WhisperSource.ModelLabel(WhisperCpuSource.ModelFile));
    }
}

/// <summary>The answer hygiene the wallet applies to everything a model writes back.</summary>
public class TextsTests
{
    [Theory]
    [InlineData("<think>let me see…</think>Paris", "Paris")]
    [InlineData("<think>never closed, all of it is reasoning", "")]
    [InlineData("  Paris  ", "Paris")]
    [InlineData("", "")]
    public void CleanAnswerKeepsOnlyTheAnswer(string raw, string clean) => Assert.Equal(clean, Texts.CleanAnswer(raw));

    [Theory]
    [InlineData("  \"YES\"! ", "yes")]
    [InlineData("NO.", "no")]
    [InlineData("\"no\"", "no")]
    [InlineData("maybe", null)]
    public void MatchChoiceTakesTheDressingOff(string answer, string? expected)
    {
        Assert.Equal(expected, Texts.MatchChoice(answer, ["yes", "no"]));
    }

    [Fact]
    public void AMatchReturnsTheChoiceExactlyAsThePageListedIt()
    {
        Assert.Equal("Oui", Texts.MatchChoice("oui!", ["No", "Oui"]));
        Assert.Null(Texts.MatchChoice("perhaps", ["yes", "no"]));
    }

    [Fact]
    public void TheInstructionListsTheChoicesForThePrompt()
    {
        Assert.Equal("Answer with exactly one of the following and nothing else: \"yes\" | \"no\".",
            Texts.ChoicesInstruction(["yes", "no"]));
    }
}
