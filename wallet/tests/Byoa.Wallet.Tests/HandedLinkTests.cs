using System.Buffers.Text;
using System.Text;
using System.Text.Json.Nodes;
using Byoa.Wallet.Activity;
using Byoa.Wallet.Direct;
using Byoa.Wallet.Engine;
using Byoa.Wallet.Settings;

namespace Byoa.Wallet.Tests;

/// <summary>A link that reaches the wallet by hand (CONTRACT-PAIR.md section 1): what is taken out of the pasted text.</summary>
public class HandedLinkTextTests
{
    static readonly string Manifest64 = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(Clips.GeoManifest));

    static string Link(string session = "s3ss10n-c0de_with-enough-bits-0123456789") =>
        "bringyourownagent://connect?v=0.1&endpoint=" + Uri.EscapeDataString("wss://geo.example.org/byoa/ws") + "&manifest=" + Manifest64 + "&session=" + session;

    [Fact]
    public void AnUntouchedLinkComesBackAsItIs()
    {
        Assert.True(HandedLink.TryNormalize(Link(), out var link, out var why), why);
        Assert.Equal(Link(), link);
    }

    [Theory]
    [InlineData("  {0}  ")]
    [InlineData("\r\n{0}\r\n")]
    [InlineData("\"{0}\"")]
    [InlineData("'{0}'")]
    [InlineData("<{0}>")]
    public void SpacesQuotesBracketsAndZeroWidthCharactersAroundALinkAreTakenOut(string template)
    {
        Assert.True(HandedLink.TryNormalize(string.Format(template, Link()), out var link, out var why), why);
        Assert.Equal(Link(), link);
    }

    [Fact]
    public void NonBreakingSpacesCurlyQuotesAndZeroWidthCharactersAroundALinkAreTakenOutToo()
    {
        var nbsp = ((char)0xA0).ToString();
        foreach (var text in new[]
        {
            "(" + nbsp + Link() + nbsp + ")",
            (char)0x201C + Link() + (char)0x201D,
            (char)0x2018 + Link() + (char)0x2019,
            Link() + (char)0x200B,
            Link()[..20] + (char)0x200B + (char)0xFEFF + Link()[20..],
        })
        {
            Assert.True(HandedLink.TryNormalize(text, out var link, out var why), why);
            Assert.Equal(Link(), link);
        }
    }

    [Fact]
    public void ALinkThatAMessengerWrappedIntoShortLinesIsPutBackTogether()
    {
        var wrapped = string.Join("\r\n", Link().Chunk(48).Select(c => new string(c)));
        Assert.Contains("\r\n", wrapped);
        Assert.True(HandedLink.TryNormalize(wrapped, out var link, out var why), why);
        Assert.Equal(Link(), link);
        Assert.True(HandedLink.TryNormalize(string.Join(" \r\n  ", Link().Chunk(48).Select(c => new string(c))), out link, out why), why);   // a mail client's soft breaks and indentation
        Assert.Equal(Link(), link);
        Assert.False(HandedLink.TryNormalize(string.Join(" ", Link().Chunk(48).Select(c => new string(c))), out _, out why));   // spaces alone are more text
        Assert.StartsWith("That is not a single link.", why);
    }

    [Fact]
    public void TheSchemeIsMatchedWithoutRegardToCase()
    {
        var text = Link().Replace("bringyourownagent", "BringYourOwnAgent");
        Assert.True(HandedLink.TryNormalize(text, out var link, out var why), why);
        Assert.Equal(text, link);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \r\n  ")]
    public void NothingPastedIsSaidPlainly(string? text)
    {
        Assert.False(HandedLink.TryNormalize(text, out var link, out var why));
        Assert.Equal("", link);
        Assert.Equal("Nothing was pasted.", why);
    }

    [Theory]
    [InlineData("hello")]
    [InlineData("https://geo.example.org/byoa")]
    [InlineData("byoa://connect?v=0.1")]
    [InlineData("javascript:alert(1)")]
    public void TextThatIsNotASitesLinkIsRefusedInPlainWords(string text)
    {
        Assert.False(HandedLink.TryNormalize(text, out _, out var why));
        Assert.StartsWith("That is not a link from a site.", why);
    }

    [Fact]
    public void TextAroundTheLinkOrTwoLinksAreRefusedBecauseTheWalletActsOnExactlyOne()
    {
        Assert.False(HandedLink.TryNormalize("Open this: " + Link(), out _, out var why));
        Assert.StartsWith("That is not a single link.", why);
        Assert.False(HandedLink.TryNormalize(Link() + "\n" + Link("another-session-code-0123456789abc"), out _, out why));
        Assert.StartsWith("That is not a single link.", why);
        Assert.False(HandedLink.TryNormalize(Link() + " and then some words", out _, out _));       // the words run into the link: it is no longer one
    }

    [Fact]
    public void ATextFarLongerThanAnyLinkIsRefusedBeforeItIsLookedAt()
    {
        Assert.False(HandedLink.TryNormalize(new string('a', DirectLink.MaxLength * 2 + 1), out _, out var why));
        Assert.Equal("That text is too long to be a link.", why);
    }
}

/// <summary>The wallet end of a handed link: the consent says so, nothing reaches the site before Allow, and the visitor is told plainly what is wrong.</summary>
public class HandedLinkTransportTests : IDisposable
{
    readonly ManualTime _time = new();
    readonly ScriptedUi _ui = new();
    readonly FakeSource _general = new("local-any") { Answer = "francia-general" };
    readonly SettingsStore _store = new(null);
    readonly ActivityLog _log;
    readonly WalletEngine _engine;
    readonly PairingStore _pairings = new(null);
    readonly TestSite _site = new();
    readonly List<DirectTransport> _transports = [];

    public HandedLinkTransportTests()
    {
        _log = new ActivityLog(null, _time.Now);
        _engine = SpeechRoutes.Engine(_store, [_general], _log, _ui, _time);
    }

    DirectTransport Transport()
    {
        var t = new DirectTransport(_engine, _pairings, new DirectOptions
        {
            DevLocal = true,
            Backoff = [TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100)],
            WelcomeTimeout = TimeSpan.FromSeconds(5),
        }, _time);
        _transports.Add(t);
        return t;
    }

    public void Dispose()
    {
        foreach (var t in _transports) t.Dispose();
        _site.Dispose();
    }

    JsonObject ManifestWithCheck()
    {
        var manifest = JsonNode.Parse(Clips.GeoManifest)!.AsObject();
        manifest["connect"] = new JsonObject { ["check"] = true };
        return manifest;
    }

    [Fact]
    public async Task AHandedLinkGoesStraightToTheConsentWindowWhichSaysWhereItCameFrom()
    {
        var session = TestSite.NewCode();
        var connection = await Transport().HandleLinkAsync("  \"" + _site.Link(session) + "\"\r\n", handed: true);

        Assert.NotNull(connection);
        var model = Assert.Single(_ui.Consents);
        Assert.True(model.Handed);
        Assert.True(model.FromLink);
        Assert.Equal(ConnectCheck.Compute(session), model.CheckCode);                // the code is the same as for the same link from the browser
        Assert.Equal(1, _ui.Notices);
        Assert.Equal("link", _log.Recent().First(e => e.Kind == "request").Via);
        Assert.Contains("pasted by hand", _log.Recent().First(e => e.Kind == "request").Note);
    }

    [Fact]
    public async Task ALinkFromTheBrowserIsNotMarkedAsHanded()
    {
        await Transport().HandleLinkAsync(_site.Link());
        Assert.False(Assert.Single(_ui.Consents).Handed);
    }

    [Fact]
    public async Task AHandedLinkCountsAsTheVisitorsOwnClickEvenWhenTheCallerSaysNoClick()
    {
        var connection = await Transport().HandleLinkAsync(_site.Link(), userActivation: false, handed: true);
        Assert.NotNull(connection);                                                  // a link without a click would have been a quiet request
        Assert.Single(_ui.Consents);
        Assert.Equal(0, _ui.Quiet);
    }

    [Fact]
    public async Task AHandedLinkFromASiteThatShowsTheCodeStillNeedsTheTick()
    {
        _site.Manifest = ManifestWithCheck().ToJsonString();
        _ui.Consent = new ConsentAnswer(true, 0.2m, false, [], CheckConfirmed: false);

        Assert.Null(await Transport().HandleLinkAsync(_site.Link(), handed: true));

        var model = Assert.Single(_ui.Consents);
        Assert.True(model.Handed);
        Assert.True(model.CheckRequired);
        await Task.Delay(300);
        Assert.Equal(0, _site.Accepts);                                              // no tick, no contact
    }

    [Fact]
    public async Task AHandedLinkFromASiteWithNoCodeHasNoTickButTheConsentKnowsItCameByHand()
    {
        var connection = await Transport().HandleLinkAsync(_site.Link(), handed: true);
        Assert.NotNull(connection);
        var model = Assert.Single(_ui.Consents);
        Assert.False(model.CheckRequired);                                           // a link cannot demand a code the site never shows
        Assert.True(model.Handed);                                                   // the window uses it for the stronger warning
    }

    [Fact]
    public async Task NothingReachesTheSiteWhenTheVisitorDeclinesAHandedLink()
    {
        _ui.Notice = NoticeAnswer.Decline;
        Assert.Null(await Transport().HandleLinkAsync(_site.Link(), handed: true));
        await Task.Delay(300);
        Assert.Equal(0, _site.Accepts);
        Assert.Equal(SiteStatus.None, _engine.FindSite(_site.Origin)!.Status);       // "not now": nothing is stored
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello there")]
    [InlineData("https://geo.example.org/")]
    public async Task TextThatIsNotALinkIsIgnoredWithAnEntryInTheLogAndNoWindow(string text)
    {
        Assert.Null(await Transport().HandleLinkAsync(text, handed: true));
        Assert.Empty(_ui.Consents);
        Assert.Equal(0, _site.Accepts);
        var entry = _log.Recent().First(e => e.Kind == "link");
        Assert.Equal("invalid", entry.Outcome);
        Assert.Contains("a pasted link was ignored", entry.Note);
    }

    [Fact]
    public void TheCheckSaysNothingAboutAGoodLink()
    {
        Assert.Null(Transport().CheckHandedLink(_site.Link()));
        Assert.Null(Transport().CheckHandedLink("<" + _site.Link() + ">"));
    }

    [Fact]
    public void TheCheckExplainsInPlainWordsWhatIsWrong()
    {
        var transport = Transport();
        Assert.Equal("Nothing was pasted.", transport.CheckHandedLink(" "));
        Assert.StartsWith("That is not a link from a site.", transport.CheckHandedLink("hello"));
        Assert.Equal("That link cannot be used: the endpoint host is an IP address.",
            transport.CheckHandedLink(_site.Link().Replace(Uri.EscapeDataString($"ws://localhost:{_site.Port}/byoa/ws"), Uri.EscapeDataString("wss://10.1.2.3/byoa/ws"))));
        Assert.Equal("That link cannot be used: the link has no valid session code.", transport.CheckHandedLink(_site.Link("short")));
        var broken = "bringyourownagent://connect?v=0.1&endpoint=" + Uri.EscapeDataString("wss://geo.example.org/ws")
            + "&manifest=" + Base64Url.EncodeToString(Encoding.UTF8.GetBytes("{\"byoa\":\"nope\"}")) + "&session=" + TestSite.NewCode();
        Assert.Equal("That link cannot be used: the site's request is not valid.", transport.CheckHandedLink(broken));
    }

    [Fact]
    public async Task TheCheckSaysSoWhenTheSiteIsBlockedOrTheLinkWasAlreadyUsed()
    {
        var transport = Transport();
        var session = TestSite.NewCode();
        Assert.NotNull(await transport.HandleLinkAsync(_site.Link(session), handed: true));
        Assert.Equal("That link was already used. Press Connect on the site's page to get a new one.", transport.CheckHandedLink(_site.Link(session)));

        _engine.Block(_site.Origin, true);
        Assert.Equal("You blocked localhost:" + _site.Port + " in this wallet. Unblock it in Sites first.", transport.CheckHandedLink(_site.Link()));
    }
}
