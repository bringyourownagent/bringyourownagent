using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace BringYourOwnAgent.Relay.Tests;

/// <summary>A clock the tests move by hand (codes expire, pairings go idle, old jobs are swept).</summary>
sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    DateTimeOffset _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>A WebSocket pair over a real loopback TCP connection, and a small client that reads whole messages without aborting on a timeout.</summary>
sealed class WsPeer(WebSocket ws)
{
    Task<(WebSocketMessageType Type, byte[] Data)>? _receiving;

    public WebSocket Ws => ws;

    public static async Task<(WebSocket Server, WsPeer Client, IDisposable Tcp)> Pair()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new TcpClient();
        var connect = client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        var server = await listener.AcceptTcpClientAsync();
        await connect;
        listener.Stop();
        var serverWs = WebSocket.CreateFromStream(server.GetStream(), new WebSocketCreationOptions { IsServer = true });
        var clientWs = WebSocket.CreateFromStream(client.GetStream(), new WebSocketCreationOptions { IsServer = false });
        return (serverWs, new WsPeer(clientWs), new Both(server, client));
    }

    sealed class Both(IDisposable a, IDisposable b) : IDisposable
    {
        public void Dispose() { a.Dispose(); b.Dispose(); }
    }

    public Task Send(string json) => ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);

    /// <summary>One message as raw bytes (a test may want to send something that is not JSON at all).</summary>
    public Task SendBytes(byte[] bytes) => ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);

    /// <summary>The next message: (Text, json) or (Close, null); null when nothing came in time (the receive goes on and is picked up next time).</summary>
    public async Task<(WebSocketMessageType Type, JsonElement? Json)?> Next(double seconds = 10)
    {
        _receiving ??= ReceiveWhole();
        if (await Task.WhenAny(_receiving, Task.Delay(TimeSpan.FromSeconds(seconds))) != _receiving) return null;
        var (type, data) = await _receiving;
        _receiving = null;
        return (type, type == WebSocketMessageType.Text ? JsonDocument.Parse(data).RootElement.Clone() : null);
    }

    /// <summary>The next text message of this type (others are skipped); fails the test when it does not come.</summary>
    public async Task<JsonElement> NextOf(string type, double seconds = 15, List<JsonElement>? seen = null)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            var m = await Next((until - DateTime.UtcNow).TotalSeconds);
            if (m is null) break;
            if (m.Value.Type == WebSocketMessageType.Close) throw new Xunit.Sdk.XunitException($"closed while waiting for «{type}»");
            if (m.Value.Json is not { } json) continue;
            seen?.Add(json);
            if (json.TryGetProperty("type", out var t) && t.GetString() == type) return json;
        }
        throw new Xunit.Sdk.XunitException($"no «{type}» in {seconds} s");
    }

    async Task<(WebSocketMessageType, byte[])> ReceiveWhole()
    {
        var buffer = new byte[1 << 20];
        using var message = new MemoryStream();
        try
        {
            while (true)
            {
                var r = await ws.ReceiveAsync(buffer.AsMemory(), CancellationToken.None);
                if (r.MessageType == WebSocketMessageType.Close) return (WebSocketMessageType.Close, []);
                message.Write(buffer, 0, r.Count);
                if (r.EndOfMessage) return (r.MessageType, message.ToArray());
            }
        }
        catch (Exception e) when (e is WebSocketException or ObjectDisposedException or IOException) { return (WebSocketMessageType.Close, []); }
    }
}

/// <summary>The manifest the tests put in their links: a site of the shape the rules want, without the check flag (a test adds it).</summary>
static class TestManifest
{
    public const string Json = """
        {
          "byoa": "0.1-draft",
          "purpose": {
            "summary": "Group chat where the visitor talks with their own AI models and AI agents at the same time",
            "limits": { "maxTextChars": 60000, "maxAnswerTokens": 4000, "maxCallSeconds": 300, "maxParallelCalls": 6, "maxCallsPerMinute": 30 }
          },
          "skills": {
            "text.generate": {
              "required": true,
              "models": { "count": { "min": 1, "max": 6 }, "names": true, "why": "You pick up to six of your own AIs and every one of them answers in the chat." }
            }
          },
          "privacy": "personal",
          "expectedCost": { "perSession": "0.05 USD" }
        }
        """;
}

/// <summary>What a site passes the relay in the tests, and the helpers that build one.</summary>
static class Rooms
{
    public const string A = "room-aaaaaaaaaaaaaaaa";
    public const string B = "room-bbbbbbbbbbbbbbbb";

    /// <summary>The settings of the reference site, with anything a test wants to change.</summary>
    public static RelayOptions Options(Action<RelayOptions>? change = null)
    {
        var options = new RelayOptions { ManifestJson = TestManifest.Json };
        change?.Invoke(options);
        return options;
    }

    public static DirectHub Hub(TimeProvider? clock = null, Action<RelayOptions>? change = null) => new(Options(change), clock);

    public static WalletJobs Jobs(DirectHub hub, TimeProvider? clock = null) => new(hub, hub.Options, clock);

    /// <summary>A wallet connection the hub can hold (it only talks to it to say «bye»).</summary>
    public static WalletChannel Channel(RelayOptions? options = null) =>
        new(WebSocket.CreateFromStream(new MemoryStream(), new WebSocketCreationOptions { IsServer = true }), options);

    public static string NewCode(this DirectHub hub, string room, string? ip) => hub.NewCode(room, ip, "ws://localhost:5291/byoa/ws")?.Code
        ?? throw new Xunit.Sdk.XunitException("no code was issued");

    public static string AcceptToken(this DirectHub hub, WalletChannel channel, string? session, string? pairing) =>
        hub.Accept(channel, session, pairing)?.Token ?? throw new Xunit.Sdk.XunitException("the hub refused the hello");

    /// <summary>The endpoint of a request as the page would make it (the wallet's own path, on a host of the test's choosing).</summary>
    public static string Endpoint(DirectHub hub, string host, bool https = false)
    {
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString(host);
        context.Request.Scheme = https ? "https" : "http";
        return hub.Endpoint(context.Request);
    }
}

/// <summary>The frames a wallet sends, built with the serializer so a test never fights JSON braces.</summary>
static class WalletFrames
{
    public static string Result(int id, bool ok, string? text = null, string? reason = null) =>
        JsonSerializer.Serialize(new { type = "result", id, ok, reason, output = text is null ? null : new { text } });

    public static string Chunk(int id, string text) => JsonSerializer.Serialize(new { type = "chunk", id, text });

    public static string Grant(string? limits, string models) =>
        "{\"type\":\"grant\",\"limits\":" + (limits ?? "null") + ",\"models\":{\"text.generate\":" + models + "}}";
}

/// <summary>A hello frame as a wallet sends it, with a grant the tests can shape.</summary>
static class Hello
{
    /// <param name="models">One <c>text.generate</c> list, as an array (the common case).</param>
    /// <param name="capabilities">The closed capability list as an array; appended to the grant when not null.</param>
    /// <param name="modelsBySkill">A whole <c>models</c> object (<c>{"text.generate":[...],"image.generate":[...]}</c>);
    /// it wins over <paramref name="models"/> when given.</param>
    public static string Json(string? session, string? pairing, string[]? skills = null, string? limits = null, string? models = null,
                              string? capabilities = null, string? modelsBySkill = null)
    {
        var grant = new StringBuilder("{");
        grant.Append("\"skills\":[").Append(string.Join(',', (skills ?? ["text.generate"]).Select(s => $"\"{s}\""))).Append(']');
        if (capabilities is not null) grant.Append(",\"capabilities\":").Append(capabilities);
        if (limits is not null) grant.Append(",\"limits\":").Append(limits);
        if (modelsBySkill is not null) grant.Append(",\"models\":").Append(modelsBySkill);
        else if (models is not null) grant.Append(",\"models\":{\"text.generate\":").Append(models).Append('}');
        grant.Append('}');
        return $$"""{"type":"hello","v":"0.1","session":{{Quote(session)}},"pairing":{{Quote(pairing)}},"wallet":{"name":"Fake wallet","version":"0.1"},"grant":{{grant}}}""";
    }

    /// <summary>The models list of the demo personas, as the grant carries it.</summary>
    public const string FourModels = """
        [{"ref":"m1","name":"Local Qwen 4B","id":"qwen/qwen3.5-4b","runs":"local"},
         {"ref":"m2","name":"GLM Flash","id":"z-ai/glm-5.3-flash","runs":"api-key"},
         {"ref":"m3","name":"DeepSeek","id":"deepseek/deepseek-v3.2","runs":"api-key"},
         {"ref":"m4","name":"Claude Code (Opus)","id":"agent/claude-code-opus","runs":"subscription"}]
        """;

    public const string SaneLimits = """{"maxTextChars":60000,"maxAnswerTokens":4000,"maxCallSeconds":300,"maxParallelCalls":4,"maxCallsPerMinute":30}""";

    static string Quote(string? value) => value is null ? "null" : $"\"{value}\"";
}