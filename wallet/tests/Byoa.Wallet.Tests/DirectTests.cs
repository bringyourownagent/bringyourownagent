using System.Diagnostics;
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

/// <summary>bringyourownagent://connect links: parsing, and refusing hostile ones (CONTRACT-DIRECT.md §1).</summary>
public class DirectLinkTests
{
    const string Code = "s3ss10n-c0de_with-enough-bits-0123456789";

    static string Link(string endpoint, string manifest, string session = Code, string extra = "") =>
        "bringyourownagent://connect?v=0.1&endpoint=" + Uri.EscapeDataString(endpoint) + "&manifest=" + Uri.EscapeDataString(manifest) + "&session=" + session + extra;

    static string Good(string extra = "") => Link("wss://geo.example.org/byoa/ws", "https://geo.example.org/byoa/manifest.json", extra: extra);

    [Fact]
    public void AGoodLinkGivesTheSitesOriginEndpointManifestAndCode()
    {
        Assert.True(DirectLink.TryParse(Good(), devLocal: false, out var link, out var why), why);
        Assert.Equal("https://geo.example.org", link!.Origin);
        Assert.Equal("geo.example.org", link.Host);
        Assert.Equal("wss://geo.example.org/byoa/ws", link.Endpoint.ToString());
        Assert.Equal("https://geo.example.org/byoa/manifest.json", link.Manifest.ToString());
        Assert.Equal(Code, link.Session);
    }

    [Fact]
    public void APortIsPartOfTheOriginAndTrailingSlashesAndCaseAreTolerated()
    {
        var text = Link("wss://Geo.Example.org:8443/byoa/ws", "https://geo.example.org:8443/byoa/manifest.json").Replace("bringyourownagent://connect?", "BringYourOwnAgent://connect/?");
        Assert.True(DirectLink.TryParse(text, false, out var link, out var why), why);
        Assert.Equal("https://geo.example.org:8443", link!.Origin);
        Assert.Equal("geo.example.org:8443", link.Host);
        Assert.True(DirectLink.TryParse(Good("&future=1"), false, out _, out why), why);   // unknown extra parameters are ignored
    }

    public static TheoryData<string> Hostile() => new()
    {
        "",
        "byoa://connect?v=0.1&endpoint=wss%3A%2F%2Fgeo.example.org%2Fws&manifest=https%3A%2F%2Fgeo.example.org%2Fm&session=" + Code,
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
        Good() + new string('a', 5000),
        Link("ws://geo.example.org/ws", "https://geo.example.org/m"),                   // not wss
        Link("https://geo.example.org/ws", "https://geo.example.org/m"),
        Link("wss://geo.example.org/ws", "http://geo.example.org/m"),                   // manifest not https
        Link("wss://geo.example.org/ws", "https://other.example.org/m"),                // another host
        Link("wss://geo.example.org/ws", "https://geo.example.org:8443/m"),             // another port
        Link("wss://127.0.0.1/ws", "https://127.0.0.1/m"),
        Link("wss://[::1]/ws", "https://[::1]/m"),
        Link("wss://10.0.0.5/ws", "https://10.0.0.5/m"),
        Link("wss://2130706433/ws", "https://2130706433/m"),                            // 127.0.0.1 as one number
        Link("wss://0x7f.0.0.1/ws", "https://0x7f.0.0.1/m"),
        Link("wss://localhost/ws", "https://localhost/m"),
        Link("wss://app.localhost/ws", "https://app.localhost/m"),
        Link("wss://printer.local/ws", "https://printer.local/m"),
        Link("wss://nas.home.arpa/ws", "https://nas.home.arpa/m"),
        Link("wss://intranet/ws", "https://intranet/m"),
        Link("wss://geo.example.org./ws", "https://geo.example.org./m"),
        Link("wss://user:pass@geo.example.org/ws", "https://geo.example.org/m"),
        Link("wss://geo.example.org/ws#x", "https://geo.example.org/m"),
        Link("wss://evil.example.com/@geo.example.org", "https://geo.example.org/m"),
        Link("wss://geo.example.org\\@evil.example.com/ws", "https://geo.example.org/m"),
        Link("wss://geo.example.org/ws\r\nX: 1", "https://geo.example.org/m"),          // header injection
        Link("ws://localhost:5192/byoa/ws", "http://localhost:5192/byoa/manifest.json"), // localhost without the developer switch
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
        var local = Link("ws://localhost:5192/byoa/ws", "http://localhost:5192/byoa/manifest.json");
        Assert.True(DirectLink.TryParse(local, devLocal: true, out var link, out var why), why);
        Assert.Equal("http://localhost:5192", link!.Origin);                       // the same origin as an extension page there
        Assert.False(DirectLink.TryParse(Link("ws://127.0.0.1:5192/ws", "http://127.0.0.1:5192/m"), true, out _, out _));
        Assert.False(DirectLink.TryParse(Link("ws://geo.example.org/ws", "http://geo.example.org/m"), true, out _, out _));
        Assert.False(DirectLink.TryParse(Link("ws://localhost:5192/ws", "https://localhost:5192/m"), true, out _, out why));
        Assert.Contains("security", why);
    }

    [Theory]
    [InlineData("0123456789abcdefABCDEF", true)]
    [InlineData("0123456789abcdefABCDE", false)]
    [InlineData("0123456789abcdefABCD+F", false)]
    [InlineData("0123456789abcdef_ABC-EF", true)]
    public void ASessionCodeHasAtLeast128BitsOfUrlSafeCharacters(string code, bool ok) => Assert.Equal(ok, DirectLink.IsSessionCode(code));

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
    static Pairing P(string origin, string token) => new(origin, "wss://x.example.org/ws", "https://x.example.org/m", token, DateTimeOffset.UnixEpoch);

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

/// <summary>
/// A small site for the tests: an HTTP server on 127.0.0.1 that serves the manifest and accepts WebSocket upgrades
/// (the test drives each accepted socket itself).
/// </summary>
sealed class TestSite : IDisposable
{
    readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    readonly CancellationTokenSource _stop = new();
    readonly Channel<WebSocket> _sockets = Channel.CreateUnbounded<WebSocket>();
    int _manifestRequests;
    int _upgrades;

    public TestSite()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptAsync();
    }

    public int Port { get; }
    public string Origin => "http://localhost:" + Port;
    public string Manifest { get; set; } = Clips.GeoManifest;
    public string ManifestType { get; set; } = "application/json";
    public string? ManifestStatus { get; set; }
    public int ManifestRequests => Volatile.Read(ref _manifestRequests);
    public int Upgrades => Volatile.Read(ref _upgrades);
    public List<string> Heads { get; } = [];

    public string Link(string? session = null) =>
        "bringyourownagent://connect?v=0.1&endpoint=" + Uri.EscapeDataString($"ws://localhost:{Port}/byoa/ws")
        + "&manifest=" + Uri.EscapeDataString($"http://localhost:{Port}/byoa/manifest.json") + "&session=" + (session ?? NewCode());

    public static string NewCode() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).Replace('+', '-').Replace('/', '_');

    public async Task<WebSocket> NextSocketAsync(int seconds = 10) => await _sockets.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(seconds));

    public bool HasSocket => _sockets.Reader.TryPeek(out _);

    async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

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
        lock (Heads) Heads.Add(text);
        if (text.Contains("Upgrade: websocket", StringComparison.OrdinalIgnoreCase))
        {
            var key = text.Split("\r\n").First(l => l.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))[18..].Trim();
            var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n"));
            Interlocked.Increment(ref _upgrades);
            await _sockets.Writer.WriteAsync(WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = TimeSpan.Zero }));
            return;
        }

        Interlocked.Increment(ref _manifestRequests);
        var body = Encoding.UTF8.GetBytes(Manifest);
        var status = ManifestStatus ?? "200 OK";
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: {ManifestType}\r\nContent-Length: {body.Length}\r\nLocation: http://localhost:{Port}/elsewhere\r\nConnection: close\r\n\r\n"));
        await stream.WriteAsync(body);
        await stream.FlushAsync();
        client.Dispose();
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
/// The direct transport end to end inside the process: a link, the manifest, the consent, the wallet's own
/// connection (hello, welcome, calls and results, ping, bye), pairing, reconnect with backoff, revoke, and the
/// same gate, limits, routing and "ask before paid" as for the extension's calls.
/// </summary>
public class DirectTransportTests : IDisposable
{
    readonly ManualTime _time = new();
    readonly ScriptedUi _ui = new();
    readonly FakeSource _giga = new("gigaam-v3", languages: ["ru"]) { Answer = "francia-giga" };
    readonly FakeSource _whisper = new("whisper") { Answer = "francia-whisper" };
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
        _engine = new WalletEngine(_store, [_giga, _whisper, _paid], _log, _ui, _time);
    }

    DirectTransport Transport(WalletEngine? engine = null, int maxFrame = 4 * 1024 * 1024, double pingSeconds = 30, Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null)
    {
        var t = new DirectTransport(engine ?? _engine, _pairings, new DirectOptions
        {
            DevLocal = true,
            Backoff = [TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100)],
            PingInterval = TimeSpan.FromSeconds(pingSeconds),
            WelcomeTimeout = TimeSpan.FromSeconds(5),
            MaxFrameBytes = maxFrame,
            Resolve = resolve,
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
        await TestSite.SendAsync(socket, new JsonObject { ["type"] = "welcome", ["pairing"] = token });
        Assert.True(await connection!.Welcomed.WaitAsync(TimeSpan.FromSeconds(10)));
        return (transport, connection, socket, hello);
    }

    [Fact]
    public async Task ALinkAsksTheVisitorThenTheWalletSaysHelloAndAnswersCallsThroughTheEngine()
    {
        var (_, connection, socket, hello) = await PairAsync(auto: false);
        Assert.Equal(1, _ui.Notices);                                              // the same notice and consent
        Assert.Single(_ui.Consents);
        Assert.Equal(1, _site.ManifestRequests);                                   // the manifest, fetched before the consent
        Assert.Equal("hello", hello["type"]!.GetValue<string>());
        Assert.Equal("0.1", hello["v"]!.GetValue<string>());
        Assert.Null(hello["pairing"]);
        Assert.Equal("Agent wallet", hello["wallet"]!["name"]!.GetValue<string>());
        Assert.Equal("[\"audio.transcribe\"]", hello["grant"]!["skills"]!.ToJsonString());
        Assert.NotNull(hello["grant"]!["expiresAt"]);
        Assert.Equal(DirectState.Connected, connection.State);

        await TestSite.SendAsync(socket, Call(7, "ru"));
        var result = await TestSite.ReceiveAsync(socket);
        Assert.Equal("{\"type\":\"result\",\"id\":7,\"ok\":true,\"output\":{\"text\":\"francia-giga\"},\"receipt\":{\"cost\":\"0.000 USD\",\"provider\":\"local\"}}", result.ToJsonString());
        await TestSite.SendAsync(socket, Call(8, "sk"));
        Assert.Equal("francia-whisper", (await TestSite.ReceiveAsync(socket))["output"]!["text"]!.GetValue<string>());

        var request = _log.Recent().First(e => e.Kind == "request");
        Assert.Equal(("allowed", "link"), (request.Outcome, request.Via));
        Assert.All(_log.Recent().Where(e => e.Kind == "call"), e => Assert.Equal("link", e.Via));
        Assert.Contains(Links, e => e.Outcome == "connected");
        Assert.Null(_pairings.Get(_site.Origin));                                  // no "decide on my own": no pairing kept
        var head = _site.Heads.First(h => h.StartsWith("GET /byoa/manifest.json", StringComparison.Ordinal));
        Assert.DoesNotContain("Cookie", head, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(hello["session"]!.GetValue<string>(), head);         // no session code in the manifest request
    }

    [Fact]
    public async Task CallsMeetTheSameGateLimitsAndPaidQuestionAsExtensionCalls()
    {
        var (_, _, socket, _) = await PairAsync(auto: false);
        await TestSite.SendAsync(socket, Call(1, "ru", seconds: 20));               // over the manifest's 15 s
        Assert.Equal("{\"type\":\"result\",\"id\":1,\"ok\":false}", (await TestSite.ReceiveAsync(socket)).ToJsonString());
        Assert.Equal(("limit", "link"), (_log.Recent()[0].Outcome, _log.Recent()[0].Via));

        _giga.Fail = true;
        _whisper.Fail = true;
        _ui.Paid = PaidAnswer.No;
        await TestSite.SendAsync(socket, Call(2, "ru"));
        Assert.Equal("{\"type\":\"result\",\"id\":2,\"ok\":false}", (await TestSite.ReceiveAsync(socket)).ToJsonString());
        Assert.Equal(1, _ui.PaidQuestions);                                         // asked before paying, said no
        Assert.Equal(0, _paid.Calls);
    }

    [Fact]
    public async Task AtMostFourCallsRunAtOnceOnAConnection()
    {
        var (_, _, socket, _) = await PairAsync(auto: false);
        _giga.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        for (var i = 1; i <= 5; i++) await TestSite.SendAsync(socket, Call(i));
        var refused = await TestSite.ReceiveAsync(socket);                          // the fifth, at once
        Assert.Equal("{\"type\":\"result\",\"id\":5,\"ok\":false}", refused.ToJsonString());
        _giga.Hold.SetResult();
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
        await TestSite.SendAsync(socket, new JsonObject { ["type"] = "welcome", ["pairing"] = "pairing-token-0123456789abcdef" });
        Assert.Equal("ping", (await TestSite.ReceiveAsync(socket, skipPings: false))["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task WithDecideOnMyOwnThePairingIsKeptAndTheWalletReconnectsWithBackoff()
    {
        var (_, connection, socket, _) = await PairAsync(auto: true, token: "pairing-token-AAAA-0123456789");
        Assert.Equal("pairing-token-AAAA-0123456789", _pairings.Get(_site.Origin)!.Token);

        socket.Abort();                                                            // the site's server restarts
        var again = await _site.NextSocketAsync();
        var hello = await TestSite.ReceiveAsync(again);
        Assert.Null(hello["session"]);                                             // no new link: the stored pairing
        Assert.Equal("pairing-token-AAAA-0123456789", hello["pairing"]!.GetValue<string>());
        await TestSite.SendAsync(again, new JsonObject { ["type"] = "welcome", ["pairing"] = "pairing-token-AAAA-0123456789" });
        await WaitUntil(() => connection.State == DirectState.Connected);
        await TestSite.SendAsync(again, Call(3));
        Assert.True((await TestSite.ReceiveAsync(again))["ok"]!.GetValue<bool>());
        Assert.Contains(Links, e => e.Note == "reconnected with the stored pairing");
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
    public async Task AWalletRestartReconnectsWithTheStoredPairingWithoutANotice()
    {
        var (first, _, socket, _) = await PairAsync(auto: true);
        first.Dispose();                                                            // the wallet quits: bye
        Assert.Equal("bye", (await TestSite.ReceiveAsync(socket))["type"]!.GetValue<string>());
        Assert.Equal("(close)", (await TestSite.ReceiveAsync(socket))["type"]!.GetValue<string>());
        var notices = _ui.Notices;

        // A new wallet process: a new engine (no grants in memory), the same settings, activity and pairings.
        var engine = new WalletEngine(_store, [_giga, _whisper, _paid], _log, _ui, _time);
        var transport = Transport(engine);
        transport.ResumePairings();
        var again = await _site.NextSocketAsync();
        var hello = await TestSite.ReceiveAsync(again);
        Assert.Equal("pairing-token-0123456789abcdef", hello["pairing"]!.GetValue<string>());
        Assert.Equal(notices, _ui.Notices);                                        // silent: "decide on my own"
        Assert.Equal(2, _site.ManifestRequests);                                   // the manifest again, then the silent grant
        Assert.Equal(("auto", "link"), (_log.Recent().First(e => e.Kind == "request").Outcome, _log.Recent().First(e => e.Kind == "request").Via));
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
        Assert.Null(_pairings.Get(_site.Origin));
        await Task.Delay(300);
        Assert.Equal(1, _site.Upgrades);
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
        await TestSite.SendAsync(socket, new JsonObject { ["type"] = "welcome", ["pairing"] = "pairing-token-0123456789abcdef" });
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
        Assert.Equal(0, _giga.Calls);
    }

    [Fact]
    public async Task DeclineMeansNoConnectionAtAll()
    {
        _ui.Notice = NoticeAnswer.Decline;
        Assert.Null(await Transport().HandleLinkAsync(_site.Link()));
        await Task.Delay(300);
        Assert.Equal(0, _site.Upgrades);
        Assert.Equal(("declined", "link"), (_log.Recent()[0].Outcome, _log.Recent()[0].Via));
    }

    [Fact]
    public async Task BlockMakesLaterLinksDoNothing()
    {
        _ui.Notice = NoticeAnswer.Block;
        var transport = Transport();
        Assert.Null(await transport.HandleLinkAsync(_site.Link()));
        var requests = _site.ManifestRequests;
        Assert.Null(await transport.HandleLinkAsync(_site.Link()));
        Assert.Equal(requests, _site.ManifestRequests);                            // not even the manifest
        Assert.Equal(1, _ui.Notices);
        Assert.Equal(0, _site.Upgrades);
        Assert.Equal("blocked site: the link was ignored", Links[0].Note);
    }

    [Fact]
    public async Task BadAndRepeatedLinksAreIgnoredWithAnActivityEntryOnly()
    {
        var transport = Transport();
        Assert.Null(await transport.HandleLinkAsync("bringyourownagent://connect?v=0.1&endpoint=wss%3A%2F%2F10.0.0.1%2Fws"));
        Assert.Equal("invalid", Links[0].Outcome);
        Assert.Equal(0, _site.ManifestRequests);

        _ui.Notice = NoticeAnswer.Decline;
        var link = _site.Link();
        await transport.HandleLinkAsync(link);
        Assert.Null(await transport.HandleLinkAsync(link));                        // the same session code again
        Assert.Equal("ignored", Links[0].Outcome);
        Assert.Equal(1, _ui.Notices);
    }

    [Theory]
    [InlineData("text/html", null, "manifest: not application/json")]
    [InlineData("application/json", "302 Found", "manifest: HTTP 302")]
    [InlineData("application/json", "404 Not Found", "manifest: HTTP 404")]
    public async Task TheManifestMustBeJsonAndIsNotFollowedElsewhere(string type, string? status, string why)
    {
        _site.ManifestType = type;
        _site.ManifestStatus = status;
        Assert.Null(await Transport().HandleLinkAsync(_site.Link()));
        Assert.Equal(why, Links[0].Note);
        Assert.Equal(0, _ui.Notices);
        Assert.Equal(1, _site.ManifestRequests);                                   // the redirect was not followed
    }

    [Fact]
    public async Task AManifestOver64KbIsRefused()
    {
        _site.Manifest = "{\"x\":\"" + new string('a', 70 * 1024) + "\"}";
        Assert.Null(await Transport().HandleLinkAsync(_site.Link()));
        Assert.Equal("manifest: larger than 64 KB", Links[0].Note);
    }

    [Fact]
    public async Task ANameThatResolvesToAPrivateAddressIsRefusedBeforeAnyRequest()
    {
        var transport = Transport(resolve: (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.215.14"), IPAddress.Parse("10.0.0.5") }));
        var link = "bringyourownagent://connect?v=0.1&endpoint=" + Uri.EscapeDataString("wss://geo.example.org/byoa/ws")
            + "&manifest=" + Uri.EscapeDataString("https://geo.example.org/byoa/manifest.json") + "&session=" + TestSite.NewCode();
        Assert.Null(await transport.HandleLinkAsync(link));
        Assert.Equal("refused: the site's name resolves to a private, loopback or link-local address", Links[0].Note);
        Assert.Equal(0, _ui.Notices);
    }

    [Fact]
    public async Task TheNativeHostNeverForwardsALinkFromTheBrowser()
    {
        var (browser, host) = HostRelayTests.Pair();
        var connector = new HostRelayTests.Connector(_ => throw new InvalidOperationException("must not connect"));
        var relay = Task.Run(() => new HostRelay(host, host, connector).RunAsync());
        await Framing.WriteAsync(browser, Encoding.UTF8.GetBytes("{\"id\":4,\"type\":\"link\",\"origin\":\"https://a.example\",\"payload\":{\"url\":\"bringyourownagent://connect\"}}"));
        Assert.Equal("{\"id\":4,\"result\":null}", (await HostRelayTests.ReadAsync(browser)).ToJsonString());
        Assert.Empty(connector.Starts);
        browser.Dispose();
        await relay.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
