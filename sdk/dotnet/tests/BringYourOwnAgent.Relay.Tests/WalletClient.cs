using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace BringYourOwnAgent.Relay.Tests;

/// <summary>One AI the pretend wallet offers: a name, what it is, and how long it thinks before it answers.</summary>
/// <param name="Ref">What a call names (<c>m1</c>...); the site may only use refs it was given.</param>
/// <param name="Id">The model's real name in the wallet's style; <c>agent/...</c> makes it an agent (the site shows "AGENT · can search the web").</param>
/// <param name="Runs">Where it runs, as the grant says: <c>local</c>, <c>home-server</c> or <c>api-key</c>.</param>
public sealed record Persona(string Ref, string Name, string Id, string Runs, int DelayMs)
{
    public bool IsAgent => Id.StartsWith("agent/", StringComparison.OrdinalIgnoreCase);
}

/// <summary>How the pretend wallet behaves: the participants, who fails, and the tricks a test wants to play.</summary>
public sealed class WalletOptions
{
    /// <summary>The four personas of the demo: a quick local model, two slower ones, and an agent that "checks the web".</summary>
    public static Persona[] Demo { get; } =
    [
        new("m1", "Local Qwen 4B", "qwen/qwen3.5-4b", "local", 1000),
        new("m2", "GLM Flash", "z-ai/glm-5.3-flash", "api-key", 3000),
        new("m3", "DeepSeek", "deepseek/deepseek-v3.2", "api-key", 6000),
        new("m4", "Claude Code (Opus)", "agent/claude-code-opus", "subscription", 20000),
    ];

    public List<Persona> Models { get; init; } = [.. Demo];
    /// <summary>Refs that answer <c>ok:false</c> (the site shows "the wallet refused the request").</summary>
    public HashSet<string> Fail { get; init; } = new(StringComparer.Ordinal);
    /// <summary>Answer in pieces (<c>chunk</c> frames) as a model writes, at most one every 80 ms.
    /// Off only to check that a site works with a wallet that does not stream.</summary>
    public bool Stream { get; set; } = true;
    /// <summary>Seconds after the connection when the wallet tells the site its models changed (a <c>grant</c> frame).</summary>
    public double? GrantChangeAfter { get; set; }
    /// <summary>Seconds after the connection when the wallet drops the connection (the site must say "the wallet went away").</summary>
    public double? DropAfter { get; set; }
    /// <summary>Where the wallet writes what it does (never a token, never a message text: only kinds and numbers).</summary>
    public TextWriter Log { get; init; } = Console.Out;
    public bool Quiet { get; set; }

    public void Note(string line)
    {
        if (Quiet) return;
        Log.WriteLine(line);
        Log.Flush();                     // a test that reads this log while the wallet runs must see the lines at once
    }
}

/// <summary>
/// A pretend BYOA wallet speaking the «direct» protocol of CONTRACT-DIRECT.md: it opens the WebSocket to a site's server, says
/// <c>hello</c> with a grant (skills, limits, models), checks that <c>welcome</c> echoes the manifest of the link byte for byte,
/// answers <c>call</c> frames for <c>text.generate</c> after the persona's delay (in <c>chunk</c> pieces and then a <c>result</c>),
/// can send a <c>grant</c> frame, can fail a model, can drop the connection, pings every 30 s and says <c>bye</c> when asked.
/// The end-to-end test of this library drives it against a real site over real HTTP and a real WebSocket; the reference site
/// (ChatToAgents) has a console copy of it (<c>tools/FakeWallet</c>) with exactly the same behaviour.
/// </summary>
public sealed class WalletClient(WalletOptions? options = null) : IAsyncDisposable
{
    public const int PieceChars = 40;                 // the size of one streamed piece
    public static readonly TimeSpan PieceEvery = TimeSpan.FromMilliseconds(80);

    readonly WalletOptions _options = options ?? new WalletOptions();
    readonly SemaphoreSlim _send = new(1, 1);
    WebSocket? _ws;
    Task? _pinger;
    CancellationTokenSource? _life;

    public WalletOptions Options => _options;

    /// <summary>The pairing token the site gave this wallet (a secret: it is kept, never printed).</summary>
    public string? Pairing { get; private set; }

    /// <summary>What the site's <c>welcome</c> echoed (must be the manifest the link carried, byte for byte).</summary>
    public string? WelcomeManifest { get; private set; }

    /// <summary>How many calls this wallet has answered (tests).</summary>
    public int Answered { get; private set; }

    /// <summary>Reads a <c>bringyourownagent://connect</c> link: the endpoint, the one-time code and the manifest it carries.</summary>
    public static (string Endpoint, string Session, string Manifest) ParseLink(string link)
    {
        var at = link.IndexOf('?');
        if (!link.StartsWith("bringyourownagent://connect", StringComparison.OrdinalIgnoreCase) || at < 0)
            throw new ArgumentException("not a bringyourownagent://connect link", nameof(link));
        string endpoint = "", session = "", manifest = "";
        foreach (var pair in link[(at + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq < 0) continue;
            var name = pair[..eq];
            var value = Uri.UnescapeDataString(pair[(eq + 1)..]);
            switch (name)
            {
                case "endpoint": endpoint = value; break;
                case "session": session = value; break;
                case "manifest": manifest = value; break;
            }
        }
        if (endpoint.Length == 0 || session.Length == 0) throw new ArgumentException("the link has no endpoint or no session", nameof(link));
        return (endpoint, session, manifest);
    }

    /// <summary>May the wallet act on this endpoint? <c>wss</c> anywhere, <c>ws</c> only on this computer
    /// (the wallet's own rule is «wss only», with a developer switch for local tests, CONTRACT-DIRECT.md §1).</summary>
    public static bool EndpointAllowed(string endpoint, bool allowLocalWs)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme == "wss") return true;
        return allowLocalWs && uri.Scheme == "ws" && (uri.IsLoopback || uri.Host is "localhost" or "127.0.0.1" or "[::1]");
    }

    /// <summary>Opens the channel and says <c>hello</c>. Returns the site's answer: the pairing (or null), the manifest it echoed,
    /// or the error code it refused with. <paramref name="grant"/> overrides what the hello says about the grant.</summary>
    public async Task<(string? Pairing, string? Manifest, string? Error)> ConnectAsync(
        string endpoint, string? session, string? pairing, JsonObject? grant = null, CancellationToken ct = default)
    {
        var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(endpoint), ct);
        return await ConnectWithAsync(ws, session, pairing, grant, ct);
    }

    /// <summary>The same, over a socket somebody else opened — the site's end-to-end test takes it from the test host, so the whole
    /// handshake runs in process through the real ASP.NET pipeline.</summary>
    public async Task<(string? Pairing, string? Manifest, string? Error)> ConnectWithAsync(
        WebSocket ws, string? session, string? pairing, JsonObject? grant = null, CancellationToken ct = default)
    {
        _ws = ws;
        _life = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _pinger = PingAsync(_life.Token);
        await SendAsync(Hello(session, pairing, grant), ct);
        var frame = await NextAsync(TimeSpan.FromSeconds(15), ct) ?? throw new TimeoutException("the site did not answer the hello");
        var type = frame["type"]?.GetValue<string>();
        if (type == "error") return (null, null, frame["code"]?.GetValue<string>() ?? "error");
        if (type != "welcome") throw new InvalidOperationException($"the site answered «{type}» to a hello");
        Pairing = frame["pairing"]?.GetValue<string>();
        WelcomeManifest = frame["manifest"]?.GetValue<string>();
        _options.Note($"connected: welcome with a pairing of {Pairing?.Length ?? 0} characters");
        return (Pairing, WelcomeManifest, null);
    }

    /// <summary>The hello frame: the code (single use) or the pairing token, and what the site is given.</summary>
    public JsonObject Hello(string? session, string? pairing, JsonObject? grant = null)
    {
        var skills = new JsonArray("text.generate");
        var models = new JsonArray();
        foreach (var p in _options.Models)
            models.Add(new JsonObject { ["ref"] = p.Ref, ["name"] = p.Name, ["id"] = p.Id, ["runs"] = p.Runs, ["kind"] = p.IsAgent ? "agent" : "model" });
        var g = grant ?? new JsonObject
        {
            ["skills"] = skills,
            ["expiresAt"] = DateTimeOffset.UtcNow.AddHours(8).ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["limits"] = new JsonObject
            {
                ["maxTextChars"] = 60_000, ["maxAnswerTokens"] = 4000, ["maxCallSeconds"] = 300, ["maxParallelCalls"] = 4, ["maxCallsPerMinute"] = 30,
            },
            ["models"] = new JsonObject { ["text.generate"] = models },
        };
        return new JsonObject
        {
            ["type"] = "hello", ["v"] = "0.1", ["session"] = session, ["pairing"] = session is null ? pairing : null,
            ["wallet"] = new JsonObject { ["name"] = "Fake wallet", ["version"] = "0.1" }, ["grant"] = g.DeepClone(),
        };
    }

    /// <summary>The frame the wallet sends when the visitor changes what the site was given (<c>grant</c>, CONTRACT-DIRECT.md §4).</summary>
    public JsonObject GrantFrame(IReadOnlyList<Persona> models)
    {
        var list = new JsonArray();
        foreach (var p in models) list.Add(new JsonObject { ["ref"] = p.Ref, ["name"] = p.Name, ["id"] = p.Id, ["runs"] = p.Runs, ["kind"] = p.IsAgent ? "agent" : "model" });
        return new JsonObject
        {
            ["type"] = "grant",
            ["limits"] = new JsonObject
            {
                ["maxTextChars"] = 60_000, ["maxAnswerTokens"] = 4000, ["maxCallSeconds"] = 300, ["maxParallelCalls"] = 4, ["maxCallsPerMinute"] = 30,
            },
            ["models"] = new JsonObject { ["text.generate"] = list },
        };
    }

    /// <summary>The handle a name is mentioned by, exactly as the site does it: lower case, letters and digits, hyphens between words.</summary>
    public static string HandleOf(string name)
    {
        var sb = new StringBuilder();
        var last = '-';
        foreach (var ch in (name ?? "").ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) { sb.Append(ch); last = ch; }
            else if (sb.Length > 0 && last != '-') { sb.Append('-'); last = '-'; }
        }
        var handle = sb.ToString().Trim('-');
        return handle.Length > 24 ? handle[..24].TrimEnd('-') : handle;
    }

    /// <summary>
    /// The newest message of the transcript — the last «[Name (@handle)]: text» block before the request line — with the handles it
    /// mentions. This is what the page writes: one line per message, «[Visitor (@visitor)]: …», and the newest one is what decides
    /// whether this participant was addressed at all.
    /// </summary>
    public static (string Name, string Handle, string Text)? LastMessage(string transcript)
    {
        var lines = (transcript ?? "").Replace("\r\n", "\n").Split('\n');
        var end = lines.Length;
        for (var i = 0; i < lines.Length; i++)
            if (lines[i].TrimStart().StartsWith("The latest message is for:", StringComparison.Ordinal)) { end = i; break; }
        var start = -1;
        for (var i = 0; i < end; i++)
            if (lines[i].TrimStart().StartsWith('[') && lines[i].Contains("]: ", StringComparison.Ordinal)) start = i;
        if (start < 0) return null;
        var first = lines[start].Trim();
        var cut = first.IndexOf("]: ", StringComparison.Ordinal);
        var label = first[1..cut];
        var handle = label.Contains('(') ? label[(label.IndexOf('(') + 1)..].TrimEnd(')').TrimStart('@') : HandleOf(label);
        var name = label.Contains('(') ? label[..label.IndexOf('(')].Trim() : label;
        var text = string.Join("\n", lines[start..end]).Trim();
        return (name, handle, text[(cut + 2)..].Trim());
    }

    /// <summary>The handles a text mentions: an «@» that starts a word, then letters, digits and hyphens. «a@b.com» is not a mention.</summary>
    public static List<string> Mentions(string text)
    {
        var found = new List<string>();
        var value = text ?? "";
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '@') continue;
            if (i > 0 && (char.IsLetterOrDigit(value[i - 1]) || value[i - 1] is '_' or '-' or '@')) continue;
            var end = i + 1;
            while (end < value.Length && (char.IsLetterOrDigit(value[end]) || value[end] == '-')) end++;
            var token = value[(i + 1)..end].TrimEnd('-');
            if (token.Length == 0) continue;
            var handle = token.ToLowerInvariant();
            if (!found.Contains(handle)) found.Add(handle);
        }
        return found;
    }

    /// <summary>What the site says about this call: «The latest message is for: you.» / «everybody.» (null when it says nothing).</summary>
    public static string? ForWhom(string transcript)
    {
        var line = LastLineSaying(transcript, "The latest message is for:");
        return line?.Trim().TrimEnd('.').ToLowerInvariant();
    }

    /// <summary>
    /// Was this participant asked? The site says so in the «latest message is for» line, and the newest message says it in its
    /// mentions. A message that mentions somebody else and neither this participant nor @all is not for it: NO_REPLY.
    /// </summary>
    public static bool Addressed(string transcript, string handle)
    {
        var last = LastMessage(transcript);
        if (last is null) return false;
        var mentions = Mentions(last.Value.Text);
        var forWhom = ForWhom(transcript);
        if (mentions.Count == 0) return forWhom is null or "you" or "everybody";
        return mentions.Contains(handle) || mentions.Contains("all") || mentions.Contains("everyone");
    }

    /// <summary>Answers calls until the connection closes. Codes and messages are never printed: only what happened.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        Task? grantChange = _options.GrantChangeAfter is { } after ? GrantChangeLater(TimeSpan.FromSeconds(after), ct) : null;
        Task? drop = _options.DropAfter is { } gone ? DropLater(TimeSpan.FromSeconds(gone), ct) : null;
        var calls = new List<Task>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var frame = await NextAsync(TimeSpan.FromSeconds(120), ct);
                if (frame is null) { _options.Note($"the site closed the connection after {calls.Count} call(s)"); break; }
                switch (frame["type"]?.GetValue<string>())
                {
                    case "call":
                        var call = AnswerCallAsync(frame, ct);
                        calls.Add(call);
                        break;
                    case "bye":
                        _options.Note("the site said bye");
                        await CloseAsync();
                        return;
                    case "ping":
                        break;                                        // the site may ping: nothing to do
                }
            }
        }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException or ObjectDisposedException) { }
        finally
        {
            _options.Note($"done: {(int)(DateTime.UtcNow - started).TotalSeconds} s, {Answered} answer(s)");
            if (grantChange is not null || drop is not null) await Task.WhenAll(new[] { grantChange, drop }.Where(t => t is not null).Select(t => t!.ContinueWith(_ => { })));
        }
    }

    /// <summary>One <c>text.generate</c> call: think for the persona's delay, then stream the answer (if the site asked for a stream
    /// and this wallet streams) and send the result. A ref marked <c>--fail</c> answers <c>ok:false</c>.
    /// NO_REPLY (a message that was not for this participant) comes back quickly: there was nothing to think about.</summary>
    async Task AnswerCallAsync(JsonObject call, CancellationToken ct)
    {
        var id = call["id"]!.DeepClone();
        var @params = call["params"] as JsonObject;
        var reference = @params?["model"]?.GetValue<string>();
        var persona = _options.Models.FirstOrDefault(p => p.Ref == reference);
        var messages = MessagesOf(call);
        var stream = _options.Stream && @params?["stream"]?.GetValue<bool>() == true;
        var text = persona is null ? "" : AnswerText(persona, messages, _options.Models.IndexOf(persona), _options.Models);
        var silent = text.Length == 0;
        var delay = persona is null ? 1500 : silent ? Math.Min(persona.DelayMs, 400) : persona.DelayMs;
        _options.Note($"call for {reference ?? "(no model)"}: {messages.Count} message(s), stream {stream}, thinking {delay} ms{(silent ? ", nothing to say" : "")}");

        try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { return; }
        if (persona is null || _options.Fail.Contains(persona.Ref))
        {
            await SendAsync(new JsonObject { ["type"] = "result", ["id"] = id.DeepClone(), ["ok"] = false }, ct);   // refused, or no such model
            Answered++;
            return;
        }
        if (stream)
        {
            for (var at = 0; at < text.Length; at += PieceChars)
            {
                var piece = text.Substring(at, Math.Min(PieceChars, text.Length - at));
                await SendAsync(new JsonObject { ["type"] = "chunk", ["id"] = id.DeepClone(), ["text"] = piece }, ct);
                await Task.Delay(PieceEvery, ct);
            }
        }
        var receipt = new JsonObject { ["cost"] = persona.Runs == "api-key" ? "0.001 USD" : "0 USD", ["provider"] = persona.Runs == "local" ? "local" : persona.Runs, ["model"] = persona.Ref };
        await SendAsync(new JsonObject { ["type"] = "result", ["id"] = id.DeepClone(), ["ok"] = true, ["output"] = new JsonObject { ["text"] = silent ? "NO_REPLY" : text }, ["receipt"] = receipt }, ct);
        Answered++;
        _options.Note(silent ? $"answered {persona.Ref} with NO_REPLY" : $"answered {persona.Ref} with {text.Length} characters");
    }

    /// <summary>
    /// The canned but context-aware answer of one persona, under the rules of the new page (the brief's demo script):
    /// it reads the newest message, its mentions and the «latest message is for» line, and answers only when it was addressed
    /// (an empty string means NO_REPLY: the site does not show those at all). It quotes the visitor's message and says who spoke
    /// before it, and it starts with its own name, as a real model often does, so the site's stripping of «Name:» is exercised.
    /// Four words make it reproducible: «verify» (the quick model asks the agent, the agent checks its pages and ends with @visitor),
    /// «everyone» (the slow model answers @all, so the others are asked once more), «debate» (the two models tag each other in turn
    /// until the room pauses itself) and «quiet» (the models have nothing to say and say so).
    /// </summary>
    public static string AnswerText(Persona persona, IReadOnlyList<(string Role, string Content)> messages, int index,
        IReadOnlyList<Persona>? others = null)
    {
        var transcript = messages.Where(m => m.Role == "user").Select(m => m.Content).LastOrDefault() ?? "";
        var last = LastMessage(transcript);
        if (last is null || !Addressed(transcript, HandleOf(persona.Name))) return "";
        var words = last.Value.Text.ToLowerInvariant();                       // the NEWEST message decides which rule applies
        var quoted = Quote(VisitorSaid(transcript), others);                  // and the answer quotes what the human asked
        var before = OtherSpeakers(transcript, persona.Name);
        var verify = words.Contains("verify", StringComparison.Ordinal);

        if (words.Contains("quiet", StringComparison.Ordinal) && !persona.IsAgent) return "";     // «quiet»: the models say nothing
        if (words.Contains("debate", StringComparison.Ordinal)) return Debate(persona, quoted);
        if (verify && persona.IsAgent) return Checked(quoted, before, toVisitor: true);
        if (verify && persona.DelayMs <= 2000)
            return $"{persona.Name}: @{HandleOf("Claude Code (Opus)")} can you verify this? You asked: \"{quoted}\". "
                 + "I answer from my own knowledge only and cannot browse, so I would rather have it checked.";
        if (words.Contains("everyone", StringComparison.Ordinal) && persona.DelayMs >= 5000)
            return $"{persona.Name}: @all I would add one thing to what was said. You asked: \"{quoted}\". "
                 + "I answer from my own knowledge only and cannot browse, so this is what I know without looking anything up.";
        return persona.IsAgent ? Checked(quoted, before) : Plain(persona, quoted, before, index);
    }

    /// <summary>What a model says when it answers for itself: short, from its own knowledge, and it names who spoke before it.</summary>
    static string Plain(Persona persona, string quoted, List<string> before, int index)
    {
        var sb = new StringBuilder();
        sb.Append(persona.Name).Append(": ");
        sb.Append("You asked: \"").Append(quoted).Append("\". ");
        if (before.Count > 0) sb.Append(before[0]).Append(" answered before me; I read it and mostly agree, with one difference: I would put it more simply. ");
        sb.Append("I answer from my own knowledge only and cannot browse, so this is what I know without looking anything up. ");
        sb.Append("Point ").Append(index + 1).Append(" of the chat, as far as I can tell.");
        return sb.ToString();
    }

    /// <summary>What the agent says: the pages it checked, their addresses, and (when it was asked to verify) the human at the end.</summary>
    static string Checked(string quoted, List<string>? before = null, bool toVisitor = false)
    {
        var name = "Claude Code (Opus)";
        var sb = new StringBuilder();
        sb.Append(name).Append(": I looked this up before answering. ");
        sb.Append("You asked: \"").Append(quoted).Append("\". ");
        if (before is { Count: > 0 }) sb.Append(before[0]).Append(" spoke before me; I checked its claim. ");
        sb.Append("\n\nI checked two pages: https://example.org/byoa and https://example.net/notes. ");
        sb.Append("Both describe the same thing in different words, and neither says anything that changes my answer. ");
        sb.Append("This is an agent of yours, so this took a while on purpose.");
        sb.Append("\n\nSources: https://example.org/byoa, https://example.net/notes");
        if (toVisitor) sb.Append("\n\n@visitor that is what I found; is it enough?");
        return sb.ToString();
    }

    /// <summary>Two models that keep answering each other — what the room's own safety pause (12 asks) is there for.</summary>
    static string Debate(Persona persona, string quoted)
    {
        var other = persona.Ref == "m1" ? "GLM Flash" : persona.Ref == "m2" ? "Local Qwen 4B" : null;
        var sb = new StringBuilder();
        sb.Append(persona.Name).Append(": ");
        sb.Append("You asked: \"").Append(quoted).Append("\". ");
        sb.Append("I still think my reading of it is the better one, and I can say why in one line.");
        if (other is not null) sb.Append(" @").Append(HandleOf(other)).Append(" do you hold your point?");
        return sb.ToString();
    }

    /// <summary>What the human last said in this transcript (the newest message of the Visitor), or «your message».</summary>
    public static string VisitorSaid(string transcript)
    {
        var said = "your message";
        var lines = (transcript ?? "").Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (!trimmed.StartsWith("[Visitor", StringComparison.Ordinal) || !trimmed.Contains("]: ", StringComparison.Ordinal)) continue;
            var from = trimmed.IndexOf("]: ", StringComparison.Ordinal) + 3;
            var block = new List<string> { trimmed[from..] };
            for (var j = i + 1; j < lines.Length && lines[j].Trim().Length > 0 && !lines[j].TrimStart().StartsWith('['); j++) block.Add(lines[j]);
            said = string.Join("\n", block).Trim();
        }
        return said;
    }

    /// <summary>
/// A quote short enough to sit in one sentence — and never a mention: quoting somebody is not addressing them, and a quote that
/// still carried the @handle would ask that participant all over again (the room would never stop). A handle this wallet knows
/// becomes the name of that participant, which is what a person quoting somebody would write.
/// </summary>
    static string Quote(string text, IReadOnlyList<Persona>? others = null)
    {
        var flat = string.Join(' ', (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries)).Trim();
        flat = System.Text.RegularExpressions.Regex.Replace(flat, @"(?<![\p{L}\p{N}_@-])@([\p{L}\p{N}-]+)", (match) =>
        {
            var handle = match.Groups[1].Value.ToLowerInvariant();
            return others?.FirstOrDefault(p => HandleOf(p.Name) == handle)?.Name ?? "";
        });
        flat = System.Text.RegularExpressions.Regex.Replace(flat, @"\s{2,}", " ").Trim();
        if (flat.Length == 0) flat = "your message";
        return flat.Length <= 140 ? flat : flat[..140] + "…";
    }

    /// <summary>The text after the last line that starts with <paramref name="label"/> (for example «[Visitor]:») — the site's transcript labels.</summary>
    public static string? LastLineSaying(string text, string label)
    {
        string? found = null;
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith(label, StringComparison.Ordinal))
            {
                var rest = trimmed[label.Length..].Trim();
                if (rest.Length > 0) found = rest;
            }
        }
        return found;
    }

    /// <summary>
    /// The names of the other participants that appear in the transcript, in the order they spoke. The site's labels are
    /// «[Name (@handle)]:», so the name is what stands before the bracket.
    /// </summary>
    public static List<string> OtherSpeakers(string transcript, string selfName)
    {
        var names = new List<string>();
        foreach (var line in transcript.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith('[') || !trimmed.Contains("]: ")) continue;
            var end = trimmed.IndexOf("]: ", StringComparison.Ordinal);
            var label = trimmed[1..end];
            var name = label.Contains('(') ? label[..label.IndexOf('(')].Trim() : label;
            if (name.Length == 0 || name == "Visitor" || name == selfName || names.Contains(name)) continue;
            names.Add(name);
        }
        return names;
    }

    static List<(string Role, string Content)> MessagesOf(JsonObject call)
    {
        var list = new List<(string, string)>();
        if (call["input"] is JsonObject input && input["messages"] is JsonArray messages)
            foreach (var m in messages)
                if (m is JsonObject o) list.Add((o["role"]?.GetValue<string>() ?? "", o["content"]?.GetValue<string>() ?? ""));
        return list;
    }

    /// <summary>Tells the site its models changed: one is gone, another appeared (exactly what the wallet's Sites page does).</summary>
    async Task GrantChangeLater(TimeSpan after, CancellationToken ct)
    {
        try { await Task.Delay(after, ct); } catch (OperationCanceledException) { return; }
        var changed = _options.Models.Where(p => p.Ref != "m3").ToList();
        changed.Add(new Persona("m5", "Gemma Tiny", "google/gemma-4-2b", "local", 1500));
        _options.Models.Clear();
        _options.Models.AddRange(changed);
        await SendAsync(GrantFrame(_options.Models), ct);
        _options.Note($"grant changed: {_options.Models.Count} models now ({string.Join(", ", _options.Models.Select(m => m.Ref))})");
    }

    async Task DropLater(TimeSpan after, CancellationToken ct)
    {
        try { await Task.Delay(after, ct); } catch (OperationCanceledException) { return; }
        _options.Note("dropping the connection (as a wallet whose visitor closed it)");
        try { _ws?.Abort(); } catch (Exception e) when (e is WebSocketException or ObjectDisposedException) { }
    }

    async Task PingAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(30), ct); } catch (OperationCanceledException) { return; }
            try { await SendAsync(new JsonObject { ["type"] = "ping" }, ct); } catch { return; }
        }
    }

    /// <summary>One frame from the site, or null when the connection closed or nothing came in time.</summary>
    public async Task<JsonObject?> NextAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var ws = _ws ?? throw new InvalidOperationException("not connected");
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(timeout);
        try
        {
            while (true)
            {
                var r = await ws.ReceiveAsync(buffer.AsMemory(), wait.Token);
                if (r.MessageType == WebSocketMessageType.Close) return null;
                message.Write(buffer, 0, r.Count);
                if (message.Length > 4 * 1024 * 1024) throw new InvalidOperationException("the site sent more than a wallet accepts");
                if (r.EndOfMessage) break;
            }
        }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException or ObjectDisposedException) { return null; }
        return JsonNode.Parse(message.ToArray()) as JsonObject;
    }

    public Task SendAsync(JsonObject frame, CancellationToken ct = default) => SendAsync(frame.ToJsonString(), ct);

    public async Task SendAsync(string json, CancellationToken ct = default)
    {
        var ws = _ws ?? throw new InvalidOperationException("not connected");
        var bytes = Encoding.UTF8.GetBytes(json);
        await _send.WaitAsync(ct);
        try { await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct); }
        finally { _send.Release(); }
    }

    public async Task CloseAsync()
    {
        try
        {
            if (_ws is { State: WebSocketState.Open }) await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        }
        catch (Exception e) when (e is WebSocketException or ObjectDisposedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        try { _life?.Cancel(); } catch (ObjectDisposedException) { }
        try { _ws?.Dispose(); } catch (Exception e) when (e is WebSocketException or ObjectDisposedException) { }
        if (_pinger is not null) await Task.WhenAny(_pinger, Task.Delay(1000));
        _life?.Dispose();
        _send.Dispose();
    }
}