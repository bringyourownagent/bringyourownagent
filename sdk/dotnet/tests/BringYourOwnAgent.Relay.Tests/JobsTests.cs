using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;

namespace BringYourOwnAgent.Relay.Tests;

/// <summary>The page's calls: one participant answers one question, the answer is fetched by asking again, and the pieces of a
/// streaming answer are there while the model writes.</summary>
public class JobsTests
{
    /// <summary>A hub, the jobs route and a real wallet connection (a loopback WebSocket the test answers on), welcomed for one room.</summary>
    static async Task<(DirectHub Hub, WalletJobs Jobs, WsPeer Wallet, string Token, Task Run, IDisposable Tcp)> Pair(
        string? limits = Hello.SaneLimits, string? models = Hello.FourModels, string[]? skills = null, string room = Rooms.A,
        ManualClock? clock = null, Action<RelayOptions>? change = null)
    {
        var hub = Rooms.Hub(clock, change);
        var jobs = Rooms.Jobs(hub, clock);
        var (server, wallet, tcp) = await WsPeer.Pair();
        var run = WalletChannel.RunAsync(server, hub, NullLogger.Instance, CancellationToken.None);
        await wallet.Send(Hello.Json(hub.NewCode(room, "203.0.113.5"), null, skills, limits, models));
        var token = (await wallet.NextOf("welcome")).GetProperty("pairing").GetString()!;
        return (hub, jobs, wallet, token, run, tcp);
    }

    static JobRequest Ask(string token, string? model = "m2", int count = 1, string text = "Who are you?", int? maxTokens = null, double? temperature = null) =>
        new(token, model, Enumerable.Range(0, count).Select(i => new JobMessage(i == count - 1 ? "user" : "assistant", i == count - 1 ? text : $"earlier {i}")).ToList(), maxTokens, temperature);

    [Fact]
    public async Task AQuestionGoesToTheNamedParticipantWithAStreamAndTheAnswerComesBackWithItsLines()
    {
        var (_, jobs, wallet, token, run, tcp) = await Pair();
        using var _t = tcp;
        var started = jobs.Start(Rooms.A, new JobRequest(token, "m2", [new JobMessage("system", "You are GLM Flash."), new JobMessage("user", "Conversation so far:\n\n[Visitor]: hullo")], 1500, 0.7));
        Assert.NotNull(started.Id);

        var asked = await wallet.NextOf("call");
        Assert.Equal("text.generate", asked.GetProperty("skill").GetString());
        var p = asked.GetProperty("params");
        Assert.Equal("m2", p.GetProperty("model").GetString());                       // the grant's ref, and nothing else
        Assert.True(p.GetProperty("stream").GetBoolean());                            // the link carries a stream, so it is asked for
        Assert.Equal(1500, p.GetProperty("maxTokens").GetInt32());
        Assert.Equal(0.7, p.GetProperty("temperature").GetDouble(), 3);
        Assert.Equal(4, p.EnumerateObject().Count());                                 // model, maxTokens, temperature, stream: nothing more
        var messages = asked.GetProperty("input").GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("system", messages[0].GetProperty("role").GetString());

        var pending = await jobs.PollAsync(Rooms.A, started.Id, TimeSpan.FromMilliseconds(100), CancellationToken.None);
        Assert.Equal("pending", pending.State);

        var longText = "Sure:\n- one\n- two\n\n" + new string('x', 3000) + "<b>markup stays text</b>";
        await wallet.Send(WalletFrames.Result(asked.GetProperty("id").GetInt32(), true, longText));
        var done = await jobs.PollAsync(Rooms.A, started.Id, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal("done", done.State);
        Assert.True(done.Ok);
        Assert.Equal(longText, done.Text);
        Assert.Equal("unknown", (await jobs.PollAsync(Rooms.A, started.Id, TimeSpan.Zero, CancellationToken.None)).State);   // handed out once
        Assert.Equal(0, jobs.Count);

        await wallet.Ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ThePiecesAreThereWhileTheModelWritesAndTheResultReplacesThem()
    {
        var (_, jobs, wallet, token, run, tcp) = await Pair();
        using var _t = tcp;
        var started = jobs.Start(Rooms.A, Ask(token));
        var id = (await wallet.NextOf("call")).GetProperty("id").GetInt32();
        await wallet.Send(WalletFrames.Chunk(id, "Half "));
        await wallet.Send(WalletFrames.Chunk(id, "a thought"));
        var until = DateTime.UtcNow.AddSeconds(5);
        JobResult? pending = null;
        while (DateTime.UtcNow < until)
        {
            pending = await jobs.PollAsync(Rooms.A, started.Id, TimeSpan.Zero, CancellationToken.None);
            if (pending.Partial == "Half a thought") break;
            await Task.Delay(20);
        }
        Assert.Equal("pending", pending!.State);
        Assert.Equal("Half a thought", pending.Partial);                              // the page shows this with a «writing…» mark

        await wallet.Send(WalletFrames.Result(id, true, "Half a thought, and then the whole answer."));
        var done = await jobs.PollAsync(Rooms.A, started.Id, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal("Half a thought, and then the whole answer.", done.Text);        // the result replaces the pieces
        Assert.Null(done.Partial);
        await wallet.Ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task OnlyTheShapesThePageUsesAreAccepted()
    {
        var (hub, jobs, wallet, token, run, tcp) = await Pair();
        using var _t = tcp;
        var own = hub.Options.MaxCallChars;
        JobRequest With(List<JobMessage>? m, string? model = "m1") => new(token, model, m, null, null);
        JobMessage U(string c) => new("user", c);

        Assert.Equal("bad-request", jobs.Start(Rooms.A, With(null)).Error);
        Assert.Equal("bad-request", jobs.Start(Rooms.A, With([])).Error);
        Assert.Equal("bad-request", jobs.Start(Rooms.A, With([new JobMessage("tool", "x"), U("hi")])).Error);       // the contract's roles only
        Assert.Equal("bad-request", jobs.Start(Rooms.A, With([new JobMessage(null, "x"), U("hi")])).Error);
        Assert.Equal("bad-request", jobs.Start(Rooms.A, With([new JobMessage("system", " "), U("hi")])).Error);
        Assert.Equal("bad-request", jobs.Start(Rooms.A, With([new JobMessage("user", "hi"), new JobMessage("assistant", "x")])).Error);   // the last one is the question
        Assert.Equal("bad-request", jobs.Start(Rooms.A, With(Enumerable.Repeat(U("x"), hub.Options.MaxCallMessages + 1).ToList())).Error);
        Assert.Equal("bad-request", jobs.Start(Rooms.A, With([U(new string('x', own + 1))])).Error);
        Assert.Equal("bad-request", jobs.Start("not a room id!", With([U("hi")])).Error);
        Assert.Null(await wallet.Next(0.2));                                          // nothing of the refused ones ever reached the wallet
        Assert.NotNull(jobs.Start(Rooms.A, With([U(new string('x', own))])).Id);      // exactly the cap is fine
        await wallet.NextOf("call");                                                  // and that one did
        await wallet.Ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task TheSitesOwnCapsAreItsOwn()
    {
        var (hub, jobs, wallet, token, run, tcp) = await Pair(change: o => { o.MaxCallChars = 50; o.MaxCallMessages = 2; });
        using var _t = tcp;
        JobMessage U(string c) => new("user", c);
        Assert.Equal("bad-request", jobs.Start(Rooms.A, new JobRequest(token, "m1", [U(new string('x', 51))], null, null)).Error);
        Assert.Equal("bad-request", jobs.Start(Rooms.A, new JobRequest(token, "m1", [U("a"), new JobMessage("assistant", "b"), U("c")], null, null)).Error);
        Assert.NotNull(jobs.Start(Rooms.A, new JobRequest(token, "m1", [U(new string('x', 50))], null, null)).Id);
        await wallet.NextOf("call");
        Assert.Equal(50, hub.Options.MaxCallChars);
        await wallet.Ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ACallMayOnlyNameAParticipantOfTheCurrentGrant()
    {
        var (hub, jobs, wallet, token, run, tcp) = await Pair();
        using var _t = tcp;
        Assert.Equal("bad-model", jobs.Start(Rooms.A, Ask(token, "m9")).Error);       // a ref the site was never given
        Assert.Equal("bad-model", jobs.Start(Rooms.A, Ask(token, null)).Error);
        Assert.Equal("bad-model", jobs.Start(Rooms.A, Ask(token, " m1")).Error);
        Assert.Equal("bad-model", jobs.Start(Rooms.A, Ask(token, "m1; drop table")).Error);
        Assert.NotNull(jobs.Start(Rooms.A, Ask(token, "m4")).Id);                     // the agent is a participant like any other
        await wallet.NextOf("call");

        await wallet.Send(WalletFrames.Grant(Hello.SaneLimits, """[{"ref":"m1","name":"Local Qwen 4B","id":"qwen/qwen3.5-4b","runs":"local"}]"""));
        var until = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < until && hub.Grant(Rooms.A, token)!.ModelsOf("text.generate").Length != 1) await Task.Delay(20);
        Assert.Equal("bad-model", jobs.Start(Rooms.A, Ask(token, "m2")).Error);       // the visitor took that participant away
        Assert.NotNull(jobs.Start(Rooms.A, Ask(token, "m1")).Id);
        await wallet.Ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task MaxTokensAndTemperatureAreHeldToSaneValues()
    {
        var (_, jobs, wallet, token, run, tcp) = await Pair(models: Hello.FourModels);
        using var _t = tcp;
        // the grant allows 4000 tokens; the site's own cap is 8000, so the grant wins
        var huge = jobs.Start(Rooms.A, Ask(token, "m1", maxTokens: 1_000_000, temperature: 5));
        var asked = await wallet.NextOf("call");
        Assert.Equal(4000, asked.GetProperty("params").GetProperty("maxTokens").GetInt32());
        Assert.Equal(1, asked.GetProperty("params").GetProperty("temperature").GetDouble());

        var cold = jobs.Start(Rooms.A, Ask(token, "m2", maxTokens: 2000, temperature: -3));
        var asked2 = await wallet.NextOf("call");
        Assert.Equal(2000, asked2.GetProperty("params").GetProperty("maxTokens").GetInt32());       // a sane ask is passed on as it is
        Assert.Equal(0, asked2.GetProperty("params").GetProperty("temperature").GetDouble());

        var none = jobs.Start(Rooms.A, new JobRequest(token, "m3", [new JobMessage("user", "hi")], null, null));
        var asked3 = await wallet.NextOf("call");
        Assert.Equal(3000, asked3.GetProperty("params").GetProperty("maxTokens").GetInt32());       // the site's own default
        Assert.Equal(0.7, asked3.GetProperty("params").GetProperty("temperature").GetDouble(), 3);
        Assert.NotNull(huge.Id);
        Assert.NotNull(cold.Id);
        Assert.NotNull(none.Id);
        await wallet.Ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task TheSiteNeverAsksForMoreTokensThanTheGrantAllows()
    {
        var (hub, jobs, wallet, token, run, tcp) = await Pair(limits: """{"maxTextChars":60000,"maxAnswerTokens":20000,"maxCallSeconds":300,"maxParallelCalls":4,"maxCallsPerMinute":30}""");
        using var _t = tcp;
        jobs.Start(Rooms.A, Ask(token, "m1", maxTokens: 20_000));
        var asked = await wallet.NextOf("call");
        Assert.Equal(hub.Options.MaxCallTokens, asked.GetProperty("params").GetProperty("maxTokens").GetInt32());     // never above the site's own cap
        await wallet.Ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task TooManyCallsAtOnceAreTurnedAwayPolitely()
    {
        var (_, jobs, wallet, token, run, tcp) = await Pair(limits: """{"maxTextChars":60000,"maxAnswerTokens":4000,"maxCallSeconds":300,"maxParallelCalls":2,"maxCallsPerMinute":30}""");
        using var _t = tcp;
        var first = jobs.Start(Rooms.A, Ask(token, "m1"));
        var second = jobs.Start(Rooms.A, Ask(token, "m2"));
        Assert.NotNull(first.Id);
        Assert.NotNull(second.Id);
        Assert.Equal("busy", jobs.Start(Rooms.A, Ask(token, "m3")).Error);             // the wallet may not be given more at once
        var ids = new List<int>();
        for (var i = 0; i < 2; i++) ids.Add((await wallet.NextOf("call")).GetProperty("id").GetInt32());
        await wallet.Send(WalletFrames.Result(ids[0], true, "one"));
        Assert.True((await jobs.PollAsync(Rooms.A, first.Id, TimeSpan.FromSeconds(5), CancellationToken.None)).Ok);
        Assert.NotNull(jobs.Start(Rooms.A, Ask(token, "m3")).Id);                      // one finished: there is room again
        await wallet.Ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task TooManyQuestionsInAMinuteAreTurnedAway()
    {
        var (_, jobs, wallet, token, run, tcp) = await Pair(change: o => o.JobsPerMinute = 2);
        using var _t = tcp;
        Assert.NotNull(jobs.Start(Rooms.A, Ask(token, "m1")).Id);
        Assert.NotNull(jobs.Start(Rooms.A, Ask(token, "m1")).Id);
        Assert.Equal("busy", jobs.Start(Rooms.A, Ask(token, "m1")).Error);
        Assert.NotNull(jobs.Start(Rooms.B, Ask(token, "m1")).Error);                    // another room's page cannot use this wallet at all
        await wallet.NextOf("call");
        await wallet.NextOf("call");
        await wallet.Ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task WhyAJobEndedIsToldInPlainWords()
    {
        var (_, jobs, wallet, token, run, tcp) = await Pair(change: o => o.JobSeconds = 1);
        using var _t = tcp;

        var refused = jobs.Start(Rooms.A, Ask(token, "m1"));
        await wallet.Send(WalletFrames.Result((await wallet.NextOf("call")).GetProperty("id").GetInt32(), false));
        Assert.Equal(new JobResult("done", false, null, null, "failed"),
                     await jobs.PollAsync(Rooms.A, refused.Id, TimeSpan.FromSeconds(5), CancellationToken.None));

        // the wallet's own reason reaches the page; a word that is not on the closed list is just "failed"
        foreach (var (said, told) in new[] { ("timeout", "timeout"), ("cancelled", "cancelled"), ("limit", "limit"), ("failed", "failed"), ("no model for you", "failed") })
        {
            var job = jobs.Start(Rooms.A, Ask(token, "m1"));
            await wallet.Send(WalletFrames.Result((await wallet.NextOf("call")).GetProperty("id").GetInt32(), false, reason: said));
            Assert.Equal(told, (await jobs.PollAsync(Rooms.A, job.Id, TimeSpan.FromSeconds(5), CancellationToken.None)).Reason);
        }

        var wordless = jobs.Start(Rooms.A, Ask(token, "m2"));
        await wallet.Send(WalletFrames.Result((await wallet.NextOf("call")).GetProperty("id").GetInt32(), true, "  ​ \n "));
        Assert.Equal("failed", (await jobs.PollAsync(Rooms.A, wordless.Id, TimeSpan.FromSeconds(5), CancellationToken.None)).Reason);      // nothing readable is no answer

        var slow = jobs.Start(Rooms.A, Ask(token, "m3"));                              // JobSeconds = 1
        await wallet.NextOf("call");
        Assert.Equal("timeout", (await jobs.PollAsync(Rooms.A, slow.Id, TimeSpan.FromSeconds(5), CancellationToken.None)).Reason);

        var gone = jobs.Start(Rooms.A, Ask(token, "m4"));
        await wallet.NextOf("call");
        await wallet.Ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        Assert.Equal("gone", (await jobs.PollAsync(Rooms.A, gone.Id, TimeSpan.FromSeconds(5), CancellationToken.None)).Reason);
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ThePageCanStopWaitingAndALateAnswerIsDropped()
    {
        var (_, jobs, wallet, token, run, tcp) = await Pair();
        using var _t = tcp;
        var started = jobs.Start(Rooms.A, Ask(token, "m4"));
        var id = (await wallet.NextOf("call")).GetProperty("id").GetInt32();
        Assert.True(jobs.Cancel(Rooms.A, started.Id));                                 // «Stop» in the page
        Assert.False(jobs.Cancel(Rooms.A, started.Id));
        Assert.Equal("unknown", (await jobs.PollAsync(Rooms.A, started.Id, TimeSpan.Zero, CancellationToken.None)).State);
        Assert.Equal(0, jobs.Count);

        var finish = await wallet.NextOf("cancel");                                  // the wallet is told to stop the call (wallet 0.36)
        Assert.Equal(id, finish.GetProperty("id").GetInt32());
        await wallet.Send(WalletFrames.Result(id, true, "too late"));                  // the wallet finishes anyway: the answer is dropped
        Assert.Null(await wallet.Next(0.3));                                           // and the site says nothing more to it
        Assert.Equal("unknown", (await jobs.PollAsync(Rooms.A, started.Id, TimeSpan.Zero, CancellationToken.None)).State);

        Assert.NotNull(jobs.Start(Rooms.A, Ask(token, "m1")).Id);                      // the participant is free again
        await wallet.NextOf("call");
        await wallet.Ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AnotherRoomCanNeitherStartNorFetchAJob()
    {
        var (_, jobs, wallet, token, run, tcp) = await Pair();
        using var _t = tcp;
        var started = jobs.Start(Rooms.A, Ask(token));
        await wallet.NextOf("call");
        Assert.Equal("unknown", (await jobs.PollAsync(Rooms.B, started.Id, TimeSpan.Zero, CancellationToken.None)).State);
        Assert.Equal("unknown", (await jobs.PollAsync("not a room id!", started.Id, TimeSpan.Zero, CancellationToken.None)).State);
        Assert.Equal("unknown", (await jobs.PollAsync(Rooms.A, "made-up-job", TimeSpan.Zero, CancellationToken.None)).State);
        Assert.False(jobs.Cancel(Rooms.B, started.Id));
        Assert.Equal("offline", jobs.Start(Rooms.B, Ask(token)).Error);                // room B's page cannot use room A's wallet
        Assert.Equal("pending", (await jobs.PollAsync(Rooms.A, started.Id, TimeSpan.FromMilliseconds(50), CancellationToken.None)).State);
        await wallet.Ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AJobNobodyFetchedIsForgottenAfterTenMinutes()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero));
        var (_, jobs, wallet, token, run, tcp) = await Pair(clock: clock);
        using var _t = tcp;
        var started = jobs.Start(Rooms.A, Ask(token));
        await wallet.NextOf("call");
        Assert.Equal(1, jobs.Count);
        clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal("unknown", (await jobs.PollAsync(Rooms.A, started.Id, TimeSpan.Zero, CancellationToken.None)).State);
        Assert.Equal(0, jobs.Count);
        await wallet.Ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AQuestionForAWalletThatIsNotThereIsToldApartFromOneThatWasNotAllowed()
    {
        var (_, jobs, wallet, token, run, tcp) = await Pair(skills: ["audio.transcribe"], models: Hello.FourModels);
        using var _t = tcp;
        Assert.Equal("no-text", jobs.Start(Rooms.A, Ask(token)).Error);                // the visitor did not allow text for this site
        Assert.Null(await wallet.Next(0.2));                                           // nothing reached the wallet
        Assert.Equal("offline", jobs.Start(Rooms.A, Ask("made-up-pairing")).Error);
        Assert.Equal("offline", jobs.Start(Rooms.A, Ask(null!)).Error);
        await wallet.Ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task StoppingAJobTellsTheWalletToCancelTheCall()
    {
        var (_, jobs, wallet, token, run, tcp) = await Pair();
        using var _t = tcp;
        var started = jobs.Start(Rooms.A, Ask(token));
        var asked = await wallet.NextOf("call");
        Assert.True(jobs.Cancel(Rooms.A, started.Id));
        var cancel = await wallet.NextOf("cancel");
        Assert.Equal(asked.GetProperty("id").GetInt32(), cancel.GetProperty("id").GetInt32());
        Assert.Equal("unknown", (await jobs.PollAsync(Rooms.A, started.Id, TimeSpan.Zero, CancellationToken.None)).State);
        await wallet.Ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }
}