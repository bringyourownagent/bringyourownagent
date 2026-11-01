using System.Net.WebSockets;
using System.Text.Json.Nodes;
using Byoa.Wallet.Activity;
using Byoa.Wallet.Direct;
using Byoa.Wallet.Engine;
using Byoa.Wallet.Host;
using Byoa.Wallet.Protocol;
using Byoa.Wallet.Settings;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Tests;

/// <summary>
/// The pictures of an image.generate answer on the wire (PROPOSAL-v3 3.2 and 3.3): <c>image</c> frames of at most 524,288 base64 characters, then the result
/// that describes them, on the direct link and through the native host; and the capabilities and model lists a site is told. The picture is joined from
/// its parts the way a receiver would and compared by hash with the result.
/// </summary>
public class PictureTransportTests : IDisposable
{
    const string Origin = "https://icons.example.org";

    static readonly string PictureManifest = """
        {"byoa":"0.1-draft","purpose":{"summary":"Icon designer"},"privacy":"personal","expectedCost":{"perSession":"0.05 USD"},
         "skills":{"text.generate":{"required":true,"models":{"count":1}},
                   "image.generate":{"required":false,"models":{"count":{"min":1,"max":2}}}}}
        """;

    readonly ManualTime _time = new();
    readonly ScriptedUi _ui = new();
    readonly FakeTextSource _gemma = new("lm:gemma");
    readonly FakeImageSource _draw = new("openrouter:free/draw", costPerCall: 0);
    readonly SettingsStore _store = new(null);
    readonly ActivityLog _log;
    readonly WalletEngine _engine;
    readonly PairingStore _pairings = new(null);
    readonly TestSite _site = new();
    readonly List<DirectTransport> _transports = [];

    public PictureTransportTests()
    {
        _log = new ActivityLog(null, _time.Now);
        _engine = new WalletEngine(_store, [], _log, _ui, _time, [_gemma, _draw])
        {
            Catalog = new ListedCatalog(new CatalogModel("free/draw", "Free: Draw", 32_768, 0m, 0m, 0, ["text"], ["image"])),
        };
        _store.Update(s =>
        {
            s.Sources.Add(new SourceSetting { Id = "lm:gemma", Kind = SourceKinds.LmStudio, Model = "google/gemma-3-1b-it", Skills = [Vocabulary.Generate] });
            s.Sources.Add(new SourceSetting { Id = "openrouter:free/draw", Kind = SourceKinds.OpenRouter, Model = "free/draw", Skills = [Vocabulary.Generate], Offer = false });
            Router.EnsureTextChain(s, [_gemma, _draw]);
        });
        _site.Manifest = PictureManifest;
    }

    public void Dispose()
    {
        foreach (var t in _transports) t.Dispose();
        _site.Dispose();
    }

    static ConsentAnswer Allow() => new(true, 0.05m, false, [], new Dictionary<string, IReadOnlyList<string>>
    {
        ["text.generate"] = ["lm:gemma"],
        ["image.generate"] = ["openrouter:free/draw"],
    });

    static JsonObject DrawCall(int id, string? model = "m2", int count = 1, int maxBytes = 3_000_000) => new()
    {
        ["type"] = "call", ["id"] = id, ["skill"] = "image.generate",
        ["input"] = new JsonObject { ["prompt"] = "a fox", ["count"] = count, ["maxBytes"] = maxBytes },
        ["params"] = model is null ? new JsonObject() : new JsonObject { ["model"] = model },
    };

    /// <summary>A picture the way a receiver rebuilds it: the parts in <c>seq</c> order, each of at most 524,288 characters, checked against the result's size and hash.</summary>
    static byte[] Join(IReadOnlyList<JsonObject> frames, int index, JsonObject description)
    {
        var parts = frames.Where(f => f["index"]!.GetValue<int>() == index).OrderBy(f => f["seq"]!.GetValue<int>()).ToList();
        Assert.NotEmpty(parts);
        Assert.Equal(Enumerable.Range(0, parts.Count), parts.Select(p => p["seq"]!.GetValue<int>()));
        Assert.All(parts, p =>
        {
            Assert.Equal(parts.Count, p["of"]!.GetValue<int>());
            Assert.Equal(description["mime"]!.GetValue<string>(), p["mime"]!.GetValue<string>());
            Assert.True(p["data"]!.GetValue<string>().Length <= PictureFrames.PartChars);
        });
        var bytes = parts.SelectMany(p => Convert.FromBase64String(p["data"]!.GetValue<string>())).ToArray();
        Assert.Equal(description["size"]!.GetValue<int>(), bytes.Length);
        Assert.Equal(description["sha256"]!.GetValue<string>(), TestPictures.Sha256(bytes));
        return bytes;
    }

    // ------------------------------------------------------------------ the frames themselves

    [Fact]
    public void APictureIsCutIntoPartsOfAtMost524288CharactersAndEveryPartKnowsItsPlace()
    {
        var result = new JsonObject { ["ok"] = true, ["output"] = new JsonObject { ["images"] = new JsonArray(new JsonObject { ["mime"] = "image/png" }, new JsonObject { ["mime"] = "image/jpeg" }) } };
        var big = Enumerable.Range(0, 393_216 * 2 + 5).Select(i => (byte)i).ToArray();
        var frames = PictureFrames.Of(JsonValue.Create(7)!, result, [big, [1, 2, 3]]).ToList();

        Assert.Equal(4, frames.Count);                                                      // 3 parts and 1
        Assert.Equal(["image", "image", "image", "image"], frames.Select(f => f["type"]!.GetValue<string>()));
        Assert.Equal([7, 7, 7, 7], frames.Select(f => f["id"]!.GetValue<int>()));
        Assert.Equal([0, 0, 0, 1], frames.Select(f => f["index"]!.GetValue<int>()));
        Assert.Equal([0, 1, 2, 0], frames.Select(f => f["seq"]!.GetValue<int>()));
        Assert.Equal([3, 3, 3, 1], frames.Select(f => f["of"]!.GetValue<int>()));
        Assert.Equal(["image/png", "image/png", "image/png", "image/jpeg"], frames.Select(f => f["mime"]!.GetValue<string>()));
        Assert.Equal(524_288, frames[0]["data"]!.GetValue<string>().Length);                // the largest part is exactly the limit
        Assert.Equal(big, frames.Take(3).SelectMany(f => Convert.FromBase64String(f["data"]!.GetValue<string>())).ToArray());
        Assert.All(frames, f => Assert.True(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(f).Length < Framing.MaxToBrowser));
    }

    [Fact]
    public void OnlyASuccessWithPicturesCarriesThem()
    {
        var ok = new JsonObject { ["ok"] = true };
        Assert.True(PictureFrames.Carries(ok, [[1]]));
        Assert.False(PictureFrames.Carries(ok, []));
        Assert.False(PictureFrames.Carries(Wire.Failure(), [[1]]));
        Assert.False(PictureFrames.Carries(null, [[1]]));
    }

    [Fact]
    public void TheCapabilitiesOfAGrantNameTheSkillAndTheParts()
    {
        Assert.Equal(["needs", "can", "kind", "cancel", "image.generate", "parts"], GrantSession.Capabilities);
        Assert.DoesNotContain("images-in", GrantSession.Capabilities);                       // reserved: nobody sends them
        Assert.DoesNotContain("frame-limit", GrantSession.Capabilities);
    }

    // ------------------------------------------------------------------ the direct link

    async Task<(WebSocket Socket, JsonObject Hello)> ConnectAsync()
    {
        _ui.Consent = Allow();
        var transport = new DirectTransport(_engine, _pairings, new DirectOptions
        {
            DevLocal = true, PingInterval = TimeSpan.FromSeconds(30), WelcomeTimeout = TimeSpan.FromSeconds(5),
        }, _time);
        _transports.Add(transport);
        var connection = await transport.HandleLinkAsync(_site.Link());
        Assert.NotNull(connection);
        var socket = await _site.NextSocketAsync();
        var hello = await TestSite.ReceiveAsync(socket);
        await TestSite.SendAsync(socket, _site.Welcome());
        Assert.True(await connection!.Welcomed.WaitAsync(TimeSpan.FromSeconds(10)));
        return (socket, hello);
    }

    /// <summary>The frames the wallet sends for one call: its image frames and, last, its result.</summary>
    static async Task<(List<JsonObject> Images, JsonObject Result)> AnswerAsync(WebSocket socket)
    {
        var images = new List<JsonObject>();
        while (true)
        {
            var frame = await TestSite.ReceiveAsync(socket);
            if (frame["type"]!.GetValue<string>() == "image") images.Add(frame);
            else return (images, frame);
        }
    }

    [Fact]
    public async Task TheHelloTellsTheSiteTheCapabilitiesAndBothListsOfModelsWithRefsThatDoNotRepeat()
    {
        var (_, hello) = await ConnectAsync();
        var grant = hello["grant"]!.AsObject();
        Assert.Equal(["needs", "can", "kind", "cancel", "image.generate", "parts"], grant["capabilities"]!.AsArray().Select(c => c!.GetValue<string>()));
        Assert.Equal(["image.generate", "text.generate"], grant["skills"]!.AsArray().Select(s => s!.GetValue<string>()).Order());
        Assert.Equal("m1", grant["models"]!["text.generate"]![0]!["ref"]!.GetValue<string>());
        Assert.Equal("m2", grant["models"]!["image.generate"]![0]!["ref"]!.GetValue<string>());
        Assert.NotNull(grant["limits"]);
    }

    [Fact]
    public async Task APictureComesAsImageFramesThenTheResultAndJoinsToTheSameBytes()
    {
        var (socket, _) = await ConnectAsync();
        var big = TestPictures.Png(650, 650, noise: true);                                   // a bit over a megabyte: four parts
        var small = TestPictures.Png(16, 16);
        Assert.True(big.Length > 1_000_000);
        _draw.Pictures.Enqueue(_ => new ImageAnswer([big, small], null));

        await TestSite.SendAsync(socket, DrawCall(7, count: 2));
        var (frames, result) = await AnswerAsync(socket);

        Assert.Equal("result", result["type"]!.GetValue<string>());
        Assert.Equal(7, result["id"]!.GetValue<int>());
        Assert.True(result["ok"]!.GetValue<bool>());
        var described = result["output"]!["images"]!.AsArray().Select(i => i!.AsObject()).ToList();
        Assert.Equal(2, described.Count);
        Assert.Equal(5, frames.Count);                                                       // four parts of the big picture and one of the small
        Assert.Equal(big, Join(frames, 0, described[0]));
        Assert.Equal(small, Join(frames, 1, described[1]));
        Assert.Equal(4, frames.Count(f => f["index"]!.GetValue<int>() == 0));
        Assert.All(frames, f => Assert.Equal(7, f["id"]!.GetValue<int>()));
        Assert.Equal("m2", result["receipt"]!["model"]!.GetValue<string>());
    }

    [Fact]
    public async Task ACallThatFailsGetsAResultAndNoImageFrame()
    {
        var (socket, _) = await ConnectAsync();
        await TestSite.SendAsync(socket, DrawCall(8, model: null));
        var (frames, result) = await AnswerAsync(socket);
        Assert.Empty(frames);
        Assert.False(result["ok"]!.GetValue<bool>());
        Assert.Equal("failed", result["reason"]!.GetValue<string>());
        Assert.Equal(0, _draw.ImageCalls);
    }

    [Fact]
    public async Task ACancelBeforeThePictureCameEndsTheCallAndNoPartIsSent()
    {
        var (socket, _) = await ConnectAsync();
        _draw.HoldPicture = new TaskCompletionSource();
        await TestSite.SendAsync(socket, DrawCall(9));
        await Task.Delay(300);
        await TestSite.SendAsync(socket, new JsonObject { ["type"] = "cancel", ["id"] = 9 });
        var (frames, result) = await AnswerAsync(socket);
        Assert.Empty(frames);
        Assert.Equal("cancelled", result["reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task AnImageFrameFromTheSiteIsIgnoredAndTheConnectionCarriesOn()
    {
        var (socket, _) = await ConnectAsync();
        await TestSite.SendAsync(socket, new JsonObject { ["type"] = "image", ["id"] = 99, ["index"] = 0, ["seq"] = 0, ["of"] = 1, ["mime"] = "image/png", ["data"] = "AAAA" });
        await TestSite.SendAsync(socket, DrawCall(10));
        var (frames, result) = await AnswerAsync(socket);
        Assert.True(result["ok"]!.GetValue<bool>());
        Assert.Single(frames);
    }

    // ------------------------------------------------------------------ the native host

    static byte[] Message(int id, JsonObject payload) =>
        System.Text.Encoding.UTF8.GetBytes(new JsonObject { ["id"] = id, ["type"] = "call", ["origin"] = Origin, ["userActivation"] = false, ["payload"] = payload }.ToJsonString());

    [Fact]
    public async Task ThroughTheHostAPictureComesAsFramesOfAtMostAMegabyteThenTheResponse()
    {
        // the extension <-> the host <-> the wallet's pipe <-> the wallet
        var (browser, hostSide) = HostRelayTests.Pair();
        var (hostToWallet, walletSide) = HostRelayTests.Pair();
        var connector = new HostRelayTests.Connector(_ => hostToWallet);
        var relay = Task.Run(() => new HostRelay(hostSide, hostSide, connector).RunAsync());
        var frames = new FrameServer((message, pictures, ct) => message.Type == Wire.Link ? Task.FromResult<JsonNode?>(null) : _engine.HandleAsync(message, ct, pictures));
        var serving = Task.Run(() => frames.ServeAsync(walletSide, CancellationToken.None));

        _ui.Consent = Allow();
        var granted = (JsonObject)(await _engine.HandleAsync(Clips.Request(Origin, click: true, PictureManifest)))!;
        var big = TestPictures.Png(650, 650, noise: true);
        _draw.Pictures.Enqueue(_ => new ImageAnswer([big], null));

        var payload = new JsonObject
        {
            ["grantId"] = granted["grantId"]!.GetValue<string>(), ["skill"] = "image.generate",
            ["input"] = new JsonObject { ["prompt"] = "a fox", ["maxBytes"] = 3_000_000 },
            ["params"] = new JsonObject { ["model"] = "m2" },
        };
        await Framing.WriteAsync(browser, Message(21, payload));

        var images = new List<JsonObject>();
        JsonObject response;
        while (true)
        {
            var raw = (await Framing.ReadAsync(browser).WaitAsync(TimeSpan.FromSeconds(20)))!;
            Assert.True(raw.Length <= Framing.MaxToBrowser, "a frame to the browser was " + raw.Length + " bytes");
            var frame = (JsonObject)JsonNode.Parse(raw)!;
            if (frame["type"]?.GetValue<string>() == "image") images.Add(frame);
            else
            {
                response = frame;
                break;
            }
        }

        Assert.Equal(21, response["id"]!.GetValue<int>());
        var result = response["result"]!.AsObject();
        Assert.True(result["ok"]!.GetValue<bool>());
        Assert.True(images.Count >= 4);
        Assert.All(images, f => Assert.Equal(21, f["id"]!.GetValue<int>()));
        Assert.Equal(big, Join(images, 0, result["output"]!["images"]![0]!.AsObject()));

        browser.Dispose();
        await relay.WaitAsync(TimeSpan.FromSeconds(10));
        hostToWallet.Dispose();                                                              // the host lets go of the wallet's pipe: its server ends
        await serving.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task TheHostPassesAnImageFrameOnWhileItsCallRunsAndIgnoresOneForACallThatIsNot()
    {
        var (browser, hostSide) = HostRelayTests.Pair();
        var (hostToWallet, wallet) = HostRelayTests.Pair();
        var relay = Task.Run(() => new HostRelay(hostSide, hostSide, new HostRelayTests.Connector(_ => hostToWallet)).RunAsync());

        await Framing.WriteAsync(browser, HostRelayTests.Message(1, "call"));
        Assert.Equal(1, (await HostRelayTests.ReadAsync(wallet))["id"]!.GetValue<int>());           // the call is at the wallet

        byte[] Image(int id, int seq) => System.Text.Encoding.UTF8.GetBytes(new JsonObject
        {
            ["type"] = "image", ["id"] = id, ["index"] = 0, ["seq"] = seq, ["of"] = 2, ["mime"] = "image/png", ["data"] = "AAAA",
        }.ToJsonString());
        await Framing.WriteAsync(wallet, Image(99, 0));                                              // not a running call: ignored
        await Framing.WriteAsync(wallet, Image(1, 0));
        await Framing.WriteAsync(wallet, Image(1, 1));
        await Framing.WriteAsync(wallet, System.Text.Encoding.UTF8.GetBytes("{\"id\":1,\"result\":{\"ok\":true}}"));
        await Framing.WriteAsync(wallet, Image(1, 0));                                               // after the result: the call is over, ignored

        var first = await HostRelayTests.ReadAsync(browser);
        var second = await HostRelayTests.ReadAsync(browser);
        var last = await HostRelayTests.ReadAsync(browser);
        Assert.Equal([("image", 1, 0), ("image", 1, 1)], [(first["type"]!.GetValue<string>(), first["id"]!.GetValue<int>(), first["seq"]!.GetValue<int>()), (second["type"]!.GetValue<string>(), second["id"]!.GetValue<int>(), second["seq"]!.GetValue<int>())]);
        Assert.Equal("{\"id\":1,\"result\":{\"ok\":true}}", last.ToJsonString());

        // nothing else was written to the browser: the next frame it gets is the answer to a new message
        await Framing.WriteAsync(browser, HostRelayTests.Message(2, "call"));
        await HostRelayTests.ReadAsync(wallet);
        await Framing.WriteAsync(wallet, System.Text.Encoding.UTF8.GetBytes("{\"id\":2,\"result\":{\"ok\":false,\"reason\":\"failed\"}}"));
        Assert.Equal(2, (await HostRelayTests.ReadAsync(browser))["id"]!.GetValue<int>());

        browser.Dispose();
        await relay.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
