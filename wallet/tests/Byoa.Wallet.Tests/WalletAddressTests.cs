using Byoa.Wallet.Android;

namespace Byoa.Wallet.Tests;

/// <summary>
/// The addresses and codes the Android wallet takes from another device's screen (<see cref="WalletAddress"/>, whose file is compiled into these tests): the
/// web wallet's address with the link in its fragment is taken, and only that shape; a site's hand-off address is for the browser; everything else is nothing.
/// </summary>
public class WalletAddressTests
{
    const string Link = "bringyourownagent://connect?v=0.1&endpoint=wss%3A%2F%2Fsite.example%2Fbyoa%2Fws&manifest=eyJieW9hIjoiMC4xLWRyYWZ0In0&session=Zm9vYmFyYmF6cXV4";
    const string Wallet = "https://wallet.bringyourownagent.org/";

    [Fact]
    public void TheLinkInTheFragmentOfTheWebWalletsAddressIsTaken()
    {
        Assert.True(WalletAddress.TryLinkFromAddress(Wallet + "#link=" + Link, out var link));
        Assert.Equal(Link, link);                       // as it is: the wallet's own checks come next, on the link
    }

    [Fact]
    public void TheLinkWrittenAsOnePercentEncodedValueIsTakenToo()
    {
        Assert.True(WalletAddress.TryLinkFromAddress(Wallet + "#link=" + Uri.EscapeDataString(Link), out var link));
        Assert.Equal(Link, link);
    }

    [Fact]
    public void TheHostAndTheSchemeOfTheLinkMayBeInAnyCase()
    {
        Assert.True(WalletAddress.TryLinkFromAddress("https://WALLET.BringYourOwnAgent.org/#link=" + Link, out _));
        Assert.True(WalletAddress.TryLinkFromAddress(Wallet + "#link=BringYourOwnAgent://Connect?" + Link["bringyourownagent://connect?".Length..], out _));
    }

    [Fact]
    public void AQueryOnTheAddressIsIgnoredNotTrusted()
    {
        Assert.True(WalletAddress.TryLinkFromAddress(Wallet + "?utm_source=chat#link=" + Link, out var link));
        Assert.Equal(Link, link);
    }

    [Theory]
    [InlineData("http://wallet.bringyourownagent.org/#link=" + Link)]                                   // not https
    [InlineData("https://other.example/#link=" + Link)]                                                 // another host
    [InlineData("https://wallet.bringyourownagent.org.evil.example/#link=" + Link)]                     // a host that only starts like it
    [InlineData("https://evil-wallet.bringyourownagent.org/#link=" + Link)]                             // a host that only ends like it
    [InlineData("https://sub.wallet.bringyourownagent.org/#link=" + Link)]                              // a subdomain
    [InlineData("https://wallet.bringyourownagent.org@evil.example/#link=" + Link)]                     // the real host is after the @
    [InlineData("https://user:pass@wallet.bringyourownagent.org/#link=" + Link)]                        // a user name
    [InlineData("https://wallet.bringyourownagent.org:8443/#link=" + Link)]                             // another port
    [InlineData("https://wallet.bringyourownagent.org/other#link=" + Link)]                             // another path
    [InlineData("https://wallet.bringyourownagent.org/#" + Link)]                                       // no "link=" before the link
    [InlineData("https://wallet.bringyourownagent.org/#Link=" + Link)]                                  // the key is "link"
    [InlineData("https://wallet.bringyourownagent.org/#other=" + Link)]                                 // another fragment
    [InlineData("https://wallet.bringyourownagent.org/ #link=" + Link)]
    [InlineData("https://wallet.bringyourownagent.org/")]                                               // no fragment
    [InlineData("https://wallet.bringyourownagent.org/#")]
    [InlineData("https://wallet.bringyourownagent.org/#link=")]                                         // nothing in it
    [InlineData("https://wallet.bringyourownagent.org/#link=https://evil.example/byoa/ws")]              // not a link from a site
    [InlineData("https://wallet.bringyourownagent.org/#link=javascript:alert(1)")]
    [InlineData("https://wallet.bringyourownagent.org/#link=bringyourownagent://other?x=1")]            // a link, but not the connect one
    [InlineData("https://wallet.bringyourownagent.org/#link= " + Link)]                                 // a space before it
    [InlineData("https://wallet.bringyourownagent.org/#link=bringyourownagent%3a%zz")]                  // a bad percent escape
    [InlineData("bringyourownagent://connect?v=0.1")]                                                   // a link is not an address
    [InlineData("")]
    [InlineData(null)]
    public void WhatIsNotThatShapeIsNotTaken(string? address)
    {
        Assert.False(WalletAddress.TryLinkFromAddress(address, out var link));
        Assert.Equal("", link);
    }

    [Fact]
    public void AnOversizeAddressIsNotLookedAt()
    {
        Assert.False(WalletAddress.TryLinkFromAddress(Wallet + "#link=" + Link + new string('a', 40 * 1024), out _));
    }

    [Fact]
    public void TheCameraTakesALinkTheWebWalletsAddressAndASitesHandOffPage()
    {
        var raw = WalletAddress.Read("  " + Link + "\n");
        Assert.Equal(CodeKind.Link, raw.Kind);
        Assert.Equal(Link, raw.Value);

        var web = WalletAddress.Read(Wallet + "#link=" + Link);
        Assert.Equal(CodeKind.Link, web.Kind);
        Assert.Equal(Link, web.Value);

        var hand = WalletAddress.Read("https://game.example/byoa/hand/AbCdEf123_-xyz");
        Assert.Equal(CodeKind.HandOff, hand.Kind);                       // opened in the browser: the wallet does not act on it
        Assert.Equal("https://game.example/byoa/hand/AbCdEf123_-xyz", hand.Value);
    }

    [Fact]
    public void TheCameraSaysPlainlyWhenACodeIsNotASitesAndOpensNothing()
    {
        var noFragment = WalletAddress.Read(Wallet);
        Assert.Equal(CodeKind.Nothing, noFragment.Kind);
        Assert.Equal(WalletAddress.NoLinkLine, noFragment.Line);

        foreach (var text in new[]
        {
            null, "", "hello", "https://example.com/", "http://game.example/byoa/hand/abc", "https://game.example/byoa/hand/", "https://game.example/byoa/hand",
            "https://user@game.example/byoa/hand/abc", "https://game.example:8443/byoa/hand/abc", "bringyourownagent://other?x=1", "mailto:a@b.example", "javascript:alert(1)",
            "https://wallet.bringyourownagent.org/#link=https://evil.example/",
        })
        {
            var reading = WalletAddress.Read(text);
            Assert.Equal(CodeKind.Nothing, reading.Kind);
            Assert.Null(reading.Value);
            Assert.False(string.IsNullOrEmpty(reading.Line));
        }
    }
}
