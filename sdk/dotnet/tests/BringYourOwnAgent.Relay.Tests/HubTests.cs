using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;

namespace BringYourOwnAgent.Relay.Tests;

/// <summary>The site's side of the wallet connected without the extension: one-time codes, their check code, pairings, and the rule
/// that one room can never reach another room's wallet.</summary>
public class HubTests
{
    static (DirectHub Hub, ManualClock Clock) Make(Action<RelayOptions>? change = null)
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero));
        return (Rooms.Hub(clock, change), clock);
    }

    [Fact]
    public void ACodeIsLongRandomUrlSafeAndBelongsToTheRoomThatAskedForIt()
    {
        var (hub, _) = Make();
        var a = hub.NewCode(Rooms.A, "203.0.113.5");
        var b = hub.NewCode(Rooms.A, "203.0.113.5");
        Assert.Matches(new Regex("^[A-Za-z0-9_-]{32,}$"), a);                       // 192 bits, base64url without padding
        Assert.NotEqual(a, b);
        Assert.Equal("waiting", hub.CodeState(Rooms.A, a).State);
        Assert.Equal("expired", hub.CodeState(Rooms.B, a).State);                   // another room's page cannot ask about it
        Assert.Equal("expired", hub.CodeState(Rooms.A, "made-up-code").State);
        Assert.Equal("expired", hub.CodeState(Rooms.A, null).State);
    }

    [Fact]
    public void EveryCodeComesWithItsCheckCodeAndItsLink()
    {
        var (hub, _) = Make();
        var issued = hub.NewCode(Rooms.A, "203.0.113.5", "wss://chat.example/byoa/ws") ?? throw new Xunit.Sdk.XunitException("no code");
        Assert.Equal(ConnectCheck.Compute(issued.Code), issued.Check);              // §3a: the site computes it from its own session
        Assert.Matches("^[ABCDEFGHJKMNPQRSTUVWXYZ23456789]{4}$", issued.Check);
        Assert.Contains(Uri.EscapeDataString(issued.Code), issued.Link);
        Assert.Equal(DirectHub.Link("wss://chat.example/byoa/ws", hub.Manifest, issued.Code), issued.Link);
    }

    [Fact]
    public void ARoomIdOfTheWrongShapeIsNeverAccepted()
    {
        var (hub, _) = Make();
        Assert.False(DirectHub.IsRoomId(null));
        Assert.False(DirectHub.IsRoomId("short"));
        Assert.False(DirectHub.IsRoomId(new string('x', 65)));
        Assert.False(DirectHub.IsRoomId("room with spaces here"));
        Assert.False(DirectHub.IsRoomId("room/with/slashes/here"));
        Assert.True(DirectHub.IsRoomId("8f14e45f-ceea-467a-9c1c-5b0d3a1e77aa"));    // what crypto.randomUUID makes
        Assert.True(DirectHub.IsRoomId(new string('a', 16)));
        Assert.Null(hub.NewCode("short", "203.0.113.5", "ws://localhost/byoa/ws"));
    }

    [Fact]
    public void TheLinkCarriesTheManifestTheSiteServes()
    {
        var (hub, _) = Make();
        var issued = hub.NewCode(Rooms.A, "203.0.113.5", "wss://chat-to-agents.appshub.net/byoa/ws") ?? throw new Xunit.Sdk.XunitException("no code was issued");
        var link = issued.Link;
        Assert.StartsWith("bringyourownagent://connect?v=0.1&endpoint=", link);
        Assert.Contains("&manifest=", link);
        Assert.Contains("&session=", link);
        Assert.Contains(Uri.EscapeDataString("wss://chat-to-agents.appshub.net/byoa/ws"), link);
        Assert.EndsWith(Uri.EscapeDataString(issued.Code), link);

        // the manifest is the site's own, compact, base64url, no padding, and inside the 4096 bytes a link carries
        var node = JsonDocument.Parse(TestManifest.Json).RootElement;
        var compact = JsonSerializer.Serialize(node);
        var decoded = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(hub.Manifest));
        Assert.True(Encoding.UTF8.GetByteCount(decoded) <= hub.Options.MaxManifestBytes);
        Assert.Equal(JsonDocument.Parse(compact).RootElement.GetProperty("privacy").GetString(), JsonDocument.Parse(decoded).RootElement.GetProperty("privacy").GetString());
        Assert.Equal("personal", JsonDocument.Parse(decoded).RootElement.GetProperty("privacy").GetString());
        Assert.DoesNotContain('=', hub.Manifest);
        Assert.DoesNotContain('+', hub.Manifest);
        Assert.DoesNotContain('/', hub.Manifest);
    }

    [Fact]
    public void AManifestThatIsNotAnObjectOrIsTooBigIsRefusedWhenTheSiteStarts()
    {
        Assert.Throws<InvalidOperationException>(() => DirectHub.Encode("[]"));                  // not an object
        Assert.ThrowsAny<JsonException>(() => DirectHub.Encode("not json"));                     // not JSON at all
        Assert.ThrowsAny<JsonException>(() => DirectHub.Encode(""));                             // nothing at all
        var big = "{\"byoa\":\"0.1-draft\",\"privacy\":\"" + new string('x', 5000) + "\"}";
        Assert.Throws<InvalidOperationException>(() => DirectHub.Encode(big));
        Assert.NotNull(DirectHub.Encode(big, maxBytes: 6000));                      // the cap is the site's own
        Assert.Throws<InvalidOperationException>(() => Rooms.Hub(change: o => o.MaxManifestBytes = 100));      // and the hub uses it
        Assert.NotNull(Rooms.Hub(change: o => o.MaxManifestBytes = 20_000).Manifest);
    }

    [Fact]
    public void TheConnectCheckFlagIsAddedToTheManifestOfTheLinkAndStaysSingle()
    {
        var plain = DirectHub.Encode(TestManifest.Json);
        var flagged = DirectHub.Encode(TestManifest.Json, connectCheck: true);
        var json = JsonDocument.Parse(Encoding.UTF8.GetString(Base64Url.DecodeFromChars(flagged))).RootElement;
        Assert.True(json.GetProperty("connect").GetProperty("check").GetBoolean());
        Assert.Equal(plain, DirectHub.Encode(TestManifest.Json, connectCheck: false));            // without the flag the manifest is untouched
        var already = DirectHub.Encode(Encoding.UTF8.GetString(Base64Url.DecodeFromChars(flagged)), connectCheck: true);
        Assert.Equal(flagged, already);                                                           // adding it twice changes nothing
        Assert.Equal("personal", json.GetProperty("privacy").GetString());                        // the rest of the manifest is left alone

        // a site that shows the code: its links carry the flag, and the wallet is the one that demands the comparison (§3a)
        var hub = Rooms.Hub(change: o => o.ConnectCheck = true);
        var issued = hub.NewCode(Rooms.A, "203.0.113.5", "wss://chat.example/byoa/ws") ?? throw new Xunit.Sdk.XunitException("no code");
        var carried = JsonDocument.Parse(Encoding.UTF8.GetString(Base64Url.DecodeFromChars(hub.Manifest))).RootElement;
        Assert.True(carried.GetProperty("connect").GetProperty("check").GetBoolean());
        Assert.Contains(Uri.EscapeDataString(hub.Manifest), issued.Link);
    }

    [Fact]
    public void ACodeIsSingleUseAndItsSecondWalletIsTurnedAway()
    {
        var (hub, _) = Make();
        var code = hub.NewCode(Rooms.A, "203.0.113.5");
        var token = hub.AcceptToken(Rooms.Channel(), code, null);
        Assert.Null(hub.Accept(Rooms.Channel(), code, null));                       // the second wallet with the same code gets bad-session
        Assert.Equal(("connected", token), hub.CodeState(Rooms.A, code));
        Assert.Matches(new Regex("^[A-Za-z0-9_-]{32,}$"), token);
        Assert.NotEqual(code, token);
    }

    [Fact]
    public void ACodeExpiresAfter120SecondsAndAnIdlePairingAfterADay()
    {
        var (hub, clock) = Make();
        var code = hub.NewCode(Rooms.A, "203.0.113.5");
        clock.Advance(TimeSpan.FromSeconds(119));
        Assert.Equal("waiting", hub.CodeState(Rooms.A, code).State);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal("expired", hub.CodeState(Rooms.A, code).State);
        Assert.Null(hub.Accept(Rooms.Channel(), code, null));

        var live = Rooms.Channel();
        var token = hub.AcceptToken(live, hub.NewCode(Rooms.A, "203.0.113.5"), null);
        hub.Gone(live);                                                             // the wallet went away
        clock.Advance(TimeSpan.FromHours(25));                                      // and stayed away for a day
        Assert.Equal(PairingState.Unknown, hub.Status(Rooms.A, token));
        Assert.Equal(0, hub.Pairings);
    }

    [Fact]
    public void TheTimesAndCapsOfTheSiteAreItsOwn()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero));
        var hub = Rooms.Hub(clock, o => { o.CodeLifeSeconds = 5; o.PairingIdleHours = 1; });
        Assert.Equal(TimeSpan.FromSeconds(5), hub.CodeLife);
        var code = hub.NewCode(Rooms.A, "203.0.113.5");
        clock.Advance(TimeSpan.FromSeconds(6));
        Assert.Equal("expired", hub.CodeState(Rooms.A, code).State);

        var live = Rooms.Channel();
        var token = hub.AcceptToken(live, hub.NewCode(Rooms.A, "203.0.113.5"), null);
        hub.Gone(live);
        clock.Advance(TimeSpan.FromMinutes(61));
        Assert.Equal(PairingState.Unknown, hub.Status(Rooms.A, token));
    }

    [Fact]
    public void ACancelledCodeIsDead()
    {
        var (hub, _) = Make();
        var code = hub.NewCode(Rooms.A, "203.0.113.5");
        Assert.False(hub.Cancel(Rooms.B, code));                                     // another room cannot cancel it
        Assert.True(hub.Cancel(Rooms.A, code));
        Assert.Equal("expired", hub.CodeState(Rooms.A, code).State);
        Assert.Null(hub.Accept(Rooms.Channel(), code, null));
    }

    [Fact]
    public void CodesAreLimitedPerAddressPerMinute()
    {
        var (hub, clock) = Make();
        for (var i = 0; i < 10; i++) Assert.NotNull(hub.NewCode(Rooms.A, "203.0.113.5", "ws://localhost/byoa/ws"));
        Assert.Null(hub.NewCode(Rooms.A, "203.0.113.5", "ws://localhost/byoa/ws"));
        Assert.Null(hub.NewCode(Rooms.B, "203.0.113.5", "ws://localhost/byoa/ws"));  // per address, not per room
        Assert.NotNull(hub.NewCode(Rooms.A, "198.51.100.9", "ws://localhost/byoa/ws"));
        clock.Advance(TimeSpan.FromSeconds(61));
        Assert.NotNull(hub.NewCode(Rooms.A, "203.0.113.5", "ws://localhost/byoa/ws"));

        var (hub6, _) = Make(o => o.CodesPerMinute = 2);
        Assert.NotNull(hub6.NewCode(Rooms.A, "2001:db8:1:2::1", "ws://localhost/byoa/ws"));
        Assert.NotNull(hub6.NewCode(Rooms.A, "2001:db8:1:2::2", "ws://localhost/byoa/ws"));
        Assert.Null(hub6.NewCode(Rooms.A, "2001:db8:1:2:ffff::3", "ws://localhost/byoa/ws"));       // the same /64: one visitor
    }

    [Fact]
    public void HelloNeedsAFreshCodeOrAKnownPairing()
    {
        var (hub, _) = Make();
        Assert.Null(hub.Accept(Rooms.Channel(), null, null));
        Assert.Null(hub.Accept(Rooms.Channel(), "no-such-code", null));
        Assert.Null(hub.Accept(Rooms.Channel(), null, "no-such-pairing"));
    }

    [Fact]
    public void AWalletReconnectsWithItsPairingAndTheNewestConnectionWins()
    {
        var (hub, _) = Make();
        var first = Rooms.Channel();
        var token = hub.AcceptToken(first, hub.NewCode(Rooms.A, "203.0.113.5"), null);
        Assert.Equal(PairingState.Live, hub.Status(Rooms.A, token));
        var second = Rooms.Channel();
        Assert.Equal(token, hub.AcceptToken(second, null, token));                   // at most one connection per pairing
        hub.Gone(first);                                                            // the old one ending does not take the new one down
        Assert.Equal(PairingState.Live, hub.Status(Rooms.A, token));
        Assert.Same(second, hub.Channel(Rooms.A, token));
        hub.Gone(second);
        Assert.Equal(PairingState.Offline, hub.Status(Rooms.A, token));
        Assert.Null(hub.Channel(Rooms.A, token));
    }

    [Fact]
    public void OneRoomCanNeverReachAnotherRoomsWallet()
    {
        var (hub, _) = Make();
        var wallet = Rooms.Channel();
        var token = hub.AcceptToken(wallet, hub.NewCode(Rooms.A, "203.0.113.5"), null);

        Assert.Same(wallet, hub.Channel(Rooms.A, token));
        Assert.Null(hub.Channel(Rooms.B, token));                                   // room B's page gets nothing
        Assert.Null(hub.Channel(null, token));
        Assert.Null(hub.Channel(Rooms.A, "made-up"));
        Assert.Equal(PairingState.Unknown, hub.Status(Rooms.B, token));
        Assert.Null(hub.Grant(Rooms.B, token));
        Assert.False(hub.Release(Rooms.B, token));                                  // and cannot let it go either
        Assert.Equal(PairingState.Live, hub.Status(Rooms.A, token));
    }

    [Fact]
    public void ThePageLetsItsWalletGoAndThePairingIsForgotten()
    {
        var (hub, _) = Make();
        var wallet = Rooms.Channel();
        var token = hub.AcceptToken(wallet, hub.NewCode(Rooms.A, "203.0.113.5"), null);
        Assert.True(hub.Release(Rooms.A, token));
        Assert.Equal(PairingState.Unknown, hub.Status(Rooms.A, token));
        Assert.Null(hub.Accept(Rooms.Channel(), null, token));                      // the token is dead for good
        Assert.False(hub.Release(Rooms.A, token));
        Assert.Equal(0, hub.Pairings);
    }

    [Fact]
    public void AFullTableMakesRoomInsteadOfRefusing()
    {
        var (hub, _) = Make(o => o.MaxPairings = 3);
        var wallets = new List<WalletChannel>();
        for (var i = 0; i < 3; i++)
        {
            var wallet = Rooms.Channel();
            wallets.Add(wallet);
            hub.AcceptToken(wallet, hub.NewCode(Rooms.A, $"203.0.113.{i}"), null);
        }
        Assert.Equal(3, hub.Pairings);
        Assert.Null(hub.Accept(Rooms.Channel(), hub.NewCode(Rooms.A, "198.51.100.9"), null));      // every wallet is still connected: no room
        hub.Gone(wallets[0]);                                                                      // the oldest wallet leaves
        Assert.NotNull(hub.AcceptToken(Rooms.Channel(), hub.NewCode(Rooms.A, "198.51.100.9"), null));
        Assert.Equal(3, hub.Pairings);                                                             // its slot was reused, not grown
    }

    [Fact]
    public async Task TheWalletTellsTheSiteItsSkillsLimitsAndParticipants()
    {
        var (hub, _) = Make();
        var (server, wallet, tcp) = await WsPeer.Pair();
        using var _t = tcp;
        var run = WalletChannel.RunAsync(server, hub, NullLogger.Instance, CancellationToken.None);
        await wallet.Send(Hello.Json(hub.NewCode(Rooms.A, "203.0.113.5"), null, ["text.generate"], Hello.SaneLimits, Hello.FourModels));
        var welcome = await wallet.NextOf("welcome");
        var token = welcome.GetProperty("pairing").GetString()!;

        var grant = hub.Grant(Rooms.A, token)!;
        Assert.Equal(["m1", "m2", "m3", "m4"], grant.Models.Select(m => m.Ref));
        Assert.Equal(["model", "model", "model", "agent"], grant.Models.Select(m => m.Kind));       // the agent is told apart by its id
        Assert.Equal("Claude Code (Opus)", grant.Models[3].Name);
        Assert.Equal("subscription", grant.Models[3].Runs);
        Assert.Equal(["text.generate"], grant.Skills);
        Assert.Equal(60000, grant.Limits.MaxTextChars);
        Assert.Equal(4000, grant.Limits.MaxAnswerTokens);
        Assert.Equal(4, grant.Limits.MaxParallelCalls);
        Assert.Null(hub.Grant(Rooms.B, token));                                                     // and only its own room sees it

        await wallet.Ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(PairingState.Offline, hub.Status(Rooms.A, token));
    }

    [Fact]
    public async Task AHelloWithoutLimitsGetsTheContractsFourParallelCalls()
    {
        var (hub, _) = Make();
        var (server, wallet, tcp) = await WsPeer.Pair();
        using var _t = tcp;
        var run = WalletChannel.RunAsync(server, hub, NullLogger.Instance, CancellationToken.None);
        await wallet.Send(Hello.Json(hub.NewCode(Rooms.A, "203.0.113.5"), null));
        var token = (await wallet.NextOf("welcome")).GetProperty("pairing").GetString()!;
        var channel = hub.Channel(Rooms.A, token)!;
        Assert.Equal(4, channel.Limits.MaxParallelCalls);                    // CONTRACT-DIRECT.md §4: 4 when the hello has no limits
        Assert.Equal(100_000, channel.Limits.MaxTextChars);
        Assert.Empty(channel.Models);                                        // a site that asked for no models gets none
        Assert.False(channel.Knows("m1"));
        await wallet.Ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void APairingTokenIsASecretOf192Bits()
    {
        var seen = new HashSet<string>();
        for (var i = 0; i < 50; i++)
        {
            var token = DirectHub.NewSecret();
            Assert.True(seen.Add(token));
            Assert.True(Encoding.UTF8.GetByteCount(token) >= 22);                   // CONTRACT-DIRECT.md §4: at least 22 characters
            Assert.All(token, c => Assert.True(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'));
        }
    }
}