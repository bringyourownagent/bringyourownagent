using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;

namespace BringYourOwnAgent.Relay.Tests;

/// <summary>The page's picture calls (CONTRACT.md §3.7, wallet 0.37): the job reaches the picture participant, the wallet's parts are joined and
/// checked, and anything that does not add up to the pictures the result describes is a failed call.</summary>
public class PictureJobsTests
{
    const string Models = """
        {"text.generate":[{"ref":"m1","name":"GLM Flash","id":"z-ai/glm-5.3-flash","runs":"api-key"},
                          {"ref":"m2","name":"Nano Banana","id":"google/gemini-3.1-flash-image","runs":"api-key"}],
         "image.generate":[{"ref":"m3","name":"Nano Banana","id":"google/gemini-3.1-flash-image","runs":"api-key"}]}
        """;

    static async Task<(WalletJobs Jobs, WsPeer Wallet, string Token, Task Run, IDisposable Tcp)> Pair(string[]? skills = null)
    {
        var hub = Rooms.Hub();
        var jobs = Rooms.Jobs(hub);
        var (server, wallet, tcp) = await WsPeer.Pair();
        var run = WalletChannel.RunAsync(server, hub, NullLogger.Instance, CancellationToken.None);
        await wallet.Send(Hello.Json(hub.NewCode(Rooms.A, "203.0.113.5"), null, skills ?? ["text.generate", "image.generate"], Hello.SaneLimits,
                                     capabilities: """["needs","can","kind","cancel","image.generate","parts"]""", modelsBySkill: Models));
        var token = (await wallet.NextOf("welcome")).GetProperty("pairing").GetString()!;
        return (jobs, wallet, token, run, tcp);
    }

    static JobRequest Draw(string token, string? model = "m3", string? prompt = "A fox, flat, on magenta", int? count = null, string? aspect = null,
                           List<JobPicture>? references = null) =>
        new(token, model, null, null, null, "image.generate", prompt, references, count, aspect);

    /// <summary>Bytes that begin like a PNG file and go on with noise (the relay looks at the first bytes only).</summary>
    static byte[] Png(int size, int seed = 1)
    {
        var bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
        return bytes;
    }

    static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>The <c>image</c> frames of one picture: its base64 cut in parts of at most <see cref="PictureParts.MaxPartChars"/> characters.</summary>
    static List<string> Parts(int id, int index, byte[] bytes, string mime = "image/png", int partChars = PictureParts.MaxPartChars)
    {
        var data = Convert.ToBase64String(bytes);
        var pieces = Enumerable.Range(0, (data.Length + partChars - 1) / partChars).Select(i => data.Substring(i * partChars, Math.Min(partChars, data.Length - i * partChars))).ToList();
        return pieces.Select((piece, seq) => JsonSerializer.Serialize(new { type = "image", id, index, seq, of = pieces.Count, mime, data = piece })).ToList();
    }

    static string Result(int id, params (string Mime, int Size, string Sha)[] pictures) =>
        JsonSerializer.Serialize(new { type = "result", id, ok = true, output = new { images = pictures.Select(p => new { mime = p.Mime, size = p.Size, sha256 = p.Sha }) },
                                       receipt = new { cost = "0.068 USD", provider = "api-key", model = "m3" } });

    static string ReducedResult(int id, string mime, int size, string sha) =>
        JsonSerializer.Serialize(new { type = "result", id, ok = true, output = new { images = new[] { new { mime, size, sha256 = sha, reduced = true } } } });

    static async Task<JobResult> Done(WalletJobs jobs, string id) => await jobs.PollAsync(Rooms.A, id, TimeSpan.FromSeconds(5), CancellationToken.None);

    [Fact]
    public async Task APictureJobGoesToThePictureParticipantAndItsPartsComeBackAsOnePicture()
    {
        var (jobs, wallet, token, run, tcp) = await Pair();
        using var _t = tcp;
        var reference = Png(2000, seed: 7);
        var started = jobs.Start(Rooms.A, Draw(token, count: 1, aspect: "1:1", references: [new JobPicture("image/png", Convert.ToBase64String(reference))]));
        Assert.Null(started.Error);

        var asked = await wallet.NextOf("call");
        Assert.Equal("image.generate", asked.GetProperty("skill").GetString());
        var p = asked.GetProperty("params");
        Assert.Equal("m3", p.GetProperty("model").GetString());
        Assert.Single(p.EnumerateObject());                                                   // the ref, and nothing else
        var input = asked.GetProperty("input");
        Assert.Equal("A fox, flat, on magenta", input.GetProperty("prompt").GetString());
        Assert.Equal(1, input.GetProperty("count").GetInt32());
        Assert.Equal("1:1", input.GetProperty("aspect").GetString());
        Assert.Equal(Convert.ToBase64String(reference), input.GetProperty("references")[0].GetProperty("data").GetString());

        var id = asked.GetProperty("id").GetInt32();
        var picture = Png(1_100_000);                                                          // three parts
        var parts = Parts(id, 0, picture);
        Assert.Equal(3, parts.Count);
        foreach (var part in parts) await wallet.Send(part);
        Assert.Equal("pending", (await jobs.PollAsync(Rooms.A, started.Id, TimeSpan.FromMilliseconds(100), CancellationToken.None)).State);
        await wallet.Send(Result(id, ("image/png", picture.Length, Sha(picture))));

        var done = await Done(jobs, started.Id!);
        Assert.Equal("done", done.State);
        Assert.True(done.Ok);
        Assert.Null(done.Text);
        var image = Assert.Single(done.Images!);
        Assert.Equal("image/png", image.Mime);
        Assert.Equal(picture.Length, image.Size);
        Assert.Equal(Sha(picture), image.Sha256);
        Assert.Equal(picture, Convert.FromBase64String(image.Data));

        await wallet.Ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task PartsAreJoinedInTheirOrderWhateverOrderTheyCameInAndSeveralPicturesStayApart()
    {
        var (jobs, wallet, token, _, tcp) = await Pair();
        using var _t = tcp;
        var started = jobs.Start(Rooms.A, Draw(token, count: 2));
        var id = (await wallet.NextOf("call")).GetProperty("id").GetInt32();
        var first = Png(30_000, seed: 1);
        var second = Png(25_000, seed: 2);
        var parts = Parts(id, 0, first, partChars: 10_000).Concat(Parts(id, 1, second, partChars: 10_000)).Reverse().ToList();
        foreach (var part in parts) await wallet.Send(part);
        await wallet.Send(Result(id, ("image/png", first.Length, Sha(first)), ("image/png", second.Length, Sha(second))));

        var done = await Done(jobs, started.Id!);
        Assert.True(done.Ok, done.Reason);
        Assert.Equal(first, Convert.FromBase64String(done.Images![0].Data));
        Assert.Equal(second, Convert.FromBase64String(done.Images[1].Data));
    }

    /// <summary>Every way an answer can fail to add up: the call is failed, never an answer with a picture that is not what the wallet said.</summary>
    [Theory]
    [InlineData("missing part")]
    [InlineData("wrong sha256")]
    [InlineData("wrong size")]
    [InlineData("not a picture")]
    [InlineData("a picture more than described")]
    [InlineData("a part twice")]
    [InlineData("parts that disagree")]
    [InlineData("no pictures at all")]
    [InlineData("type unlike the bytes")]
    public async Task AnAnswerThatDoesNotAddUpIsAFailedCall(string fault)
    {
        var (jobs, wallet, token, _, tcp) = await Pair();
        using var _t = tcp;
        var started = jobs.Start(Rooms.A, Draw(token));
        var id = (await wallet.NextOf("call")).GetProperty("id").GetInt32();
        var picture = fault == "not a picture" ? "hello, this is text and no picture at all"u8.ToArray() : Png(30_000);
        var parts = Parts(id, 0, picture, partChars: 10_000);
        var described = ("image/png", picture.Length, Sha(picture));
        switch (fault)
        {
            case "missing part": parts.RemoveAt(1); break;
            case "wrong sha256": described = ("image/png", picture.Length, Sha(Png(10))); break;
            case "wrong size": described = ("image/png", picture.Length - 1, Sha(picture)); break;
            case "a picture more than described": parts.AddRange(Parts(id, 1, Png(500, seed: 3))); break;
            case "a part twice": parts.Add(parts[0]); break;
            case "parts that disagree": parts[1] = parts[1].Replace("\"of\":4", "\"of\":5"); break;
            case "type unlike the bytes": parts = Parts(id, 0, picture, mime: "image/jpeg", partChars: 10_000); described = ("image/jpeg", picture.Length, Sha(picture)); break;
        }
        foreach (var part in parts) await wallet.Send(part);
        await wallet.Send(fault == "no pictures at all" ? Result(id) : Result(id, described));

        var done = await Done(jobs, started.Id!);
        Assert.Equal("done", done.State);
        Assert.False(done.Ok);
        Assert.Equal("failed", done.Reason);
        Assert.Null(done.Images);
    }

    [Fact]
    public async Task ThePagesSizeWishesReachTheWalletWithinTheirRangeAndAReducedPictureSaysSo()
    {
        var (jobs, wallet, token, _, tcp) = await Pair();
        using var _t = tcp;
        var started = jobs.Start(Rooms.A, Draw(token) with { MaxSide = 10_000, MaxBytes = 50 });
        var asked = await wallet.NextOf("call");
        var input = asked.GetProperty("input");
        Assert.Equal(4096, input.GetProperty("maxSide").GetInt32());
        Assert.Equal(100_000, input.GetProperty("maxBytes").GetInt32());
        var id = asked.GetProperty("id").GetInt32();
        var picture = Png(90_000);
        foreach (var part in Parts(id, 0, picture)) await wallet.Send(part);
        await wallet.Send(ReducedResult(id, "image/png", picture.Length, Sha(picture)));
        var done = await Done(jobs, started.Id!);
        Assert.True(done.Ok, done.Reason);
        Assert.True(Assert.Single(done.Images!).Reduced);

        var plain = jobs.Start(Rooms.A, Draw(token));
        var second = await wallet.NextOf("call");
        Assert.False(second.GetProperty("input").TryGetProperty("maxSide", out _));          // nothing asked, nothing passed on
        Assert.False(second.GetProperty("input").TryGetProperty("maxBytes", out _));
        var secondId = second.GetProperty("id").GetInt32();
        foreach (var part in Parts(secondId, 0, picture)) await wallet.Send(part);
        await wallet.Send(Result(secondId, ("image/png", picture.Length, Sha(picture))));
        Assert.False(Assert.Single((await Done(jobs, plain.Id!)).Images!).Reduced);
    }

    [Fact]
    public async Task MorePictureBytesThanEightMillionAreAFailedCall()
    {
        var (jobs, wallet, token, _, tcp) = await Pair();
        using var _t = tcp;
        var started = jobs.Start(Rooms.A, Draw(token, count: 2));
        var id = (await wallet.NextOf("call")).GetProperty("id").GetInt32();
        var first = Png(4_500_000, seed: 1);
        var second = Png(4_500_000, seed: 2);
        foreach (var part in Parts(id, 0, first).Concat(Parts(id, 1, second))) await wallet.Send(part);
        await wallet.Send(Result(id, ("image/png", first.Length, Sha(first)), ("image/png", second.Length, Sha(second))));

        var done = await Done(jobs, started.Id!);
        Assert.False(done.Ok);
        Assert.Equal("failed", done.Reason);
    }

    [Fact]
    public async Task PartsForACallThatIsNotAPictureCallOrForNoCallAreIgnored()
    {
        var (jobs, wallet, token, _, tcp) = await Pair();
        using var _t = tcp;
        var text = jobs.Start(Rooms.A, new JobRequest(token, "m1", [new JobMessage("user", "Hello?")], null, null));
        var id = (await wallet.NextOf("call")).GetProperty("id").GetInt32();
        foreach (var part in Parts(id, 0, Png(500)).Concat(Parts(999, 0, Png(500)))) await wallet.Send(part);
        await wallet.Send(WalletFrames.Result(id, true, "Hi."));

        var done = await Done(jobs, text.Id!);
        Assert.True(done.Ok);
        Assert.Equal("Hi.", done.Text);
        Assert.Null(done.Images);
    }

    [Fact]
    public async Task AWalletThatSaysNoToAPictureCallGivesItsReason()
    {
        var (jobs, wallet, token, _, tcp) = await Pair();
        using var _t = tcp;
        var started = jobs.Start(Rooms.A, Draw(token));
        var id = (await wallet.NextOf("call")).GetProperty("id").GetInt32();
        await wallet.Send(Parts(id, 0, Png(500))[0]);
        await wallet.Send(WalletFrames.Result(id, false, reason: "limit"));

        var done = await Done(jobs, started.Id!);
        Assert.False(done.Ok);
        Assert.Equal("limit", done.Reason);
        Assert.Null(done.Images);
    }

    [Fact]
    public async Task APictureJobIsCheckedBeforeItReachesTheWallet()
    {
        var (jobs, _, token, _, tcp) = await Pair();
        using var _t = tcp;
        var small = Convert.ToBase64String(Png(100));
        Assert.Equal("bad-request", jobs.Start(Rooms.A, Draw(token, prompt: " ")).Error);
        Assert.Equal("bad-request", jobs.Start(Rooms.A, Draw(token, count: 0)).Error);
        Assert.Equal("bad-request", jobs.Start(Rooms.A, Draw(token, count: 5)).Error);
        Assert.Equal("bad-request", jobs.Start(Rooms.A, Draw(token, aspect: "2:1")).Error);
        Assert.Equal("bad-request", jobs.Start(Rooms.A, Draw(token, references: [new JobPicture("text/plain", small)])).Error);
        Assert.Equal("bad-request", jobs.Start(Rooms.A, Draw(token, references: [new JobPicture("image/png", "not base64!")])).Error);
        Assert.Equal("bad-request", jobs.Start(Rooms.A, Draw(token, references: [new JobPicture("image/png", Convert.ToBase64String(Png(1_000_001)))])).Error);
        Assert.Equal("bad-request", jobs.Start(Rooms.A, Draw(token, references: [.. Enumerable.Range(0, 5).Select(_ => new JobPicture("image/png", small))])).Error);
        var big = Convert.ToBase64String(Png(900_000));
        Assert.Equal("bad-request", jobs.Start(Rooms.A, Draw(token, references: [.. Enumerable.Range(0, 4).Select(_ => new JobPicture("image/png", big))])).Error);
        Assert.Equal("bad-request", jobs.Start(Rooms.A, Draw(token) with { Skill = "audio.transcribe" }).Error);
        Assert.Equal("bad-model", jobs.Start(Rooms.A, Draw(token, model: "m2")).Error);          // a text participant is not a picture participant
        Assert.Null(jobs.Start(Rooms.A, Draw(token, references: [.. Enumerable.Range(0, 3).Select(_ => new JobPicture("image/png", big))])).Error);
    }

    [Fact]
    public async Task AWalletThatWasNotAllowedPicturesIsToldApart()
    {
        var (jobs, _, token, _, tcp) = await Pair(skills: ["text.generate"]);
        using var _t = tcp;
        Assert.Equal("no-pictures", jobs.Start(Rooms.A, Draw(token)).Error);
    }
}
