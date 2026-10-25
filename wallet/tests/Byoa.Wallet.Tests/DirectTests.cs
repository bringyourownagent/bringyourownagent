using System.Buffers.Text;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Byoa.Wallet.Activity;
using Byoa.Wallet.Direct;
using Byoa.Wallet.Engine;
using Byoa.Wallet.Host;
using Byoa.Wallet.Protocol;
using Byoa.Wallet.Settings;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Tests;

/// <summary>bringyourownagent://connect links: parsing, the manifest inside the link (§7), and refusing hostile links.</summary>
public class DirectLinkTests
{
    const string Code = "s3ss10n-c0de_with-enough-bits-0123456789";
    static readonly string GeoManifest64 = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(Clips.GeoManifest));

    static string Link(string endpoint, string? manifest = null, string session = Code, string extra = "") =>
        "bringyourownagent://connect?v=0.1&endpoint=" + Uri.EscapeDataString(endpoint) + "&manifest=" + (manifest ?? GeoManifest64) + "&session=" + session + extra;

    static string Good(string extra = "") => Link("wss://geo.example.org/byoa/ws", extra: extra);

    static string B64(string json) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void AGoodLinkGivesTheSitesOriginEndpointCodeAndTheManifestItCarries()
    {
        Assert.True(DirectLink.TryParse(Good(), devLocal: false, out var link, out var why), why);
        Assert.Equal("https://geo.example.org", link!.Origin);
        Assert.Equal("geo.example.org", link.Host);
        Assert.Equal("wss://geo.example.org/byoa/ws", link.Endpoint.ToString());
        Assert.Equal(GeoManifest64, link.Manifest);                                   // kept as it came, for the welcome's echo
        Assert.Equal("0.1-draft", link.ManifestJson["byoa"]!.GetValue<string>());
        Assert.Equal(Code, link.Session);
    }

    [Fact]
    public void APortIsPartOfTheOriginAndTrailingSlashesAndCaseAreTolerated()
    {
        var text = Link("wss://Geo.Example.org:8443/byoa/ws").Replace("bringyourownagent://connect?", "BringYourOwnAgent://connect/?");
        Assert.True(DirectLink.TryParse(text, false, out var link, out var why), why);
        Assert.Equal("https://geo.example.org:8443", link!.Origin);
        Assert.Equal("geo.example.org:8443", link.Host);
        Assert.True(DirectLink.TryParse(Good("&future=1"), false, out _, out why), why);   // unknown extra parameters are ignored
    }

    [Fact]
    public void TheManifestMayBe4096BytesNotMore()
    {
        var pad = new string('x', 4096 - "{\"x\":\"\"}".Length);
        Assert.True(DirectLink.TryParse(Link("wss://geo.example.org/ws", B64("{\"x\":\"" + pad + "\"}")), false, out _, out var why), why);
        Assert.False(DirectLink.TryParse(Link("wss://geo.example.org/ws", B64("{\"x\":\"" + pad + "x\"}")), false, out _, out why));
        Assert.Equal("the manifest is larger than 4096 bytes", why);
    }

    public static TheoryData<string> Hostile() => new()
    {
        "",
        "byoa://connect?v=0.1&endpoint=wss%3A%2F%2Fgeo.example.org%2Fws&manifest=" + GeoManifest64 + "&session=" + Code,
        "javascript:alert(1)",
        "bringyourownagent://evil?v=0.1",
        Good().Replace("://connect?", "://connect/more?"),
        Good().Replace("://connect?", "://user@connect?"),
        Good().Replace("://connect?", "://connect:99?"),
        Good() + "#fragment",
        Good().Replace("v=0.1", "v=0.2"),
        Good().Replace("v=0.1&", ""),
        Good().Replace(Code, "short"),
        Good().Replace(Code, Code + "%2F"),
        Good().Replace(Code, Code + ".x"),
        Good().Replace("&session=" + Code, ""),
        Good("&endpoint=" + Uri.EscapeDataString("wss://evil.example.com/ws")),       // a second endpoint
        Good() + " ",
        Good().Replace("geo.example", "géo.example"),
        Good() + new string('a', 13000),
        // the manifest: the old form (a URL the wallet would have fetched), padding, plain base64, not JSON, not an object, missing
        Link("wss://geo.example.org/ws", Uri.EscapeDataString("https://geo.example.org/byoa/manifest.json?track=1")),
        Link("wss://geo.example.org/ws", GeoManifest64 + "=="),
        Link("wss://geo.example.org/ws", Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"a\":\"ÿÿÿþ\"}")).TrimEnd('=')),   // + and /
        Link("wss://geo.example.org/ws", B64("not json")),
        Link("wss://geo.example.org/ws", B64("[1,2,3]")),
        Link("wss://geo.example.org/ws", "A"),
        Good().Replace("&manifest=" + GeoManifest64, ""),
        // the endpoint
        Link("ws://geo.example.org/ws"),                                               // not wss
        Link("https://geo.example.org/ws"),
        Link("wss://127.0.0.1/ws"),
        Link("wss://[::1]/ws"),
        Link("wss://10.0.0.5/ws"),
        Link("wss://2130706433/ws"),                                                   // 127.0.0.1 as one number
        Link("wss://0x7f.0.0.1/ws"),
        Link("wss://localhost/ws"),
        Link("wss://app.localhost/ws"),
        Link("wss://printer.local/ws"),
        Link("wss://nas.home.arpa/ws"),
        Link("wss://intranet/ws"),
        Link("wss://geo.example.org./ws"),
        Link("wss://user:pass@geo.example.org/ws"),
        Link("wss://geo.example.org/ws#x"),
        Link("wss://geo.example.org\\@evil.example.com/ws"),
        Link("wss://geo.example.org/ws\r\nX: 1"),                                      // header injection
        Link("ws://localhost:5192/byoa/ws"),                                           // localhost without the developer switch
    };

    [Theory]
    [MemberData(nameof(Hostile))]
    public void HostileAndMalformedLinksAreRefused(string text)
    {
        Assert.False(DirectLink.TryParse(text, devLocal: false, out var link, out var why));
        Assert.Null(link);
        Assert.False(string.IsNullOrWhiteSpace(why));
    }

    [Fact]
    public void TheDeveloperSwitchAllowsOnlyPlainLocalhost()
    {
        Assert.True(DirectLink.TryParse(Link("ws://localhost:5192/byoa/ws"), devLocal: true, out var link, out var why), why);
        Assert.Equal("http://localhost:5192", link!.Origin);                         // the same origin as an extension page there
        Assert.False(DirectLink.TryParse(Link("ws://127.0.0.1:5192/ws"), true, out _, out _));
        Assert.False(DirectLink.TryParse(Link("ws://geo.example.org/ws"), true, out _, out _));
    }

    [Theory]
    [InlineData("0123456789abcdefABCDEF", true)]
    [InlineData("0123456789abcdefABCDE", false)]
    [InlineData("0123456789abcdefABCD+F", false)]
    [InlineData("0123456789abcdef_ABC-EF", true)]
    public void ASessionCodeHasAtLeast128BitsOfUrlSafeCharacters(string code, bool ok) => Assert.Equal(ok, DirectLink.IsSessionCode(code));

    [Theory]
    [InlineData("0123456789abcdefABCDEF", true)]
    [InlineData("0123456789abcdefABCDE", false)]
    [InlineData("0123456789abcdefABCDEF+", false)]
    [InlineData("0123456789abcdefABCDEF ", false)]
    public void APairingTokenHasAtLeast22UrlSafeCharacters(string token, bool ok) => Assert.Equal(ok, DirectConnection.IsToken(token));

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("1.1.1.1", true)]
    [InlineData("172.32.0.1", true)]
    [InlineData("100.128.0.1", true)]
    [InlineData("2606:4700:4700::1111", true)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("172.31.255.255", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("169.254.1.1", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("255.255.255.255", false)]
    [InlineData("::1", false)]
    [InlineData("::", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fc00::1", false)]
    [InlineData("fd12:3456::1", false)]
    [InlineData("ff02::1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    [InlineData("::ffff:8.8.8.8", true)]
    [InlineData("2001:db8::1", false)]
    [InlineData("2002:c0a8:101::1", false)]
    [InlineData("64:ff9b::a00:1", false)]
    public void OnlyPublicAddressesMayBeConnectedTo(string address, bool ok) => Assert.Equal(ok, AddressCheck.IsPublic(IPAddress.Parse(address)));
}

/// <summary>The stored pairings: DPAPI on disk, one per origin, forgotten on demand.</summary>
public class PairingStoreTests
{
    static Pairing P(string origin, string token) => new(origin, "wss://x.example.org/ws", "eyJieW9hIjoiMC4xLWRyYWZ0In0", token, DateTimeOffset.UnixEpoch);

    [Fact]
    public void PairingsRoundTripThroughDpapiAndAreNeverPlainOnDisk()
    {
        var path = Path.Combine(TestFolder.New(), "pairings.dat");
        var store = new PairingStore(path);
        store.Set(P("https://a.example.org", "token-of-a-0123456789-abcdef"));
        store.Set(P("https://b.example.org", "token-of-b-0123456789-abcdef"));
        store.Set(P("https://a.example.org", "token-of-a2-0123456789-abcdef"));      // one per origin: the newer one
        var again = new PairingStore(path);
        Assert.Equal("token-of-a2-0123456789-abcdef", again.Get("https://a.example.org")!.Token);
        Assert.Equal(2, again.All().Count);
        var bytes = File.ReadAllBytes(path);
        Assert.DoesNotContain("token-of", Encoding.UTF8.GetString(bytes));
        Assert.DoesNotContain("example.org", Encoding.UTF8.GetString(bytes));

        Assert.False(again.Forget("https://a.example.org", "another-token"));         // only the same token is forgotten
        Assert.True(again.Forget("https://a.example.org", "token-of-a2-0123456789-abcdef"));
        Assert.True(again.Forget("https://b.example.org"));
        Assert.Empty(new PairingStore(path).All());
    }

    [Fact]
    public void ADamagedFileIsNoPairings()
    {
        var path = Path.Combine(TestFolder.New(), "pairings.dat");
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        Assert.Empty(new PairingStore(path).All());
    }
}

/// <summary>Review 2 #7: handing a link to the running wallet never hangs on a wallet that does not answer.</summary>
public class LinkHandOffTests
{
    static string PipeName() => "byoa-test-handoff-" + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task AWalletThatTakesTheLinkButNeverAnswersDoesNotHoldTheSecondProcess()
    {
        var name = PipeName();
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var accepted = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            return await Framing.ReadAsync(server);                                 // reads the link, never answers
        });
        var watch = Stopwatch.StartNew();
        var ok = await Task.Run(() => LinkHandOff.Send(name, "bringyourownagent://connect?v=0.1", TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1), userActivation: false))
            .WaitAsync(TimeSpan.FromSeconds(10));                                   // a hang fails the test instead of stopping it
        Assert.False(ok);
        Assert.InRange(watch.Elapsed.TotalSeconds, 0.9, 4);
        var frame = JsonNode.Parse((await accepted.WaitAsync(TimeSpan.FromSeconds(5)))!)!;
        Assert.Equal("link", frame["type"]!.GetValue<string>());
        Assert.False(frame["userActivation"]!.GetValue<bool>());
        Assert.Equal("bringyourownagent://connect?v=0.1", frame["payload"]!["url"]!.GetValue<string>());
    }

    [Fact]
    public async Task AnAnsweringWalletConfirmsTheLink()
    {
        var name = PipeName();
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var received = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            return JsonNode.Parse((await Framing.ReadAsync(server))!)!;
        });
        _ = Task.Run(async () =>
        {
            await received;
            await Framing.WriteAsync(server, Encoding.UTF8.GetBytes("{\"id\":1,\"result\":{\"accepted\":true}}"));
        });
        Assert.True(await Task.Run(() => LinkHandOff.Send(name, "bringyourownagent://connect", TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(3))));
        Assert.True((await received).AsObject()["userActivation"]!.GetValue<bool>());
    }

    [Fact]
    public void NoRunningWalletGivesUpAfterTheTryTime()
    {
        var watch = Stopwatch.StartNew();
        Assert.False(LinkHandOff.Send(PipeName(), "bringyourownagent://connect", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)));
        Assert.InRange(watch.Elapsed.TotalSeconds, 0.9, 6);
    }
}

/// <summary>
/// A small site for the tests: a TCP server on 127.0.0.1 that accepts WebSocket upgrades (the test drives each socket
/// itself) and counts every connection, so a test can prove that nothing reached the site before Allow.
/// </summary>
sealed class TestSite : IDisposable
{
    readonly TcpListener _listener;
    readonly CancellationTokenSource _stop = new();
    readonly Channel<WebSocket> _sockets = Channel.CreateUnbounded<WebSocket>();
    int _accepts;
    int _upgrades;

    /// <param name="port">A fixed port (0: any free one).</param>
    /// <param name="listen">False: the site is down until <see cref="Listen"/>.</param>
    public TestSite(int port = 0, bool listen = true)
    {
        if (port == 0)
        {
            // A free port, fixed from now on (a listener made for port 0 would take a new one when started again).
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
        }

        Port = port;
        _listener = new TcpListener(IPAddress.Loopback, port);
        if (listen) Listen();
    }

    public void Listen()
    {
        _listener.Start();
        _ = AcceptAsync();
    }

    public int Port { get; }
    public string Origin => "http://localhost:" + Port;
    public string Manifest { get; set; } = Clips.GeoManifest;

    /// <summary>The manifest as the link carries it and the welcome echoes it.</summary>
    public string Manifest64 => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(Manifest));

    /// <summary>Every connection the site saw (WebSocket or anything else).</summary>
    public int Accepts => Volatile.Read(ref _accepts);
    public int Upgrades => Volatile.Read(ref _upgrades);

    public string Link(string? session = null) =>
        "bringyourownagent://connect?v=0.1&endpoint=" + Uri.EscapeDataString($"ws://localhost:{Port}/byoa/ws")
        + "&manifest=" + Manifest64 + "&session=" + (session ?? NewCode());

    public static string NewCode() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).Replace('+', '-').Replace('/', '_');

    public async Task<WebSocket> NextSocketAsync(int seconds = 10) => await _sockets.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(seconds));

    public JsonObject Welcome(string token = "pairing-token-0123456789abcdef", string? manifest = null) =>
        new() { ["type"] = "welcome", ["pairing"] = token, ["manifest"] = manifest ?? Manifest64 };

    async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            Interlocked.Increment(ref _accepts);
            _ = ServeAsync(client);
        }
    }

    async Task ServeAsync(TcpClient client)
    {
        var stream = client.GetStream();
        var head = new StringBuilder();
        var one = new byte[1];
        while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal) && head.Length < 16384)
        {
            if (await stream.ReadAsync(one) == 0) return;
            head.Append((char)one[0]);
        }

        var text = head.ToString();
        if (!text.Contains("Upgrade: websocket", StringComparison.OrdinalIgnoreCase))
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
            client.Dispose();
            return;
        }

        var key = text.Split("\r\n").First(l => l.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))[18..].Trim();
        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n"));
        Interlocked.Increment(ref _upgrades);
        await _sockets.Writer.WriteAsync(WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = TimeSpan.Zero }));
    }

    public static async Task SendAsync(WebSocket ws, JsonObject frame) =>
        await ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(frame), WebSocketMessageType.Text, true, CancellationToken.None);

    public static Task SendTextAsync(WebSocket ws, string text) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);

    /// <summary>The next JSON frame from the wallet, skipping its pings; a close comes back as <c>{"type":"(close)"}</c>.</summary>
    public static async Task<JsonObject> ReceiveAsync(WebSocket ws, int seconds = 10, bool skipPings = true)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        while (true)
        {
            var buffer = new byte[64 * 1024];
            using var message = new MemoryStream();
            WebSocketReceiveResult r;
            do
            {
                try
                {
                    r = await ws.ReceiveAsync(buffer, limit.Token);
                }
                catch (WebSocketException)
                {
                    return new JsonObject { ["type"] = "(close)" };
                }

                if (r.MessageType == WebSocketMessageType.Close)
                {
                    if (ws.State == WebSocketState.CloseReceived) await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                    return new JsonObject { ["type"] = "(close)", ["status"] = (int?)r.CloseStatus };
                }

                message.Write(buffer, 0, r.Count);
            }
            while (!r.EndOfMessage);

            var frame = (JsonObject)JsonNode.Parse(message.ToArray())!;
            if (skipPings && frame["type"]?.GetValue<string>() == "ping") continue;
            return frame;
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
    }
}

/// <summary>
/// The direct transport end to end inside the process: a link with its manifest, the consent (nothing reaches the
/// site before Allow), the wallet's own connection (hello, welcome with the manifest's echo, calls and results, ping,
/// bye), pairing, reconnect with backoff, a site that asks for more on a reconnect, revoke, and the same gate,
/// limits, routing and "ask before paid" as for the extension's calls.
/// </summary>
public class DirectTransportTests : IDisposable
{
    readonly ManualTime _time = new();
    readonly ScriptedUi _ui = new();
    readonly FakeSource _russian = new("local-ru", languages: ["ru"]) { Answer = "francia-russian" };
    readonly FakeSource _general = new("local-any") { Answer = "francia-general" };
    readonly FakeSource _paid = new("openrouter-scribe", Runs.ApiKey, pricePerSecond: 0.00004m) { Answer = "francia-paid" };
    readonly SettingsStore _store = new(null);
    readonly ActivityLog _log;
    readonly WalletEngine _engine;
    readonly PairingStore _pairings = new(null);
    readonly TestSite _site = new();
    readonly List<DirectTransport> _transports = [];

    public DirectTransportTests()
    {
        _log = new ActivityLog(null, _time.Now);
        _engine = SpeechRoutes.Engine(_store, [_russian, _general, _paid], _log, _ui, _time);
    }

    DirectTransport Transport(WalletEngine? engine = null, int maxFrame = 4 * 1024 * 1024, double pingSeconds = 30,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null, double sessionWindowSeconds = 60)
    {
        var t = new DirectTransport(engine ?? _engine, _pairings, new DirectOptions
        {
            DevLocal = true,
            Backoff = [TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100)],
            PingInterval = TimeSpan.FromSeconds(pingSeconds),
            WelcomeTimeout = TimeSpan.FromSeconds(5),
            MaxFrameBytes = maxFrame,
            Resolve = resolve,
            SessionRetryWindow = TimeSpan.FromSeconds(sessionWindowSeconds),
        }, _time);
        _transports.Add(t);
        return t;
    }

    public void Dispose()
    {
        foreach (var t in _transports) t.Dispose();
        _site.Dispose();
    }

    List<ActivityEntry> Links => _log.Recent().Where(e => e.Kind == "link").ToList();

    static async Task WaitUntil(Func<bool> condition, int seconds = 10)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(seconds)) throw new TimeoutException();
            await Task.Delay(10);
        }
    }

    static JsonObject Call(int id, string language = "ru", double seconds = 2) => new()
    {
        ["type"] = "call", ["id"] = id, ["skill"] = "audio.transcribe",
        ["input"] = new JsonObject { ["audio"] = new JsonObject { ["mime"] = "audio/wav", ["base64"] = Clips.Base64(seconds) } },
        ["params"] = new JsonObject { ["language"] = language },
    };

    static string Manifest(string cost) => Clips.GeoManifest.Replace("0.05 USD", cost);

    /// <summary>A link to the test site that carries the given manifest text.</summary>
    string LinkWith(string manifest) =>
        "bringyourownagent://connect?v=0.1&endpoint=" + Uri.EscapeDataString($"ws://localhost:{_site.Port}/byoa/ws")
        + "&manifest=" + Base64Url.EncodeToString(Encoding.UTF8.GetBytes(manifest)) + "&session=" + TestSite.NewCode();

    /// <summary>A link, the visitor's Allow, the wallet's hello, the site's welcome: the open connection and its socket.</summary>
    async Task<(DirectTransport Transport, DirectConnection Connection, WebSocket Socket, JsonObject Hello)> PairAsync(bool auto, string token = "pairing-token-0123456789abcdef")
    {
        _ui.Consent = new ConsentAnswer(true, 0.05m, AutoDecide: auto, []);
        var transport = Transport();
        var code = TestSite.NewCode();
        var connection = await transport.HandleLinkAsync(_site.Link(code));
        Assert.NotNull(connection);
        var socket = await _site.NextSocketAsync();
        var hello = await TestSite.ReceiveAsync(socket);
        Assert.Equal(code, hello["session"]!.GetValue<string>());
        await TestSite.SendAsync(socket, _site.Welcome(token));
        Assert.True(await connection!.Welcomed.WaitAsync(TimeSpan.FromSeconds(10)));
        return (transport, connection, socket, hello);
    }

    [Fact]
    public async Task ALinkAsksTheVisitorThenTheWalletSaysHelloAndAnswersCallsThroughTheEngine()
    {
        var (_, connection, socket, hello) = await PairAsync(auto: false);
        Assert.Equal(1, _ui.Notices);                                              // the same notice and consent
        Assert.Equal("Family geography game: recognise country names spoken aloud", Assert.Single(_ui.Consents).Summary);   // from the link's manifest
        Assert.True(Assert.Single(_ui.Consents).FromLink);
        Assert.Equal(1, _site.Accepts);                                            // the one connection, after Allow
        Assert.Equal("hello", hello["type"]!.GetValue<string>());
        Assert.Equal("0.1", hello["v"]!.GetValue<string>());
        Assert.Null(hello["pairing"]);
        Assert.Equal("Agent wallet", hello["wallet"]!["name"]!.GetValue<string>());
        Assert.Equal("[\"audio.transcribe\"]", hello["grant"]!["skills"]!.ToJsonString());
        Assert.NotNull(hello["grant"]!["expiresAt"]);
        Assert.Equal(DirectState.Connected, connection.State);

        await TestSite.SendAsync(socket, Call(7, "ru"));
        var result = await TestSite.ReceiveAsync(socket);
        Assert.Equal("{\"type\":\"result\",\"id\":7,\"ok\":true,\"output\":{\"text\":\"francia-russian\"},\"receipt\":{\"cost\":\"0.000 USD\",\"provider\":\"local\"}}", result.ToJsonString());
        await TestSite.SendAsync(socket, Call(8, "sk"));
        Assert.Equal("francia-general", (await TestSite.ReceiveAsync(socket))["output"]!["text"]!.GetValue<string>());

        var request = _log.Recent().First(e => e.Kind == "request");
        Assert.Equal(("allowed", "link"), (request.Outcome, request.Via));
        Assert.All(_log.Recent().Where(e => e.Kind == "call"), e => Assert.Equal("link", e.Via));
        Assert.Contains(Links, e => e.Outcome == "connected");
        Assert.Null(_pairings.Get(_site.Origin));                                  // no "decide on my own": no pairing kept
    }

    [Fact]
    public async Task NothingReachesTheSiteBeforeAllow()
    {
        _ui.ConsentGate = new TaskCompletionSource<ConsentAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        var link = Transport().HandleLinkAsync(_site.Link());
        await WaitUntil(() => _ui.Consents.Count == 1);
        await Task.Delay(300);
        Assert.Equal(0, _site.Accepts);                                            // the consent is up, the site knows nothing
        _ui.ConsentGate.SetResult(new ConsentAnswer(true, 0.05m, false, []));
        Assert.NotNull(await link.WaitAsync(TimeSpan.FromSeconds(10)));
        await _site.NextSocketAsync();
        Assert.Equal(1, _site.Accepts);
    }

    [Fact]
    public async Task ALinkWithoutBrowserActivationIsHeldAsAQuietRequest()
    {
        Assert.Null(await Transport().HandleLinkAsync(_site.Link(), userActivation: false));
        Assert.Equal(0, _ui.Notices);
        Assert.Equal(1, _ui.Quiet);
        Assert.Equal(1, _engine.UnseenQuiet);
        Assert.Contains(_log.Recent(), entry => entry.Kind == "request" && entry.Note == "without a click: quiet badge only");
        Assert.Equal(0, _site.Accepts);
    }

    [Fact]
    public async Task AManifestThatIsNotAValidByoaManifestAsksNothingAndConnectsNowhere()
    {
        _site.Manifest = "{\"hello\":\"world\"}";
        Assert.Null(await Transport().HandleLinkAsync(_site.Link()));
        Assert.Equal(0, _ui.Notices);
        Assert.Equal(("invalid", "link"), (_log.Recent()[0].Outcome, _log.Recent()[0].Via));
        await Task.Delay(200);
        Assert.Equal(0, _site.Accepts);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AWelcomeWhoseManifestDiffersFromTheLinksEndsItAndKeepsNothingOfTheLink(bool missing)
    {
        _ui.Consent = new ConsentAnswer(true, 0.05m, AutoDecide: true, []);
        var connection = await Transport().HandleLinkAsync(_site.Link());
        var socket = await _site.NextSocketAsync();
        await TestSite.ReceiveAsync(socket);
        var welcome = _site.Welcome(manifest: Base64Url.EncodeToString(Encoding.UTF8.GetBytes(Manifest("0.04 USD"))));
        if (missing) welcome.Remove("manifest");
        await TestSite.SendAsync(socket, welcome);
        Assert.Equal("bye", (await TestSite.ReceiveAsync(socket))["type"]!.GetValue<string>());
        await connection!.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("the site's manifest differs from its link", Links.First(e => e.Outcome == "error").Note);
        Assert.Equal(SiteStatus.None, _engine.FindSite(_site.Origin)!.Status);   // the answer to the link was never stored
        Assert.DoesNotContain(_log.Recent(), e => e.Outcome == "revoked");
        Assert.Null(_pairings.Get(_site.Origin));
        Assert.Equal(0, _russian.Calls);
    }

    [Fact]
    public async Task ALinksAllowIsStoredOnlyOnceTheWelcomeEchoesTheLinksManifest()
    {
        _ui.Consent = new ConsentAnswer(true, 0.05m, AutoDecide: true, []);
        var connection = await Transport().HandleLinkAsync(_site.Link());
        var socket = await _site.NextSocketAsync();
        await TestSite.ReceiveAsync(socket);                                      // the hello
        Assert.Equal(SiteStatus.None, _engine.FindSite(_site.Origin)!.Status);    // held in memory: the site has not proved the link
        Assert.Null(_pairings.Get(_site.Origin));
        await TestSite.SendAsync(socket, _site.Welcome());
        Assert.True(await connection!.Welcomed.WaitAsync(TimeSpan.FromSeconds(10)));
        var site = _engine.FindSite(_site.Origin)!;
        Assert.Equal(SiteStatus.Allowed, site.Status);
        Assert.True(site.AutoDecide);
        Assert.Equal("Family geography game: recognise country names spoken aloud", site.Summary);   // the site's own words, now proven
        Assert.NotNull(_pairings.Get(_site.Origin));
    }

    [Fact]
    public async Task ALinksAllowIsDroppedWhenTheWelcomeNeverComes()
    {
        _ui.Consent = new ConsentAnswer(true, 0.05m, AutoDecide: true, []);
        var connection = await Transport().HandleLinkAsync(_site.Link());
        var socket = await _site.NextSocketAsync();
        await TestSite.ReceiveAsync(socket);                                      // the hello, then silence
        await connection!.Completion.WaitAsync(TimeSpan.FromSeconds(15));         // the welcome timeout (5 s in these tests)
        Assert.False(await connection.Welcomed);
        Assert.Equal(SiteStatus.None, _engine.FindSite(_site.Origin)!.Status);
        Assert.Null(_pairings.Get(_site.Origin));
        Assert.Equal("no welcome from the site (no answer in time)", Links.First(e => e.Outcome == "closed").Note);
    }

    [Fact]
    public async Task ALinkToASiteThatDecidesOnItsOwnStillAsksAndStoresNothingUntilTheWelcomeEchoesIt()
    {
        var (transport, _, _, _) = await PairAsync(auto: true, token: "pairing-token-AAAA-0123456789");   // allowed at $0.05, paired
        _ui.Consent = new ConsentAnswer(true, 0.10m, AutoDecide: true, []);
        var code = TestSite.NewCode();
        var connection = await transport.HandleLinkAsync(_site.Link(code));      // a new link asks, though the grant covers it
        Assert.NotNull(connection);
        Assert.Equal(2, _ui.Notices);
        Assert.Equal(2, _ui.Consents.Count);
        Assert.False(_ui.Consents[^1].IsDifference);                              // what is already allowed, as for any link
        var socket = await SocketOfAsync(code);

        Assert.Equal(Money.ToSpec(0.05m), _engine.FindSite(_site.Origin)!.Budget);   // the new Allow waits for the welcome
        Assert.Equal("pairing-token-AAAA-0123456789", _pairings.Get(_site.Origin)!.Token);
        await TestSite.SendAsync(socket, _site.Welcome("pairing-token-BBBB-0123456789"));
        Assert.True(await connection!.Welcomed.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Equal(Money.ToSpec(0.10m), _engine.FindSite(_site.Origin)!.Budget);   // stored by the welcome's echo
        Assert.Equal("pairing-token-BBBB-0123456789", _pairings.Get(_site.Origin)!.Token);   // and the new pairing replaces the stored one
        await TestSite.SendAsync(socket, Call(3));
        Assert.True((await TestSite.ReceiveAsync(socket))["ok"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ADeclinedLinkToASiteThatDecidesOnItsOwnLeavesItsGrantAndPairingAlone()
    {
        var (transport, paired, pairedSocket, _) = await PairAsync(auto: true, token: "pairing-token-AAAA-0123456789");
        _ui.Consent = ConsentAnswer.Declined;
        Assert.Null(await transport.HandleLinkAsync(_site.Link()));               // "not now": the visitor is asked and says no
        Assert.Equal(2, _ui.Consents.Count);
        var request = _log.Recent().First(e => e.Kind == "request");
        Assert.Equal(("declined", "declined from a link: nothing stored"), (request.Outcome, request.Note));

        Assert.Equal(SiteStatus.Allowed, _engine.FindSite(_site.Origin)!.Status);
        Assert.Equal(Money.ToSpec(0.05m), _engine.FindSite(_site.Origin)!.Budget);
        Assert.Equal("pairing-token-AAAA-0123456789", _pairings.Get(_site.Origin)!.Token);
        Assert.Equal(DirectState.Connected, paired.State);
        await TestSite.SendAsync(pairedSocket, Call(5));
        Assert.True((await TestSite.ReceiveAsync(pairedSocket))["ok"]!.GetValue<bool>());   // the grant still serves calls
        await Task.Delay(300);
        Assert.Equal(1, _site.Upgrades);                                          // no connection was made for the link
    }

    [Fact]
    public async Task ALinkWithoutAClickToASiteThatDecidesOnItsOwnIsHeldQuietly()
    {
        var (transport, _, _, _) = await PairAsync(auto: true);
        Assert.Null(await transport.HandleLinkAsync(_site.Link(), userActivation: false));   // no click: the quiet rule, as for any link
        Assert.Equal(1, _ui.Notices);
        Assert.Equal(1, _ui.Quiet);
        await Task.Delay(300);
        Assert.Equal(1, _site.Upgrades);
    }

    [Fact]
    public async Task ASecondLinkWhileTheFirstIsUnconfirmedAsksAgainAndOnlyTheSecondIsKept()
    {
        _ui.Consent = new ConsentAnswer(true, 0.05m, AutoDecide: false, []);
        var transport = Transport();
        var first = await transport.HandleLinkAsync(_site.Link());
        var firstSocket = await _site.NextSocketAsync();
        await TestSite.ReceiveAsync(firstSocket);                                 // the first hello: no welcome yet
        Assert.Equal(SiteStatus.None, _engine.FindSite(_site.Origin)!.Status);

        _ui.Consent = new ConsentAnswer(true, 0.10m, AutoDecide: false, []);
        var second = await transport.HandleLinkAsync(_site.Link());               // nothing is stored yet: the visitor is asked again
        Assert.Equal(2, _ui.Notices);
        Assert.False(_ui.Consents[^1].IsDifference);
        var secondSocket = await _site.NextSocketAsync();
        await TestSite.ReceiveAsync(secondSocket);
        await TestSite.SendAsync(secondSocket, _site.Welcome());
        Assert.True(await second!.Welcomed.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(Money.ToSpec(0.10m), _engine.FindSite(_site.Origin)!.Budget);

        // The second Allow replaced the first's answer: the first link ends at once, not at its own welcome timeout (5 s here).
        await first!.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("(close)", (await TestSite.ReceiveAsync(firstSocket))["type"]!.GetValue<string>());
        Assert.False(await first.Welcomed);
        Assert.Equal(Money.ToSpec(0.10m), _engine.FindSite(_site.Origin)!.Budget);   // only the second is kept
        await TestSite.SendAsync(secondSocket, Call(1));
        Assert.True((await TestSite.ReceiveAsync(secondSocket))["ok"]!.GetValue<bool>());   // the second grant still works
    }

    /// <summary>The site's socket of the link with this session code: the sockets of other links that come first are skipped.</summary>
    async Task<WebSocket> SocketOfAsync(string code)
    {
        while (true)
        {
            var socket = await _site.NextSocketAsync();
            if ((await TestSite.ReceiveAsync(socket))["session"]?.GetValue<string>() == code) return socket;
        }
    }

    [Fact]
    public async Task AHostileLinkFirstDoesNotLendItsWordingToTheRealLinkThatFollows()
    {
        var hostile = Clips.GeoManifest.Replace("Family geography game: recognise country names spoken aloud", "Win a prize: confirm your details");
        var gate = new TaskCompletionSource<ConsentAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ui.ConsentGate = gate;
        var transport = Transport();
        var hostileLink = transport.HandleLinkAsync(LinkWith(hostile));           // the same site, with its words
        await WaitUntil(() => _ui.Consents.Count == 1);                           // the hostile link's question is open
        _ui.ConsentGate = null;
        _ui.Consent = new ConsentAnswer(true, 0.05m, AutoDecide: false, []);
        var realCode = TestSite.NewCode();
        var realLink = transport.HandleLinkAsync(_site.Link(realCode));           // the real link follows: its own question
        await Task.Delay(200);
        Assert.Equal(1, _ui.Notices);
        gate.SetResult(new ConsentAnswer(true, 0.05m, AutoDecide: false, []));    // the visitor allows the hostile link's window
        var hostileConnection = await hostileLink;
        var real = await realLink;
        Assert.Equal(["Win a prize: confirm your details", "Family geography game: recognise country names spoken aloud"], _ui.Consents.Select(c => c.Summary));

        var socket = await SocketOfAsync(realCode);
        await TestSite.SendAsync(socket, _site.Welcome());                        // the site echoes the real link
        Assert.True(await real!.Welcomed.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(SiteStatus.Allowed, _engine.FindSite(_site.Origin)!.Status);
        Assert.Equal("Family geography game: recognise country names spoken aloud", _engine.FindSite(_site.Origin)!.Summary);

        // The real answer replaced the hostile one (a newer decision): the hostile link ended without a welcome.
        await hostileConnection!.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(await hostileConnection.Welcomed);
        Assert.Equal("Family geography game: recognise country names spoken aloud", _engine.FindSite(_site.Origin)!.Summary);   // the hostile wording was never stored
    }

    [Fact]
    public async Task AFailingSecondLinkLeavesTheFirstLinksStoredGrantAlone()
    {
        // The first link is welcomed first: its answer is stored, and its connection and grant live on. (A first link that
        // still waits for its welcome ends at the second link's Allow instead: ALinksNewerAllowEndsTheOlderLinksAnswer….)
        _ui.Consent = new ConsentAnswer(true, 0.05m, AutoDecide: false, []);
        var transport = Transport();
        var first = await transport.HandleLinkAsync(_site.Link());
        var firstSocket = await _site.NextSocketAsync();
        await TestSite.ReceiveAsync(firstSocket);
        await TestSite.SendAsync(firstSocket, _site.Welcome());
        Assert.True(await first!.Welcomed.WaitAsync(TimeSpan.FromSeconds(10)));

        // A second link for the same site fails: its own grant ends with it, and the first link's grant stays.
        var secondCode = TestSite.NewCode();
        var second = await transport.HandleLinkAsync(_site.Link(secondCode));     // allowed in its own question
        Assert.Equal(2, _ui.Notices);
        var secondSocket = await SocketOfAsync(secondCode);
        await TestSite.SendAsync(secondSocket, new JsonObject { ["type"] = "error", ["code"] = "bad-session" });   // the second link fails
        await second!.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        await TestSite.SendAsync(firstSocket, Call(1));
        Assert.True((await TestSite.ReceiveAsync(firstSocket))["ok"]!.GetValue<bool>());   // the first link's grant still works
        Assert.Equal(SiteStatus.Allowed, _engine.FindSite(_site.Origin)!.Status);
    }

    [Fact]
    public async Task APageRequestNeverTakesALinksAnswerThatIsStillWaiting()
    {
        var gate = new TaskCompletionSource<ConsentAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ui.ConsentGate = gate;
        var transport = Transport();
        var link = transport.HandleLinkAsync(_site.Link());
        await WaitUntil(() => _ui.Consents.Count == 1);                           // the link's question is open
        _ui.ConsentGate = null;
        _ui.Notice = NoticeAnswer.Closed;                                         // the page's own notice: the visitor closes it
        var page = _engine.HandleAsync(Clips.Request(_site.Origin, click: true, _site.Manifest));
        await Task.Delay(200);
        Assert.Equal(1, _ui.Notices);                                             // the page does not join the link's question
        gate.SetResult(new ConsentAnswer(true, 0.05m, AutoDecide: false, []));
        Assert.Null(await page.WaitAsync(TimeSpan.FromSeconds(10)));              // the page gets no answer of the link's
        Assert.Equal(2, _ui.Notices);

        var connection = await link;
        var socket = await _site.NextSocketAsync();
        await TestSite.ReceiveAsync(socket);
        await TestSite.SendAsync(socket, _site.Welcome());
        Assert.True(await connection!.Welcomed.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(SiteStatus.Allowed, _engine.FindSite(_site.Origin)!.Status);   // the link's answer was kept
    }

    [Fact]
    public async Task ALinkNeverTakesAPagesQuestionAndItsFailureLeavesThePagesGrantAlone()
    {
        var gate = new TaskCompletionSource<ConsentAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ui.ConsentGate = gate;
        var page = _engine.HandleAsync(Clips.Request(_site.Origin, click: true, _site.Manifest));
        await WaitUntil(() => _ui.Consents.Count == 1);                           // the page's question is open
        _ui.ConsentGate = null;
        var linkCode = TestSite.NewCode();
        var link = Transport().HandleLinkAsync(_site.Link(linkCode));             // the same site, while the page's question is open
        await Task.Delay(200);
        gate.SetResult(new ConsentAnswer(true, 0.05m, AutoDecide: false, []));
        var grant = (await page.WaitAsync(TimeSpan.FromSeconds(10)))!["grantId"]!.GetValue<string>();
        var connection = await link;                                              // the link asked on its own, after the page's answer
        Assert.Equal(2, _ui.Notices);

        var socket = await _site.NextSocketAsync();
        Assert.Equal(linkCode, (await TestSite.ReceiveAsync(socket))["session"]!.GetValue<string>());
        await TestSite.SendAsync(socket, new JsonObject { ["type"] = "error", ["code"] = "bad-session" });   // the link fails
        await connection!.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        var answer = await _engine.HandleAsync(Clips.Call(_site.Origin, grant));
        Assert.True(answer!["ok"]!.GetValue<bool>());                             // the page's grant still works
    }

    [Fact]
    public async Task AHostileLinkWithAManifestTheRealServerNeverIssuedStoresNothing()
    {
        var hostile = Clips.GeoManifest.Replace("Family geography game: recognise country names spoken aloud", "Win a prize: confirm your details");
        _ui.Consent = new ConsentAnswer(true, 0.05m, AutoDecide: true, []);
        var connection = await Transport().HandleLinkAsync(LinkWith(hostile));
        Assert.Equal("Win a prize: confirm your details", Assert.Single(_ui.Consents).Summary);   // the consent shows the link's words, as the link has them
        var socket = await _site.NextSocketAsync();
        await TestSite.ReceiveAsync(socket);
        await TestSite.SendAsync(socket, _site.Welcome());                        // the real server answers with its own manifest
        Assert.Equal("bye", (await TestSite.ReceiveAsync(socket))["type"]!.GetValue<string>());
        await connection!.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        var site = _engine.FindSite(_site.Origin)!;
        Assert.Equal(SiteStatus.None, site.Status);
        Assert.Null(site.Summary);                                                // nothing of the hostile link is stored
        Assert.DoesNotContain(_log.Recent(), e => e.Outcome == "revoked");
        Assert.Null(_pairings.Get(_site.Origin));
        Assert.Equal(0, _russian.Calls);
    }

    [Fact]
    public async Task AMismatchedLinkDoesNotRevokeTheSitesStoredGrant()
    {
        await PairAsync(auto: true);                                              // a confirmed grant with "decide on my own"
        _ui.Consent = new ConsentAnswer(true, 0.60m, AutoDecide: true, []);
        var connection = await Transport().HandleLinkAsync(LinkWith(Manifest("0.60 USD")));   // asks for more: the difference
        var socket = await _site.NextSocketAsync();
        await TestSite.ReceiveAsync(socket);
        await TestSite.SendAsync(socket, _site.Welcome());                        // the site answers with its own manifest, not this link's
        Assert.Equal("bye", (await TestSite.ReceiveAsync(socket))["type"]!.GetValue<string>());
        await connection!.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        var site = _engine.FindSite(_site.Origin)!;
        Assert.Equal(SiteStatus.Allowed, site.Status);                            // the stored grant is intact
        Assert.Equal(Money.ToSpec(0.05m), site.Budget);
        Assert.Equal(_site.Manifest64, _pairings.Get(_site.Origin)!.Manifest);   // and so is the pairing
        Assert.DoesNotContain(_log.Recent(), e => e.Outcome == "revoked");
    }

    [Fact]
    public async Task APagesDeclineWhileALinksAnswerWaitsDropsThatAnswer()
    {
        _ui.Consent = new ConsentAnswer(true, 0.05m, AutoDecide: true, []);
        var connection = await Transport().HandleLinkAsync(_site.Link());
        var socket = await _site.NextSocketAsync();
        await TestSite.ReceiveAsync(socket);                                      // the hello, no welcome yet
        _ui.Notice = NoticeAnswer.Decline;
        Assert.Null(await _engine.HandleAsync(Clips.Request(_site.Origin, click: true, _site.Manifest)));   // the visitor's later "no"
        Assert.Equal("(close)", (await TestSite.ReceiveAsync(socket))["type"]!.GetValue<string>());
        await connection!.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(SiteStatus.Declined, _engine.FindSite(_site.Origin)!.Status);   // the later decision stands
        Assert.Null(_pairings.Get(_site.Origin));
        Assert.Equal("the visitor decided about the site in the wallet", Links.First(e => e.Outcome == "closed").Note);
    }

    [Fact]
    public async Task APagesAllowDropsALinksWaitingAnswerButNotThePairedSitesConnectionOrPairing()
    {
        var (_, paired, pairedSocket, _) = await PairAsync(auto: true);          // a paired site with a live connection
        _ui.Consent = new ConsentAnswer(true, 0.60m, AutoDecide: true, []);
        var link = await Transport().HandleLinkAsync(LinkWith(Manifest("0.60 USD")));   // a link for it asks for more: its answer waits
        var linkSocket = await _site.NextSocketAsync();
        await TestSite.ReceiveAsync(linkSocket);                                  // the hello, no welcome yet
        Assert.NotNull(await _engine.HandleAsync(Clips.Request(_site.Origin, click: true, Manifest("0.60 USD"))));   // the visitor allows it in the page's window
        Assert.Equal("(close)", (await TestSite.ReceiveAsync(linkSocket))["type"]!.GetValue<string>());   // the link's answer ends with its connection
        await link!.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(_pairings.Get(_site.Origin));                              // the paired site keeps its pairing
        Assert.Equal(DirectState.Connected, paired.State);                        // and its connection
        await TestSite.SendAsync(pairedSocket, Call(5));
        Assert.True((await TestSite.ReceiveAsync(pairedSocket))["ok"]!.GetValue<bool>());
    }

    [Fact]
    public async Task SwitchingDecideOnMyOwnWhileALinksAnswerWaitsDropsThatAnswer()
    {
        _ui.Consent = new ConsentAnswer(true, 0.05m, AutoDecide: false, []);
        var connection = await Transport().HandleLinkAsync(_site.Link());
        var socket = await _site.NextSocketAsync();
        await TestSite.ReceiveAsync(socket);                                      // the hello, no welcome yet
        _engine.SetAutoDecide(_site.Origin, true);                                // the visitor switches "decide on my own" in Sites
        Assert.Equal("(close)", (await TestSite.ReceiveAsync(socket))["type"]!.GetValue<string>());
        await connection!.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(SiteStatus.None, _engine.FindSite(_site.Origin)!.Status);
        Assert.Null(_pairings.Get(_site.Origin));
    }

    [Fact]
    public async Task ARevokeWhileTheLinksAnswerIsHeldKeepsTheSiteUnallowed()
    {
        _ui.Consent = new ConsentAnswer(true, 0.05m, AutoDecide: true, []);
        var connection = await Transport().HandleLinkAsync(_site.Link());
        var socket = await _site.NextSocketAsync();
        await TestSite.ReceiveAsync(socket);                                      // the hello, no welcome yet
        _engine.Revoke(_site.Origin);                                             // the visitor revokes the site before the welcome
        Assert.Equal("(close)", (await TestSite.ReceiveAsync(socket))["type"]!.GetValue<string>());   // the wait is cancelled: the socket just closes
        await connection!.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(SiteStatus.None, _engine.FindSite(_site.Origin)!.Status);
        Assert.Null(_pairings.Get(_site.Origin));
    }

    [Fact]
    public async Task ABlockInSitesWhileTheLinksAnswerIsHeldKeepsTheSiteBlocked()
    {
        _ui.Consent = new ConsentAnswer(true, 0.05m, AutoDecide: true, []);
        var connection = await Transport().HandleLinkAsync(_site.Link());
        var socket = await _site.NextSocketAsync();
        await TestSite.ReceiveAsync(socket);                                      // the hello, no welcome yet
        _engine.Block(_site.Origin, true);                                        // the visitor blocks the site before the welcome
        Assert.Equal("(close)", (await TestSite.ReceiveAsync(socket))["type"]!.GetValue<string>());
        await connection!.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(SiteStatus.Blocked, _engine.FindSite(_site.Origin)!.Status);   // the held answer never turns the block into an Allow
        Assert.Null(_pairings.Get(_site.Origin));
    }

    [Fact]
    public async Task APauseInSitesWhileTheLinksAnswerIsHeldEndsThatAnswerAndKeepsTheSitePaused()
    {
        await PairAsync(auto: true);                                              // an allowed site at $0.05
        _ui.Consent = new ConsentAnswer(true, 0.60m, AutoDecide: true, []);
        var link = await Transport().HandleLinkAsync(LinkWith(Manifest("0.60 USD")));   // asks for more: its answer waits for the welcome
        var linkSocket = await _site.NextSocketAsync();
        await TestSite.ReceiveAsync(linkSocket);                                  // the hello, no welcome yet
        _engine.Pause(_site.Origin);                                              // the visitor pauses the site
        Assert.Equal("(close)", (await TestSite.ReceiveAsync(linkSocket))["type"]!.GetValue<string>());
        await link!.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        var site = _engine.FindSite(_site.Origin)!;
        Assert.True(site.Paused);
        Assert.Equal(Money.ToSpec(0.05m), site.Budget);                           // the held $0.60 was never stored
        Assert.Null(_pairings.Get(_site.Origin));                                 // and a pause forgets the pairing
    }

    [Fact]
    public async Task ALinksAllowThatSwitchesDecideOnMyOwnOffForgetsTheOlderPairing()
    {
        await PairAsync(auto: true);
        Assert.NotNull(_pairings.Get(_site.Origin));                              // the older pairing, kept for "decide on my own"
        _ui.Consent = new ConsentAnswer(true, 0.05m, AutoDecide: false, []);
        var link = await Transport().HandleLinkAsync(_site.Link());               // a link always asks; the visitor unticks the box
        var linkSocket = await _site.NextSocketAsync();
        await TestSite.ReceiveAsync(linkSocket);
        await TestSite.SendAsync(linkSocket, _site.Welcome());
        Assert.True(await link!.Welcomed.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.False(_engine.FindSite(_site.Origin)!.AutoDecide);
        Assert.Null(_pairings.Get(_site.Origin));                                 // nobody may use it any more: it goes at once
    }

    [Fact]
    public async Task AWarmUpThatFailsLeavesNoAnswerAndNoGrantHeld()
    {
        var engine = SpeechRoutes.Engine(new SettingsStore(null), [new WarmingSource("local-ru", languages: ["ru"]) { WarmTime = TimeSpan.FromSeconds(1.5) }, _general], _log, _ui, _time);
        _ui.Consent = new ConsentAnswer(true, 0.05m, AutoDecide: false, []);
        _ui.FailPreparing = true;                                                 // the window that shows "Preparing your model…" fails
        Assert.Null(await Transport(engine).HandleLinkAsync(_site.Link()));
        Assert.Equal(0, engine.ActiveSessions(_site.Origin));                     // nothing is held: no answer, no grant
        Assert.Equal(0, _site.Accepts);
    }

    [Fact]
    public async Task AnAnswerWaitingForTheModelWhenTheWalletClosesGoesNowhere()
    {
        var engine = SpeechRoutes.Engine(new SettingsStore(null), [new WarmingSource("local-ru", languages: ["ru"]) { WarmTime = TimeSpan.FromSeconds(1.5) }, _general], _log, _ui, _time);
        _ui.Consent = new ConsentAnswer(true, 0.05m, AutoDecide: false, []);
        var transport = Transport(engine);
        var link = transport.HandleLinkAsync(_site.Link());
        await WaitUntil(() => _ui.Preparing.Count == 1);                          // the model is still loading
        transport.Dispose();                                                      // the wallet closes meanwhile
        Assert.Null(await link.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, engine.ActiveSessions(_site.Origin));                     // the answer that came back has no connection to take it
        Assert.Equal(0, _site.Accepts);
    }

    [Fact]
    public async Task AnAnswerDroppedAfterItWasGivenGivesNoGrantAndNoCall()
    {
        _ui.Consent = new ConsentAnswer(true, 0.05m, AutoDecide: false, []);
        Assert.NotNull(await _engine.HandleAsync(Clips.Request(_site.Origin, click: true, _site.Manifest)));   // the site is allowed at $0.05
        _ui.Consent = new ConsentAnswer(true, 0.60m, AutoDecide: false, []);
        var answer = await _engine.RequestViaLinkAsync(_site.Origin, JsonNode.Parse(Manifest("0.60 USD"))!.AsObject(), userActivation: true);   // more: waits for the welcome
        var grant = answer!["grantId"]!.GetValue<string>();
        _engine.SetAutoDecide(_site.Origin, true);                                // the visitor switches "decide on my own" before the welcome
        Assert.False(_engine.ConfirmLinkDecision(grant));                         // no answer waits any more: nothing is stored
        Assert.Null(_engine.FindGrant(grant));
        Assert.False((await _engine.HandleAsync(Clips.Call(_site.Origin, grant)))!["ok"]!.GetValue<bool>());   // and no call is served on it
        Assert.Equal(Money.ToSpec(0.05m), _engine.FindSite(_site.Origin)!.Budget);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AVisitorsDecisionWhileTheModelLoadsLeavesTheLinkNoConnectionAndNoGrant(bool switchDecideOnMyOwn)
    {
        var engine = SpeechRoutes.Engine(new SettingsStore(null), [new WarmingSource("local-ru", languages: ["ru"]) { WarmTime = TimeSpan.FromSeconds(1.5) }, _general], _log, _ui, _time);
        _ui.Consent = new ConsentAnswer(true, 0.05m, AutoDecide: false, []);
        var link = Transport(engine).HandleLinkAsync(_site.Link());
        await WaitUntil(() => _ui.Preparing.Count == 1);                          // "Preparing your model…": the grant waits for the model
        if (switchDecideOnMyOwn)
        {
            engine.SetAutoDecide(_site.Origin, true);                             // the visitor switches "decide on my own" in Sites
        }
        else
        {
            _ui.Notice = NoticeAnswer.Decline;                                    // or declines the site on a page's own request
            Assert.Null(await engine.HandleAsync(Clips.Request(_site.Origin, click: true, _site.Manifest)));
        }

        Assert.Null(await link.WaitAsync(TimeSpan.FromSeconds(10)));              // the answer that waited for the model is gone
        Assert.Equal(0, _site.Accepts);
        Assert.Equal(0, engine.ActiveSessions(_site.Origin));
        Assert.NotEqual(SiteStatus.Allowed, engine.FindSite(_site.Origin)!.Status);
    }

    [Fact]
    public async Task ALinksNewerAllowEndsTheOlderLinksAnswerSoItsWelcomeStoresNothing()
    {
        _ui.Consent = new ConsentAnswer(true, 0.05m, AutoDecide: false, []);
        var older = await _engine.RequestViaLinkAsync(_site.Origin, JsonNode.Parse(Clips.GeoManifest)!.AsObject(), userActivation: true);
        _ui.Consent = new ConsentAnswer(true, 0.10m, AutoDecide: false, []);
        var newer = await _engine.RequestViaLinkAsync(_site.Origin, JsonNode.Parse(Manifest("0.10 USD"))!.AsObject(), userActivation: true);
        Assert.True(_engine.ConfirmLinkDecision(newer!["grantId"]!.GetValue<string>()));    // the newer link's welcome comes first
        Assert.False(_engine.ConfirmLinkDecision(older!["grantId"]!.GetValue<string>()));   // the older link's welcome stores nothing
        Assert.Equal(Money.ToSpec(0.10m), _engine.FindSite(_site.Origin)!.Budget);
    }

    [Fact]
    public async Task ADeclineOfAWindowOpenedBeforeALinksWelcomeStillEndsTheSite()
    {
        _ui.Consent = new ConsentAnswer(true, 0.05m, AutoDecide: false, []);
        var link = await Transport().HandleLinkAsync(_site.Link());               // the link's answer waits for the welcome
        var socket = await _site.NextSocketAsync();
        await TestSite.ReceiveAsync(socket);
        var gate = new TaskCompletionSource<ConsentAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ui.ConsentGate = gate;
        var page = _engine.HandleAsync(Clips.Request(_site.Origin, click: true, _site.Manifest));   // a page's window, open before the welcome
        await WaitUntil(() => _ui.Consents.Count == 2);
        await TestSite.SendAsync(socket, _site.Welcome());                        // the link's answer is stored behind the open window
        await WaitUntil(() => _engine.FindSite(_site.Origin)!.Status == SiteStatus.Allowed);
        gate.SetResult(ConsentAnswer.Declined);                                   // the visitor declines that window
        Assert.Null(await page.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(SiteStatus.Declined, _engine.FindSite(_site.Origin)!.Status);   // the later decision stands
        Assert.Equal("bye", (await TestSite.ReceiveAsync(socket))["type"]!.GetValue<string>());   // the site's connection ends
        await link!.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, _engine.ActiveSessions(_site.Origin));
    }

    [Fact]
    public async Task ABlockRevokeOrAutoDecideSwitchedWhileALinksWelcomeComesIsNeverUndone()
    {
        // Each round: a link's answer waits, then the visitor's decision and the site's confirmation race on two threads.
        // The store's lock puts one before the other; the visitor's decision must stand in either order.
        for (var round = 0; round < 150; round++)
        {
            var kind = round % 3;                                                 // 0: block, 1: revoke, 2: "decide on my own" off
            var engine = SpeechRoutes.Engine(new SettingsStore(null), [_russian, _general], _log, _ui, _time);
            if (kind == 2)
            {
                _ui.Consent = new ConsentAnswer(true, 0.05m, AutoDecide: true, []);
                Assert.NotNull(await engine.HandleAsync(Clips.Request(_site.Origin, click: true, _site.Manifest)));   // allowed, on its own
            }

            // The link asks for more than the site has (kind 2), or for the same: either way its answer waits for the welcome.
            var linkManifest = JsonNode.Parse(kind == 2 ? Manifest("0.60 USD") : Clips.GeoManifest)!.AsObject();
            _ui.Consent = new ConsentAnswer(true, kind == 2 ? 0.60m : 0.05m, AutoDecide: true, []);
            var answer = await engine.RequestViaLinkAsync(_site.Origin, linkManifest, userActivation: true);
            var grant = answer!["grantId"]!.GetValue<string>();

            using var start = new Barrier(2);
            var confirm = Task.Run(() =>
            {
                start.SignalAndWait();
                engine.ConfirmLinkDecision(grant);
            });
            var visitor = Task.Run(() =>
            {
                start.SignalAndWait();
                switch (kind)
                {
                    case 0: engine.Block(_site.Origin, true); break;
                    case 1: engine.Revoke(_site.Origin); break;
                    default: engine.SetAutoDecide(_site.Origin, false); break;
                }
            });
            await Task.WhenAll(confirm, visitor).WaitAsync(TimeSpan.FromSeconds(10));

            var site = engine.FindSite(_site.Origin)!;
            switch (kind)
            {
                case 0: Assert.True(site.Status == SiteStatus.Blocked, $"round {round}: {site.Status}"); break;
                case 1: Assert.True(site.Status == SiteStatus.None, $"round {round}: {site.Status}"); break;
                default: Assert.False(site.AutoDecide, $"round {round}"); break;
            }
        }
    }

    [Fact]
    public async Task CallsMeetTheSameGateLimitsAndPaidQuestionAsExtensionCalls()
    {
        var (_, _, socket, _) = await PairAsync(auto: false);
        await TestSite.SendAsync(socket, Call(1, "ru", seconds: 20));               // over the manifest's 15 s
        Assert.Equal("{\"type\":\"result\",\"id\":1,\"ok\":false}", (await TestSite.ReceiveAsync(socket)).ToJsonString());
        Assert.Equal(("limit", "link"), (_log.Recent()[0].Outcome, _log.Recent()[0].Via));

        _russian.Fail = true;
        _general.Fail = true;
        _ui.Paid = PaidAnswer.No;
        await TestSite.SendAsync(socket, Call(2, "ru"));
        Assert.Equal("{\"type\":\"result\",\"id\":2,\"ok\":false}", (await TestSite.ReceiveAsync(socket)).ToJsonString());
        Assert.Equal(1, _ui.PaidQuestions);                                         // asked before paying, said no
        Assert.Equal(0, _paid.Calls);
    }

    [Fact]
    public async Task AtMostAsManyCallsRunAtOnceAsTheSitesLimitAllows()
    {
        var (_, _, socket, _) = await PairAsync(auto: false);
        _store.Update(s => s.Site(_site.Origin)!.ParallelCalls = 4);              // the visitor's limit for this site
        _russian.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        for (var i = 1; i <= 5; i++) await TestSite.SendAsync(socket, Call(i));
        var refused = await TestSite.ReceiveAsync(socket);                          // the fifth, at once
        Assert.Equal("{\"type\":\"result\",\"id\":5,\"ok\":false}", refused.ToJsonString());
        _russian.Hold.SetResult();
        var ids = new List<int>();
        for (var i = 0; i < 4; i++)
        {
            var r = await TestSite.ReceiveAsync(socket);
            Assert.True(r["ok"]!.GetValue<bool>());
            ids.Add(r["id"]!.GetValue<int>());
        }

        Assert.Equal([1, 2, 3, 4], ids.Order());
    }

    [Fact]
    public async Task UnknownFramesAndCallsWithoutAnIdAreIgnored()
    {
        var (_, _, socket, _) = await PairAsync(auto: false);
        await TestSite.SendAsync(socket, new JsonObject { ["type"] = "something-new" });
        var noId = Call(1);
        noId.Remove("id");
        await TestSite.SendAsync(socket, noId);
        await TestSite.SendAsync(socket, Call(2));
        Assert.Equal(2, (await TestSite.ReceiveAsync(socket))["id"]!.GetValue<int>());
    }

    [Fact]
    public async Task TheWalletPingsAndTheSiteMayIgnoreIt()
    {
        _ui.Consent = new ConsentAnswer(true, 0.05m, AutoDecide: false, []);
        var transport = Transport(pingSeconds: 0.2);
        await transport.HandleLinkAsync(_site.Link());
        var socket = await _site.NextSocketAsync();
        await TestSite.ReceiveAsync(socket);
        await TestSite.SendAsync(socket, _site.Welcome());
        Assert.Equal("ping", (await TestSite.ReceiveAsync(socket, skipPings: false))["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task WithDecideOnMyOwnThePairingIsKeptAndTheWalletReconnectsWithBackoff()
    {
        var (_, connection, socket, _) = await PairAsync(auto: true, token: "pairing-token-AAAA-0123456789");
        var stored = _pairings.Get(_site.Origin)!;
        Assert.Equal("pairing-token-AAAA-0123456789", stored.Token);
        Assert.Equal(_site.Manifest64, stored.Manifest);                          // kept with the link's manifest

        socket.Abort();                                                            // the site's server restarts
        var again = await _site.NextSocketAsync();
        var hello = await TestSite.ReceiveAsync(again);
        Assert.Null(hello["session"]);                                             // no new link: the stored pairing
        Assert.Equal("pairing-token-AAAA-0123456789", hello["pairing"]!.GetValue<string>());
        await TestSite.SendAsync(again, _site.Welcome("pairing-token-AAAA-0123456789"));
        await WaitUntil(() => connection.State == DirectState.Connected);
        await TestSite.SendAsync(again, Call(3));
        Assert.True((await TestSite.ReceiveAsync(again))["ok"]!.GetValue<bool>());
        Assert.Contains(Links, e => e.Note == "reconnected with the stored pairing");
    }

    [Fact]
    public async Task OnAReconnectASiteThatNowAsksForMoreGetsByeAndNeedsANewLink()
    {
        var (_, connection, socket, _) = await PairAsync(auto: true);
        socket.Abort();
        var again = await _site.NextSocketAsync();
        await TestSite.ReceiveAsync(again);
        _site.Manifest = Manifest("0.60 USD");                                     // above the visitor's $0.05
        await TestSite.SendAsync(again, _site.Welcome());
        Assert.Equal("bye", (await TestSite.ReceiveAsync(again))["type"]!.GetValue<string>());
        await connection.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(_pairings.Get(_site.Origin));
        Assert.StartsWith("the site now asks for more", Links.First(e => e.Outcome == "closed").Note);
        await Task.Delay(300);
        Assert.Equal(2, _site.Upgrades);                                           // and no more reconnects

        // A new link with that manifest shows only the difference.
        _ui.Consent = null;
        Assert.NotNull(await Transport().HandleLinkAsync(_site.Link()));
        var diff = _ui.Consents[^1];
        Assert.True(diff.IsDifference);
        Assert.Equal(0.60m, diff.Budget);
    }

    [Fact]
    public async Task OnAReconnectAChangedManifestWithinTheGrantIsTakenSilently()
    {
        var (_, connection, socket, _) = await PairAsync(auto: true);
        socket.Abort();
        var again = await _site.NextSocketAsync();
        await TestSite.ReceiveAsync(again);
        _site.Manifest = Manifest("0.03 USD");                                     // cheaper: still within the grant
        await TestSite.SendAsync(again, _site.Welcome());
        await WaitUntil(() => connection.State == DirectState.Connected);
        await TestSite.SendAsync(again, Call(9));
        Assert.True((await TestSite.ReceiveAsync(again))["ok"]!.GetValue<bool>());
        Assert.Equal(_site.Manifest64, _pairings.Get(_site.Origin)!.Manifest);
        Assert.Equal(1, _ui.Notices);
    }

    [Fact]
    public async Task WithoutAStoredPairingADroppedConnectionStaysClosed()
    {
        var (_, connection, socket, _) = await PairAsync(auto: false);
        socket.Abort();
        await connection.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(300);
        Assert.Equal(1, _site.Upgrades);
        Assert.Equal(DirectState.Closed, connection.State);
    }

    [Fact]
    public async Task AFirstConnectThatFailsTriesAgainWhileTheCodeMayStillBeValid()
    {
        using var down = new TestSite(listen: false);                              // the site's server is not up yet
        _ui.Consent = new ConsentAnswer(true, 0.05m, AutoDecide: false, []);
        var code = TestSite.NewCode();
        var connection = await Transport().HandleLinkAsync(down.Link(code));
        Assert.NotNull(connection);
        await WaitUntil(() => Links.Any(e => e.Outcome == "failed"), 15);
        Assert.EndsWith("trying again", Links.First(e => e.Outcome == "failed").Note);
        down.Listen();
        var socket = await down.NextSocketAsync(20);
        Assert.Equal(code, (await TestSite.ReceiveAsync(socket))["session"]!.GetValue<string>());   // the same code
        await TestSite.SendAsync(socket, down.Welcome());
        Assert.True(await connection!.Welcomed.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task AFirstConnectGivesUpWhenTheCodeCanNoLongerBeValid()
    {
        using var down = new TestSite(listen: false);
        _ui.Consent = new ConsentAnswer(true, 0.05m, AutoDecide: false, []);
        var connection = await Transport(sessionWindowSeconds: 1).HandleLinkAsync(down.Link());
        await connection!.Completion.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal("the site could not be reached within 1 s of the link", Links[0].Note);
        Assert.False(await connection.Welcomed);
    }

    [Fact]
    public async Task AWalletRestartReconnectsWithTheStoredPairingWithoutANotice()
    {
        var (first, _, socket, _) = await PairAsync(auto: true);
        first.Dispose();                                                            // the wallet quits: bye
        Assert.Equal("bye", (await TestSite.ReceiveAsync(socket))["type"]!.GetValue<string>());
        Assert.Equal("(close)", (await TestSite.ReceiveAsync(socket))["type"]!.GetValue<string>());
        var notices = _ui.Notices;

        // A new wallet process: a new engine (no grants in memory), the same settings, activity and pairings.
        var engine = SpeechRoutes.Engine(_store, [_russian, _general, _paid], _log, _ui, _time);
        var transport = Transport(engine);
        transport.ResumePairings();
        var again = await _site.NextSocketAsync();
        var hello = await TestSite.ReceiveAsync(again);
        Assert.Equal("pairing-token-0123456789abcdef", hello["pairing"]!.GetValue<string>());
        Assert.Equal(notices, _ui.Notices);                                        // silent: "decide on my own"
        Assert.Equal(2, _site.Accepts);                                            // only the two WebSocket connections
        var request = _log.Recent().First(e => e.Kind == "request");
        Assert.Equal(("auto", "link"), (request.Outcome, request.Via));
    }

    [Fact]
    public async Task AReconnectThatRenewsAGrantEndedAfterTwelveHoursStaysSilent()
    {
        var (_, connection, socket, _) = await PairAsync(auto: true, token: "pairing-token-AAAA-0123456789");
        _time.Now = _time.Now.AddHours(13);                                       // the 12-hour grant has ended; the pairing stays
        socket.Abort();
        var again = await _site.NextSocketAsync();
        var hello = await TestSite.ReceiveAsync(again);
        Assert.Null(hello["session"]);                                            // no new link
        Assert.Equal("pairing-token-AAAA-0123456789", hello["pairing"]!.GetValue<string>());
        await TestSite.SendAsync(again, _site.Welcome("pairing-token-AAAA-0123456789"));
        await WaitUntil(() => connection.State == DirectState.Connected);
        Assert.Equal(1, _ui.Notices);                                             // renewed silently: no notice, no consent
        Assert.Single(_ui.Consents);
        var request = _log.Recent().First(e => e.Kind == "request");
        Assert.Equal(("auto", "link"), (request.Outcome, request.Via));
    }

    [Fact]
    public async Task APageRequestToASiteThatDecidesOnItsOwnStaysSilent()
    {
        await PairAsync(auto: true);
        var answer = await _engine.HandleAsync(Clips.Request(_site.Origin, click: true, _site.Manifest));
        Assert.NotNull(answer!["grantId"]);                                       // the extension's request is granted on its own
        Assert.Equal(1, _ui.Notices);
        var request = _log.Recent().First(e => e.Kind == "request");
        Assert.Equal("auto", request.Outcome);
        Assert.Null(request.Via);
    }

    [Fact]
    public async Task ABadSessionIsAnErrorTheConnectionClosesAndNothingIsKept()
    {
        _ui.Consent = new ConsentAnswer(true, 0.05m, AutoDecide: true, []);
        var transport = Transport();
        var connection = await transport.HandleLinkAsync(_site.Link());
        var socket = await _site.NextSocketAsync();
        await TestSite.ReceiveAsync(socket);
        await TestSite.SendAsync(socket, new JsonObject { ["type"] = "error", ["code"] = "bad-session" });
        Assert.False(await connection!.Welcomed.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("(close)", (await TestSite.ReceiveAsync(socket))["type"]!.GetValue<string>());
        await connection.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("the site refused the connection: bad-session", Links.First(e => e.Outcome == "error").Note);
        Assert.Equal(SiteStatus.None, _engine.FindSite(_site.Origin)!.Status);   // the visitor's Allow was never stored
        Assert.Null(_pairings.Get(_site.Origin));
        await Task.Delay(300);
        Assert.Equal(1, _site.Upgrades);
    }

    [Theory]
    [InlineData("rate-limited")]
    [InlineData("busy")]
    public async Task ATryLaterErrorOnAResumeKeepsThePairingAndTheWalletTriesAgain(string code)
    {
        var (_, connection, socket, _) = await PairAsync(auto: true, token: "pairing-token-BBBB-0123456789");
        socket.Abort();                                                            // the site's server restarts
        var refused = await _site.NextSocketAsync();
        await TestSite.ReceiveAsync(refused);
        await TestSite.SendAsync(refused, new JsonObject { ["type"] = "error", ["code"] = code });   // many wallets came back at once
        Assert.NotNull(_pairings.Get(_site.Origin));                              // a retryable error does not cost the visitor the pairing
        var again = await _site.NextSocketAsync();                                 // the wallet tries again by its backoff
        var hello = await TestSite.ReceiveAsync(again);
        Assert.Equal("pairing-token-BBBB-0123456789", hello["pairing"]!.GetValue<string>());
        await TestSite.SendAsync(again, _site.Welcome("pairing-token-BBBB-0123456789"));
        await WaitUntil(() => connection.State == DirectState.Connected);
        Assert.Equal("the site asked the wallet to try again later: " + code, Links.First(e => e.Outcome == "error").Note);
        await TestSite.SendAsync(again, Call(4));
        Assert.True((await TestSite.ReceiveAsync(again))["ok"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ATryLaterErrorOnTheFirstConnectionKeepsTheCodeAndTheWalletTriesAgainWithinTheWindow()
    {
        _ui.Consent = new ConsentAnswer(true, 0.05m, AutoDecide: false, []);
        var code = TestSite.NewCode();
        var connection = await Transport().HandleLinkAsync(_site.Link(code));
        var first = await _site.NextSocketAsync();
        await TestSite.ReceiveAsync(first);
        await TestSite.SendAsync(first, new JsonObject { ["type"] = "error", ["code"] = "rate-limited" });
        var second = await _site.NextSocketAsync();                                // the same code, a moment later
        Assert.Equal(code, (await TestSite.ReceiveAsync(second))["session"]!.GetValue<string>());
        await TestSite.SendAsync(second, _site.Welcome());
        Assert.True(await connection!.Welcomed.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(SiteStatus.Allowed, _engine.FindSite(_site.Origin)!.Status);
    }

    [Fact]
    public async Task ByeFromTheSiteEndsTheConnectionEvenWithAPairing()
    {
        var (_, connection, socket, _) = await PairAsync(auto: true);
        await TestSite.SendAsync(socket, new JsonObject { ["type"] = "bye" });
        await connection.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(300);
        Assert.Equal(1, _site.Upgrades);
        Assert.Contains(Links, e => e.Note == "the site said bye");
    }

    [Fact]
    public async Task RevokeSaysByeClosesAndForgetsThePairing()
    {
        var (_, connection, socket, _) = await PairAsync(auto: true);
        Assert.NotNull(_pairings.Get(_site.Origin));
        _engine.Revoke(_site.Origin);
        Assert.Equal("bye", (await TestSite.ReceiveAsync(socket))["type"]!.GetValue<string>());
        Assert.Equal("(close)", (await TestSite.ReceiveAsync(socket))["type"]!.GetValue<string>());
        await connection.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(_pairings.Get(_site.Origin));
        await Task.Delay(300);
        Assert.Equal(1, _site.Upgrades);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    public async Task AMalformedFrameClosesTheConnection(string text)
    {
        var (_, connection, socket, _) = await PairAsync(auto: true);
        await TestSite.SendTextAsync(socket, text);
        Assert.Equal("(close)", (await TestSite.ReceiveAsync(socket))["type"]!.GetValue<string>());
        await connection.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, _site.Upgrades);
    }

    [Fact]
    public async Task AFrameOverTheLimitClosesTheConnection()
    {
        _ui.Consent = new ConsentAnswer(true, 0.05m, AutoDecide: false, []);
        var transport = Transport(maxFrame: 1024);
        var connection = await transport.HandleLinkAsync(_site.Link());
        var socket = await _site.NextSocketAsync();
        await TestSite.ReceiveAsync(socket);
        await TestSite.SendAsync(socket, _site.Welcome());
        await TestSite.SendAsync(socket, new JsonObject { ["type"] = "call", ["id"] = 1, ["pad"] = new string('x', 2000) });
        var close = await TestSite.ReceiveAsync(socket);
        Assert.Equal("(close)", close["type"]!.GetValue<string>());
        Assert.Equal((int)WebSocketCloseStatus.MessageTooBig, close["status"]!.GetValue<int>());
        await connection!.Completion.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ASiteThatSpeaksBeforeItsWelcomeIsClosed()
    {
        _ui.Consent = new ConsentAnswer(true, 0.05m, AutoDecide: false, []);
        var connection = await Transport().HandleLinkAsync(_site.Link());
        var socket = await _site.NextSocketAsync();
        await TestSite.ReceiveAsync(socket);
        await TestSite.SendAsync(socket, Call(1));
        Assert.Equal("(close)", (await TestSite.ReceiveAsync(socket))["type"]!.GetValue<string>());
        Assert.False(await connection!.Welcomed.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, _russian.Calls);
    }

    [Fact]
    public async Task DeclineMeansNoContactWithTheSiteAtAll()
    {
        _ui.Notice = NoticeAnswer.Decline;
        Assert.Null(await Transport().HandleLinkAsync(_site.Link()));
        await Task.Delay(300);
        Assert.Equal(0, _site.Accepts);
        Assert.Equal(("declined", "link"), (_log.Recent()[0].Outcome, _log.Recent()[0].Via));
        Assert.Equal(SiteStatus.None, _engine.FindSite(_site.Origin)!.Status);   // "not now": nothing is stored
    }

    // ------------------------------------------------------------------------------------------ the connect check code (CONTRACT-DIRECT.md 3a)

    [Theory]
    [InlineData("a", "G3Y6")]
    [InlineData("session-1", "6FR5")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAA", "SD95")]
    [InlineData("Zm9v", "5ZT8")]
    [InlineData("k7MQ-_9", "4H5J")]
    public void TheCheckCodeIsTheOneTheSpecDefinesForASession(string session, string code)
    {
        Assert.Equal(code, ConnectCheck.Compute(session));              // the vectors were made by an independent script, not by this code
    }

    [Fact]
    public void TheCheckCodeIsFourCharactersWithoutTheConfusingOnes()
    {
        for (var i = 0; i < 200; i++)
        {
            var code = ConnectCheck.Compute(TestSite.NewCode());
            Assert.Equal(4, code.Length);
            Assert.All(code, c => Assert.Contains(c, "ABCDEFGHJKMNPQRSTUVWXYZ23456789"));
        }
    }

    JsonObject ManifestWithCheck()
    {
        var manifest = JsonNode.Parse(Clips.GeoManifest)!.AsObject();
        manifest["connect"] = new JsonObject { ["check"] = true };
        return manifest;
    }

    [Fact]
    public async Task ALinkFromASiteThatShowsTheCodeNeedsTheTickThatTheCodesAreTheSame()
    {
        _site.Manifest = ManifestWithCheck().ToJsonString();
        var session = TestSite.NewCode();
        _ui.Consent = new ConsentAnswer(true, 0.2m, false, [], CheckConfirmed: false);               // Allow pressed without the tick (a window that did not enforce it)

        Assert.Null(await Transport().HandleLinkAsync(_site.Link(session)));

        var model = Assert.Single(_ui.Consents);
        Assert.Equal(ConnectCheck.Compute(session), model.CheckCode);
        Assert.True(model.CheckRequired);
        await Task.Delay(300);
        Assert.Equal(0, _site.Accepts);                                                               // no tick, no contact with the site
        Assert.Equal(SiteStatus.None, _engine.FindSite(_site.Origin)!.Status);
    }

    [Fact]
    public async Task WithTheTickALinkFromASiteThatShowsTheCodeConnects()
    {
        _site.Manifest = ManifestWithCheck().ToJsonString();
        _ui.Consent = new ConsentAnswer(true, 0.2m, false, [], CheckConfirmed: true);

        var connection = await Transport().HandleLinkAsync(_site.Link());

        Assert.NotNull(connection);
        Assert.True(Assert.Single(_ui.Consents).CheckRequired);
    }

    [Fact]
    public async Task ALinkFromASiteThatDoesNotShowTheCodeStillGetsAWarningAndNeedsNoTick()
    {
        var session = TestSite.NewCode();

        var connection = await Transport().HandleLinkAsync(_site.Link(session));

        Assert.NotNull(connection);                                                                   // the default answer has no tick and is accepted
        var model = Assert.Single(_ui.Consents);
        Assert.Equal(ConnectCheck.Compute(session), model.CheckCode);                                 // the code is still shown, with a warning that nothing proves it
        Assert.False(model.CheckRequired);                                                            // a link cannot demand it for a site that never shows it
    }

    [Fact]
    public async Task APagesRequestThroughTheExtensionHasNoCheckCode()
    {
        _site.Manifest = ManifestWithCheck().ToJsonString();

        await _engine.HandleAsync(Clips.Request(_site.Origin, click: true, _site.Manifest));

        var model = Assert.Single(_ui.Consents);
        Assert.Null(model.CheckCode);                                                                 // the browser has tied the request to the page already
        Assert.False(model.CheckRequired);
        Assert.False(model.FromLink);
    }

    [Fact]
    public async Task ADeclineFromALinkIsNotStoredSoTheSitesOwnRequestIsAskedAgain()
    {
        _ui.Notice = NoticeAnswer.Decline;
        Assert.Null(await Transport().HandleLinkAsync(_site.Link()));
        _ui.Notice = NoticeAnswer.Closed;
        Assert.Null(await _engine.HandleAsync(Clips.Request(_site.Origin, click: true, _site.Manifest)));   // the site's own request
        Assert.Equal(2, _ui.Notices);                                              // the visitor is asked again
        Assert.Equal(SiteStatus.None, _engine.FindSite(_site.Origin)!.Status);
    }

    [Fact]
    public async Task ABlockFromALinkIsNotStoredSoALaterLinkAsksAgain()
    {
        _ui.Notice = NoticeAnswer.Block;
        var transport = Transport();
        Assert.Null(await transport.HandleLinkAsync(_site.Link()));
        Assert.Equal(SiteStatus.None, _engine.FindSite(_site.Origin)!.Status);   // a site can only prove a link by connecting: nothing is stored
        Assert.Null(await transport.HandleLinkAsync(_site.Link()));
        Assert.Equal(2, _ui.Notices);
        await Task.Delay(300);
        Assert.Equal(0, _site.Accepts);
    }

    [Fact]
    public async Task ALinksNoticeSaysItIsALinkAndItsBlockStoresNothingAndIsNotReportedAsOne()
    {
        _ui.Notice = NoticeAnswer.Block;
        Assert.Null(await Transport().HandleLinkAsync(_site.Link()));
        Assert.True(Assert.Single(_ui.NoticeModels).FromLink);
        var request = _log.Recent().First(e => e.Kind == "request");
        Assert.Equal(("declined", "declined from a link: nothing stored"), (request.Outcome, request.Note));   // not "blocked": nothing was blocked
        Assert.Equal(SiteStatus.None, _engine.FindSite(_site.Origin)!.Status);
    }

    [Fact]
    public async Task ABlockOnThePagesNoticeIsStoredAndSilencesLaterLinks()
    {
        _ui.Notice = NoticeAnswer.Block;
        Assert.Null(await _engine.HandleAsync(Clips.Request(_site.Origin, click: true, _site.Manifest)));
        Assert.Equal(SiteStatus.Blocked, _engine.FindSite(_site.Origin)!.Status);
        Assert.False(Assert.Single(_ui.NoticeModels).FromLink);
        Assert.Null(await Transport().HandleLinkAsync(_site.Link()));
        Assert.Equal(1, _ui.Notices);
        Assert.Equal("blocked site: the link was ignored", Links[0].Note);
    }

    [Fact]
    public async Task BadAndRepeatedLinksAreIgnoredWithAnActivityEntryOnly()
    {
        var transport = Transport();
        Assert.Null(await transport.HandleLinkAsync("bringyourownagent://connect?v=0.1&endpoint=wss%3A%2F%2F10.0.0.1%2Fws"));
        Assert.Equal("invalid", Links[0].Outcome);

        _ui.Notice = NoticeAnswer.Decline;
        var link = _site.Link();
        await transport.HandleLinkAsync(link);
        Assert.Null(await transport.HandleLinkAsync(link));                        // the same session code again
        Assert.Equal("ignored", Links[0].Outcome);
        Assert.Equal(1, _ui.Notices);
        Assert.Equal(0, _site.Accepts);
    }

    [Fact]
    public async Task AFloodOfLinksIsCut()
    {
        _ui.ConsentGate = new TaskCompletionSource<ConsentAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = Transport();
        var links = Enumerable.Range(0, 10).Select(_ => transport.HandleLinkAsync(_site.Link())).ToList();
        await WaitUntil(() => links.Count(t => t.IsCompleted) == 6);
        Assert.Equal(6, Links.Count(e => e.Note == "too many links at once"));
        _ui.ConsentGate.SetResult(ConsentAnswer.Declined);
        await Task.WhenAll(links).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(4, _ui.Notices);                                              // the four that were let in each asked in turn: a link never shares a question
    }

    [Fact]
    public async Task ANameThatResolvesToAPrivateAddressIsRefusedWhenTheWalletConnects()
    {
        var transport = Transport(resolve: (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.215.14"), IPAddress.Parse("10.0.0.5") }));
        var link = "bringyourownagent://connect?v=0.1&endpoint=" + Uri.EscapeDataString("wss://geo.example.org/byoa/ws")
            + "&manifest=" + _site.Manifest64 + "&session=" + TestSite.NewCode();
        var connection = await transport.HandleLinkAsync(link);
        Assert.Equal(1, _ui.Notices);                                              // asked first: no lookup before Allow
        await connection!.Completion.WaitAsync(TimeSpan.FromSeconds(10));         // a refusal is not retried
        Assert.Equal("refused: the site's name resolves to a private, loopback or link-local address", Links[0].Note);
    }

    [Fact]
    public async Task TheNativeHostForwardsALinkAndKeepsItsActivationBit()
    {
        var (browser, host) = HostRelayTests.Pair();
        var (hostSide, walletSide) = HostRelayTests.Pair();
        var connector = new HostRelayTests.Connector(_ => hostSide);
        var relay = Task.Run(() => new HostRelay(host, host, connector).RunAsync());
        await Framing.WriteAsync(browser, Encoding.UTF8.GetBytes("{\"id\":4,\"type\":\"link\",\"origin\":\"https://a.example\",\"userActivation\":false,\"payload\":{\"url\":\"bringyourownagent://connect\"}}"));
        var atWallet = (JsonObject)JsonNode.Parse((await Framing.ReadAsync(walletSide))!)!;
        Assert.False(atWallet["userActivation"]!.GetValue<bool>());
        Assert.Equal([true], connector.Starts);
        await Framing.WriteAsync(walletSide, Encoding.UTF8.GetBytes("{\"id\":4,\"result\":{\"accepted\":true}}"));
        Assert.True((await HostRelayTests.ReadAsync(browser))["result"]!["accepted"]!.GetValue<bool>());
        browser.Dispose();
        walletSide.Dispose();
        await relay.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
