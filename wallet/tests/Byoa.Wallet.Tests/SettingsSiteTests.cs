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
    public void AllowedSiteShowsPicturesAsServedOnlyWhileOneOfItsPictureModelsIsReady()
    {
        var model = new FakeImageSource("openrouter:google/nano");
        var settings = new WalletSettings();
        var site = new SiteRecord { Origin = "https://example.test", Status = SiteStatus.Allowed, Skills = [Vocabulary.ImageGenerate] };
        Assert.Equal(Vocabulary.ImageGenerate, SettingsWindow.MissingRequiredSkill(site, settings, [], [model]));          // no picture model was given to it

        site.Models.Add(new SiteModels { Skill = Vocabulary.ImageGenerate, SourceIds = [model.Id] });
        Assert.Null(SettingsWindow.MissingRequiredSkill(site, settings, [], [model]));

        model.State = SourceState.NoKey;                                                                                   // its key is gone
        Assert.Equal(Vocabulary.ImageGenerate, SettingsWindow.MissingRequiredSkill(site, settings, [], [model]));
        model.State = SourceState.Ready;
        settings.Sources.Add(new SourceSetting { Id = model.Id, Kind = SourceKinds.OpenRouter, Model = "google/nano", Enabled = false });
        Assert.Equal(Vocabulary.ImageGenerate, SettingsWindow.MissingRequiredSkill(site, settings, [], [model]));          // the visitor turned it off
        Assert.Equal(Vocabulary.ImageGenerate, SettingsWindow.MissingRequiredSkill(site, settings, [], [new FakeTextSource("openrouter:google/nano")]));   // not a source that draws
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
