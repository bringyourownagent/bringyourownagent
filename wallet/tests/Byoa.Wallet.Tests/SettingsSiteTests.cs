using Byoa.Wallet.Desktop;
using Byoa.Wallet.Settings;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Tests;

public sealed class SettingsSiteTests
{
    [Fact]
    public void AllowedSiteShowsTheSkillThatNoSourceCanServe()
    {
        var source = new FakeSource("local-any", languages: ["ru"]) { State = SourceState.Unavailable };
        var settings = new WalletSettings
        {
            Chains =
            [
                new SkillChain
                {
                    Skill = Vocabulary.Transcribe,
                    Stops = [new ChainStop { SourceId = source.Id }],
                },
            ],
        };
        var site = new SiteRecord
        {
            Origin = "https://example.test",
            Status = SiteStatus.Allowed,
            Skills = [Vocabulary.Transcribe],
            Languages = ["ru"],
        };

        Assert.Equal(Vocabulary.Transcribe,
            SettingsWindow.MissingRequiredSkill(site, settings, [source], []));
    }

    [Fact]
    public void AllowedSiteHasNoWarningWhenItsTextRouteIsReady()
    {
        var source = new FakeTextSource("local", "local-model");
        var settings = new WalletSettings
        {
            Chains =
            [
                new SkillChain
                {
                    Skill = Vocabulary.Generate,
                    Stops = [new ChainStop { SourceId = source.Id }],
                },
            ],
        };
        var site = new SiteRecord
        {
            Origin = "https://example.test",
            Status = SiteStatus.Allowed,
            Skills = [Vocabulary.Generate],
        };

        Assert.Null(SettingsWindow.MissingRequiredSkill(site, settings, [], [source]));
    }
}
