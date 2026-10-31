using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace BringYourOwnAgent.Relay.Tests;

/// <summary>The server end of the wallet's WebSocket: the hello/welcome handshake, the grant, the calls, the stream, and the bounds.</summary>
public class ChannelTests
{
    /// <summary>A wallet connection over a real loopback socket, welcomed with a code for room <paramref name="room"/>.</summary>
    static async Task<(DirectHub Hub, WsPeer Wallet, string Token, Task Run, IDisposable Tcp)> Connect(
        string? limits = Hello.SaneLimits, string? models = Hello.FourModels, string[]? skills = null, string room = Rooms.A,
        string? capabilities = null, string? modelsBySkill = null)
    {
        var hub = Rooms.Hub();
        var (server, wallet, tcp) = await WsPeer.Pair();
        var run = WalletChannel.RunAsync(server, hub, NullLogger.Instance, CancellationToken.None);
        await wallet.Send(Hello.Json(hub.NewCode(room, "203.0.113.5"), null, skills, limits, models, capabilities, modelsBySkill));
        var welcome = await wallet.NextOf("welcome");
        return (hub, wallet, welcome.GetProperty("pairing").GetString()!, run, tcp);
    }

    [Fact]
    public async Task WelcomeEchoesTheManifestTheLinkCarriedAndCarriesAPairingOfItsOwn()
    {
        var hub = Rooms.Hub();
        var (server, wallet, tcp) = await WsPeer.Pair();
        using var _t = tcp;
        var run = WalletChannel.RunAsync(server, hub, NullLogger.Instance, CancellationToken.None);
        var issued = hub.NewCode(Rooms.A, "203.0.113.5", "ws://localhost:5291/byoa/ws") ?? throw new Xunit.Sdk.XunitException("no code");
        await wallet.Send(Hello.Json(issued.Code, null));
        var welcome = await wallet.NextOf("welcome");
        Assert.Equal(hub.Manifest, welcome.GetProperty("manifest").GetString());          // §7: the exact string the link carries
        Assert.Equal(DirectHub.Link("ws://localhost:5291/byoa/ws", hub.Manifest, issued.Code), issued.Link);
        var token = welcome.GetProperty("pairing").GetString()!;
        Assert.Matches("^[A-Za-z0-9_-]{32,}$", token);
        await wallet.Ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task TheManifestTheLinkCarriesIsTheOneWelcomeEchoesEvenWithTheConnectFlag()
    {
        var hub = Rooms.Hub(change: o => o.ConnectCheck = true);
        var (server, wallet, tcp) = await WsPeer.Pair();
        using var _t = tcp;
        var run = WalletChannel.RunAsync(server, hub, NullLogger.Instance, CancellationToken.None);
        var issued = hub.NewCode(Rooms.A, "203.0.113.5", "ws://localhost:5291/byoa/ws")!.Value;
        var link = issued.Link;
        var carried = Uri.UnescapeDataString(link[(link.IndexOf("&manifest=", StringComparison.Ordinal) + 10)..link.IndexOf("&session=", StringComparison.Ordinal)]);
        await wallet.Send(Hello.Json(issued.Code, null));
        var welcome = await wallet.NextOf("welcome");
        Assert.Equal(carried, welcome.GetProperty("manifest").GetString());               // byte for byte, flag included (§3a, §7)
        await wallet.Ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ACodeTheServerDidNotIssueGetsBadSessionAndAClose()
    {
        var hub = Rooms.Hub();
        var (server, wallet, tcp) = await WsPeer.Pair();
        using var _t = tcp;
        var run = WalletChannel.RunAsync(server, hub, NullLogger.Instance, CancellationToken.None);
        await wallet.Send(Hello.Json("made-up-session-made-up-session-0000", null));
        var error = await wallet.NextOf("error");
        Assert.Equal("bad-session", error.GetProperty("code").GetString());
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, hub.Pairings);
    }

    [Fact]
    public async Task NothingIsSentBeforeAValidHelloAndUnknownFramesAreIgnored()
    {
        var hub = Rooms.Hub();
        var (server, wallet, tcp) = await WsPeer.Pair();
        using var _t = tcp;
        var run = WalletChannel.RunAsync(server, hub, NullLogger.Instance, CancellationToken.None);
        await wallet.Send("""{"type":"result","id":1,"ok":true,"output":{"text":"hello?"}}""");      // before any hello: ignored
        await wallet.Send("42");                                                                    // JSON, but not an object: ignored
        Assert.Null(await wallet.Next(0.3));                                                        // the server said nothing
        await wallet.Send(Hello.Json(hub.NewCode(Rooms.A, "203.0.113.5"), null));
        Assert.Equal("welcome", (await wallet.NextOf("welcome")).GetProperty("type").GetString());
        await wallet.Send("""{"type":"something-new","x":1}""");                                    // a frame of a newer protocol: ignored
        await wallet.Send("""{"type":"ping"}""");
        Assert.Null(await wallet.Next(0.3));
        await wallet.Ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task MalformedJsonClosesTheConnection()
    {
        var (_, wallet, _, run, tcp) = await Connect();
        using var _t = tcp;
        await wallet.Send("{not json at all");
        var next = await wallet.Next(5);
        Assert.True(next is null || next.Value.Type == WebSocketMessageType.Close);
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AFrameOverFourMegabytesEndsTheConnection()
    {
        var (_, wallet, _, run, tcp) = await Connect();
        using var _t = tcp;
        var huge = "{\"type\":\"result\",\"id\":1,\"text\":\"" + new string('x', WalletChannel.MaxFrameBytes + 1024) + "\"}";
        await wallet.Send(huge);
        var next = await wallet.Next(10);
        Assert.True(next is null || next.Value.Type == WebSocketMessageType.Close);
        await run.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ACallGoesDownAsTextGenerateWithAStreamAndTheAnswerComesBack()
    {
        var (hub, wallet, token, run, tcp) = await Connect();
        using var _t = tcp;
        var channel = hub.Channel(Rooms.A, token)!;
        var messages = new[] { new { role = "system", content = "S" }, new { role = "user", content = "Q" } };
        var call = channel.CallAsync(WalletJobs.Skill, new { messages }, new { model = "m2", maxTokens = 1000, temperature = 0.7, stream = true },
                                     2000, TimeSpan.FromSeconds(10), CancellationToken.None);
        var asked = await wallet.NextOf("call");
        Assert.Equal("text.generate", asked.GetProperty("skill").GetString());
        Assert.Equal("m2", asked.GetProperty("params").GetProperty("model").GetString());
        Assert.True(asked.GetProperty("params").GetProperty("stream").GetBoolean());
        Assert.Equal("Q", asked.GetProperty("input").GetProperty("messages")[1].GetProperty("content").GetString());
        var id = asked.GetProperty("id").GetInt32();

        await wallet.Send(WalletFrames.Chunk(id, "one "));
        await wallet.Send(WalletFrames.Chunk(id, "two"));
        await wallet.Send(WalletFrames.Result(id, true, "one two three"));
        var answer = await call;
        Assert.Equal(WalletOutcome.Text, answer.Outcome);
        Assert.Equal("one two three", answer.Text);                        // the result replaces the pieces
        await wallet.Ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ASiteCanCallAnySkillTheGrantCovers()
    {
        var (hub, wallet, token, run, tcp) = await Connect(skills: ["text.generate", "audio.transcribe"], models: null);
        using var _t = tcp;
        var channel = hub.Channel(Rooms.A, token)!;
        var call = channel.CallAsync("audio.transcribe", new { audio = new { mime = "audio/wav", base64 = "AAAA" } }, new { language = "en" },
                                     100, TimeSpan.FromSeconds(10), CancellationToken.None);
        var asked = await wallet.NextOf("call");
        Assert.Equal("audio.transcribe", asked.GetProperty("skill").GetString());       // the skill the site named, not text.generate
        Assert.Equal("en", asked.GetProperty("params").GetProperty("language").GetString());
        await wallet.Send(WalletFrames.Result(asked.GetProperty("id").GetInt32(), true, "a transcript"));
        Assert.Equal("a transcript", (await call).Text);
        await wallet.Ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task TheStreamedPiecesAreCollectedWhileTheModelWrites()
    {
        var (hub, wallet, token, run, tcp) = await Connect();
        using var _t = tcp;
        var channel = hub.Channel(Rooms.A, token)!;
        var pieces = new List<string>();
        var call = channel.CallAsync(WalletJobs.Skill, new { messages = new[] { new { role = "user", content = "Q" } } },
                                     new { model = "m1", stream = true }, 1000, TimeSpan.FromSeconds(10), CancellationToken.None,
                                     piece => { lock (pieces) pieces.Add(piece); });
        var id = (await wallet.NextOf("call")).GetProperty("id").GetInt32();
        await wallet.Send(WalletFrames.Chunk(id, "Half "));
        await wallet.Send(WalletFrames.Chunk(id, "a thought"));
        var until = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < until && pieces.Count < 2) await Task.Delay(20);
        Assert.Equal(["Half ", "Half a thought"], pieces);                  // every piece tells the whole text so far
        await wallet.Send(WalletFrames.Result(id, true, "Half a thought"));
        Assert.Equal("Half a thought", (await call).Text);
        await wallet.Ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ARefusalIsNoAnswerAndALateResultIsIgnored()
    {
        var (hub, wallet, token, run, tcp) = await Connect();
        using var _t = tcp;
        var channel = hub.Channel(Rooms.A, token)!;
        var call = channel.CallAsync(WalletJobs.Skill, new { messages = new[] { new { role = "user", content = "Q" } } }, new { model = "m1" },
                                     8000, TimeSpan.FromSeconds(10), CancellationToken.None);
        var id = (await wallet.NextOf("call")).GetProperty("id").GetInt32();
        await wallet.Send(WalletFrames.Result(id, false));
        Assert.Equal(WalletOutcome.Failed, (await call).Outcome);

        var empty = channel.CallAsync(WalletJobs.Skill, new { messages = new[] { new { role = "user", content = "Q" } } }, new { model = "m1" },
                                      8000, TimeSpan.FromSeconds(10), CancellationToken.None);
        var id2 = (await wallet.NextOf("call")).GetProperty("id").GetInt32();
        await wallet.Send(WalletFrames.Result(id2, true, "  ​ \n "));
        var nothing = await empty;
        Assert.Equal(WalletOutcome.Text, nothing.Outcome);                  // the wallet did answer...
        Assert.Equal("", nothing.Text);                                     // ...with nothing readable: the jobs route calls that «no answer»

        await wallet.Send(WalletFrames.Result(9999, true, "made up"));   // no such call: ignored
        await wallet.Ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AWalletThatDoesNotAnswerInTimeIsNoAnswer()
    {
        var (hub, wallet, token, run, tcp) = await Connect();
        using var _t = tcp;
        var channel = hub.Channel(Rooms.A, token)!;
        var call = channel.CallAsync(WalletJobs.Skill, new { messages = new[] { new { role = "user", content = "Q" } } }, new { model = "m3" },
                                     100, TimeSpan.FromMilliseconds(200), CancellationToken.None);
        await wallet.NextOf("call");
        Assert.Equal(WalletOutcome.TimedOut, (await call).Outcome);
        await wallet.Ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AWalletsAnswerIsCutAndCleanedBeforeAPageEverSeesIt()
    {
        var (hub, wallet, token, run, tcp) = await Connect();
        using var _t = tcp;
        var channel = hub.Channel(Rooms.A, token)!;
        var call = channel.CallAsync(WalletJobs.Skill, new { messages = new[] { new { role = "user", content = "Q" } } }, new { model = "m1" },
                                     40, TimeSpan.FromSeconds(10), CancellationToken.None);
        var id = (await wallet.NextOf("call")).GetProperty("id").GetInt32();
        var nasty = "one\u0000two\n\n\n\nthree‮evil " + new string('x', 200);
        await wallet.Send(JsonSerializer.Serialize(new { type = "result", id, ok = true, output = new { text = nasty } }));
        var answer = await call;
        Assert.DoesNotContain('\u0000', answer.Text);
        Assert.DoesNotContain('‮', answer.Text);
        Assert.DoesNotContain("\n\n\n", answer.Text);
        Assert.Equal(40, answer.Text.Length);
        Assert.StartsWith("onetwo\n\nthreeevil", answer.Text);
        await wallet.Ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task TheVisitorCanChangeWhatTheSiteWasGivenWhileThePageIsOpen()
    {
        var (hub, wallet, token, run, tcp) = await Connect();
        using var _t = tcp;
        Assert.Equal(4, hub.Grant(Rooms.A, token)!.ModelsOf("text.generate").Length);
        var changed = WalletFrames.Grant(
            """{"maxTextChars":20000,"maxAnswerTokens":1000,"maxCallSeconds":60,"maxParallelCalls":2,"maxCallsPerMinute":10}""",
            """[{"ref":"m7","name":"A new model","id":"deepseek/deepseek-v3.2","runs":"api-key"}]""");
        await wallet.Send(changed);
        var until = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < until && hub.Grant(Rooms.A, token)!.ModelsOf("text.generate").Length != 1) await Task.Delay(20);
        var grant = hub.Grant(Rooms.A, token)!;
        Assert.Equal(["m7"], grant.ModelsOf("text.generate").Select(m => m.Ref));
        Assert.Equal("A new model", grant.ModelsOf("text.generate")[0].Name);
        Assert.Equal(20000, grant.Limits.MaxTextChars);
        Assert.Equal(2, grant.Limits.MaxParallelCalls);
        Assert.Equal(60, grant.Limits.MaxCallSeconds);
        await wallet.Ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ALimitThatIsSillyIsClampedToTheProtocolsCeilings()
    {
        var (hub, wallet, token, run, tcp) = await Connect(limits: """{"maxParallelCalls":9999,"maxAnswerTokens":0,"maxTextChars":-5,"maxCallSeconds":100000}""");
        using var _t = tcp;
        var limits = hub.Grant(Rooms.A, token)!.Limits;
        Assert.Equal(32, limits.MaxParallelCalls);          // the ceiling of CONTRACT.md §3.4
        Assert.Equal(1, limits.MaxAnswerTokens);
        Assert.Equal(0, limits.MaxTextChars);
        Assert.Equal(600, limits.MaxCallSeconds);
        await wallet.Ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task TheWalletsOwnKindIsBelievedAndTheIdIsOnlyTheFallbackOfAnOlderWallet()
    {
        var withKind = """
            [{"ref":"m1","name":"Model A","id":null,"runs":"api-key","kind":"agent"},
             {"ref":"m2","name":"Looks like one","id":"agent/pretend","runs":"local","kind":"model"},
             {"ref":"m3","name":"Odd","id":null,"runs":"local","kind":"wizard"},
             {"ref":"m4","name":"Old wallet","id":"agent/old","runs":"local"}]
            """;
        var (hub, wallet, token, run, tcp) = await Connect(models: withKind);
        using var _t = tcp;
        var models = hub.Grant(Rooms.A, token)!.ModelsOf("text.generate");
        Assert.Equal(["agent", "model", "model", "agent"], models.Select(m => m.Kind));   // hidden names: still an agent; the wallet's word beats the id; a kind nobody knows is a model
        await wallet.Ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task OnlyAsManyParticipantsAreReadAsTheSiteAsksFor()
    {
        var many = "[" + string.Join(',', Enumerable.Range(1, 40).Select(i => $$"""{"ref":"m{{i}}","name":"Model {{i}}","id":"x","runs":"local"}""")) + "]";
        var hub = Rooms.Hub(change: o => o.MaxModels = 3);
        var (server, wallet, tcp) = await WsPeer.Pair();
        using var _t = tcp;
        var run = WalletChannel.RunAsync(server, hub, NullLogger.Instance, CancellationToken.None);
        await wallet.Send(Hello.Json(hub.NewCode(Rooms.A, "203.0.113.5"), null, models: many));
        var token = (await wallet.NextOf("welcome")).GetProperty("pairing").GetString()!;
        var grant = hub.Grant(Rooms.A, token)!;
        Assert.Equal(["m1", "m2", "m3"], grant.ModelsOf("text.generate").Select(m => m.Ref));
        await wallet.Ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task NamesAndIdsOfParticipantsAreCleanedAndCutLikeAnyTextFromAWallet()
    {
        var messy = """
            [{"ref":"m1","name":"A‮name\u0000with\todd bits","id":"agent/../etc/passwd","runs":"local","extra":"ignored"},
             {"ref":"has space","name":"not a ref","id":"x","runs":"local"},
             {"ref":"","name":"empty","id":"x","runs":"local"},
             {"ref":"m1","name":"the same ref twice","id":"x","runs":"local"},
             "not an object",
             {"ref":"m9","name":12345,"id":null,"runs":null}]
            """;
        var (hub, wallet, token, run, tcp) = await Connect(models: messy);
        using var _t = tcp;
        var models = hub.Grant(Rooms.A, token)!.ModelsOf("text.generate");
        Assert.Equal(["m1", "m9"], models.Select(m => m.Ref));                       // bad refs and repeats are dropped
        Assert.DoesNotContain('‮', models[0].Name);
        Assert.DoesNotContain('\u0000', models[0].Name);
        Assert.DoesNotContain('\t', models[0].Name);
        Assert.Equal("agent", models[0].Kind);                                       // the id starts with «agent/», so the page shows AGENT
        Assert.Equal("agent/../etc/passwd", models[0].Id);                           // the id itself is shown as text and is never a path the page uses
        Assert.Equal("model", models[1].Kind);                                       // no id at all: nothing says it can search the web
        Assert.Equal("m9", models[1].Name);                                          // a name that is not a string falls back to the ref
        Assert.Equal("", models[1].Runs);
        await wallet.Ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void TheWalletsAnswerKeepsItsLinesAndLosesEverythingThatIsNotText()
    {
        Assert.Equal("one\ntwo\n\nthree", WalletText.CleanBlock("  one\r\ntwo\n\n\n\n\r\nthree ‮​\0", 1000));
        Assert.Equal("a\tb", WalletText.CleanBlock("a\tb", 10));
        Assert.Equal("abc", WalletText.CleanBlock("abcdef", 3));
        Assert.Equal("ab", WalletText.CleanBlock("ab😀", 3));                        // never half of a two-part character
        Assert.Equal("", WalletText.CleanBlock("\n \0 \n", 100));
        Assert.Equal("", WalletText.CleanBlock(null, 100));
        Assert.Equal("<b>x</b>", WalletText.CleanBlock("<b>x</b>", 100));            // markup stays text: the page never renders it as HTML
    }

    [Fact]
    public async Task AWalletThatStopsAnsweringIsLetGoAndTheAnswerIsGone()
    {
        var (hub, wallet, token, run, tcp) = await Connect();
        using var _t = tcp;
        var channel = hub.Channel(Rooms.A, token)!;
        var call = channel.CallAsync(WalletJobs.Skill, new { messages = new[] { new { role = "user", content = "Q" } } }, new { model = "m4" },
                                     8000, TimeSpan.FromSeconds(20), CancellationToken.None);
        await wallet.NextOf("call");
        await wallet.Ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        Assert.Equal(WalletOutcome.Cancelled, (await call).Outcome);                 // the page is told «the wallet went away»
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(PairingState.Offline, hub.Status(Rooms.A, token));
    }

    [Fact]
    public void TheEndpointIsBuiltFromTheRequestThePageCameWithAndTheSitesOwnPath()
    {
        var hub = Rooms.Hub();
        Assert.Equal("wss://chat-to-agents.appshub.net/byoa/ws", Rooms.Endpoint(hub, "chat-to-agents.appshub.net", https: true));
        Assert.Equal("ws://127.0.0.1:5291/byoa/ws", Rooms.Endpoint(hub, "127.0.0.1:5291"));

        var own = Rooms.Hub(change: o => o.WalletPath = "wallet");                  // a site may put the channel anywhere
        Assert.Equal("wss://example.org/wallet", Rooms.Endpoint(own, "example.org", https: true));
    }

    [Fact]
    public async Task AWalletThatSaysNothingAtAllIsLetGoBeforeItIsAWallet()
    {
        var hub = Rooms.Hub(change: o => { o.HelloSeconds = 1; o.IdleSeconds = 1; });
        var (server, wallet, tcp) = await WsPeer.Pair();
        using var _t = tcp;
        var run = WalletChannel.RunAsync(server, hub, NullLogger.Instance, CancellationToken.None);
        var next = await wallet.Next(5);                                            // a connection that never says hello is closed
        Assert.True(next is null || next.Value.Type == WebSocketMessageType.Close);
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, hub.Pairings);
    }

    [Fact]
    public async Task EverySkillsModelsArriveWithTheirVerdictsAndTheCapabilities()
    {
        var models = """
            {"text.generate":[
               {"ref":"m1","name":"Claude","id":"anthropic/claude","runs":"api-key","kind":"model",
                "can":{"input":true,"output":true,"context":false,"runs":"yes","surprise":true},
                "abilities":{"web.search":true}},
               {"ref":"m2","name":"Agent","id":"agent/claude","runs":"subscription","kind":"agent",
                "can":{"input":true,"output":false,"context":true,"runs":true},
                "abilities":{"web.search":true,"web.read":false,"extra":1,"x-mine":true,"image.read":true}}],
             "image.generate":[{"ref":"i1","name":"Drawer","id":"google/gemini","runs":"api-key","kind":"model","can":{"output":true}}],
             "Not-A-Skill":[]}
            """;
        var (hub, wallet, token, run, tcp) = await Connect(models: null, modelsBySkill: models,
            capabilities: """["needs","can","unknown","needs","image.generate","cancel"]""");
        using var _t = tcp;
        var grant = hub.Grant(Rooms.A, token)!;

        Assert.Equal(["image.generate", "text.generate"], grant.Models.Keys.Order(StringComparer.Ordinal));
        var text = grant.ModelsOf("text.generate");
        Assert.Equal(["m1", "m2"], text.Select(m => m.Ref));
        var can = text[0].Can!;
        Assert.Equal(["context", "input", "output"], can.Keys.Order(StringComparer.Ordinal));   // booleans only, the four properties only
        Assert.False(can["context"]);
        Assert.Null(text[0].Abilities);                                                                 // abilities are an agent's
        var abilities = text[1].Abilities!;
        Assert.Equal(["image.read", "web.read", "web.search", "x-mine"], abilities.Keys.Order(StringComparer.Ordinal));   // at most four, values are booleans
        Assert.False(abilities["web.read"]);
        Assert.True(abilities["x-mine"]);
        Assert.Equal(["output"], grant.ModelsOf("image.generate")[0].Can!.Keys);
        Assert.Empty(grant.ModelsOf("not-a.skill"));
        Assert.Equal(["needs", "can", "image.generate", "cancel"], grant.Capabilities);                  // the closed list, repeats dropped

        await wallet.Ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AGrantFrameChangesTheModelsButKeepsTheCapabilities()
    {
        var (hub, wallet, token, run, tcp) = await Connect(capabilities: """["needs","can","cancel"]""");
        using var _t = tcp;
        await wallet.Send(WalletFrames.Grant(Hello.SaneLimits,
            """[{"ref":"m7","name":"A new model","id":"deepseek/deepseek-v3.2","runs":"api-key"}]"""));
        var until = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < until && hub.Grant(Rooms.A, token)!.ModelsOf("text.generate").Length != 1) await Task.Delay(20);
        var grant = hub.Grant(Rooms.A, token)!;
        Assert.Equal(["m7"], grant.ModelsOf("text.generate").Select(m => m.Ref));
        Assert.Equal(["needs", "can", "cancel"], grant.Capabilities);
        await wallet.Ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task TheSiteCanStopACallAndTheWalletIsToldToCancelIt()
    {
        var (hub, wallet, token, run, tcp) = await Connect();
        using var _t = tcp;
        var channel = hub.Channel(Rooms.A, token)!;
        var call = channel.CallAsync(WalletJobs.Skill, new { messages = new[] { new { role = "user", content = "Q" } } },
                                     new { model = "m1" }, 1000, TimeSpan.FromSeconds(10), CancellationToken.None);
        var asked = await wallet.NextOf("call");
        var id = asked.GetProperty("id").GetInt32();

        Assert.True(channel.Cancel(id));
        var cancel = await wallet.NextOf("cancel");
        Assert.Equal(id, cancel.GetProperty("id").GetInt32());
        Assert.Equal(WalletOutcome.Cancelled, (await call).Outcome);
        Assert.False(channel.Cancel(id));                                           // already let go
        await wallet.Ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }
}