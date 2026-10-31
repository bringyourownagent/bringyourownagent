using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace BringYourOwnAgent.Relay;

/// <summary>How long a wallet connection may keep the server waiting.</summary>
/// <param name="Hello">From the connection to its <c>hello</c>.</param>
/// <param name="Idle">Between two messages after <c>welcome</c> (the wallet pings every 30 s, so a silent one is gone).</param>
/// <param name="Message">From the first bytes of a message to its end (a message trickled in byte by byte does not hold a buffer for long).</param>
/// <param name="Send">For one message to the wallet to be written (a wallet that stopped reading is dropped, the call counts as a failure).</param>
public sealed record WalletChannelLimits(TimeSpan Hello, TimeSpan Idle, TimeSpan Message, TimeSpan Send)
{
    /// <summary>What the reference site uses (and what <see cref="RelayOptions"/> defaults to).</summary>
    public static WalletChannelLimits Default { get; } = new(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(75), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5));
}

/// <summary>What the site is held to by the grant (CONTRACT.md §3.4, in the manifest's names).</summary>
public sealed record TextLimits(int MaxTextChars, int MaxAnswerTokens, int MaxCallSeconds, int MaxParallelCalls, int MaxCallsPerMinute)
{
    /// <summary>The wallet's own defaults when it tells the site nothing (CONTRACT.md §3.4: 8 parallel calls, 300 a minute, 100 000 characters).</summary>
    public static TextLimits Defaults { get; } = new(100_000, 20_000, 300, 8, 300);

    /// <summary>A wallet whose <c>hello</c> carries no <c>limits</c> at all: at most 4 calls in flight (CONTRACT-DIRECT.md §4).</summary>
    public static TextLimits NoLimits { get; } = Defaults with { MaxParallelCalls = 4 };
}

/// <summary>What a call's outcome means: an answer, a refusal, running out of time, or the wallet going away.</summary>
public enum WalletOutcome
{
    /// <summary>The wallet answered; <see cref="WalletAnswer.Text"/> carries it (it can be empty: the model said nothing readable).</summary>
    Text,
    /// <summary>The wallet refused (<c>ok:false</c>), or the model failed.</summary>
    Failed,
    /// <summary>Nothing came back in time.</summary>
    TimedOut,
    /// <summary>The page stopped waiting, or the wallet went away.</summary>
    Cancelled,
}

/// <summary>One call's answer: <paramref name="Text"/> is the wallet's text, cleaned and cut (<see cref="WalletText"/>).</summary>
public sealed record WalletAnswer(WalletOutcome Outcome, string Text = "", string? Reason = null);

/// <summary>
/// One visitor's agent wallet connected straight to the server (<c>wss://host/byoa/ws</c>, BYOA «direct» transport, CONTRACT-DIRECT.md §4–5, §7),
/// without the browser extension. The wallet opens it only after the visitor allowed the site. JSON text frames:
/// <list type="bullet">
/// <item>wallet → server first: <c>{"type":"hello","v":"0.1","session":"&lt;code&gt;"|null,"pairing":"&lt;token&gt;"|null,"grant":{...}}</c>;</item>
/// <item>server → wallet: <c>{"type":"welcome","pairing":"&lt;token&gt;","manifest":"&lt;base64url&gt;"}</c> (the manifest string the link carried; after a pairing
/// reconnect, the current one), or <c>{"type":"error","code":"bad-session"}</c> and a close;</item>
/// <item>server → wallet: <c>{"type":"call","id":N,"skill":"text.generate","input":{...},"params":{...}}</c>;</item>
/// <item>server → wallet: <c>{"type":"cancel","id":N}</c> when the site stops a call it still waits for (wallet 0.36);</item>
/// <item>wallet → server: <c>{"type":"chunk","id":N,"text":"..."}</c> while the model writes, then
/// <c>{"type":"result","id":N,"ok":true,"output":{"text":"..."}}</c> or <c>{"type":"result","id":N,"ok":false}</c>;</item>
/// <item>wallet → server: <c>{"type":"grant","models":{...},"limits":{...}}</c> when the visitor changes what the site was given;</item>
/// <item>either side: <c>{"type":"bye"}</c>.</item>
/// </list>
/// Rules: nothing is sent before a valid hello; frames up to 4 MB; unknown frames (and binary ones) are ignored; malformed JSON closes the connection;
/// a wallet that is silent too long, trickles a message, or stops reading what the server sends is dropped (<see cref="WalletChannelLimits"/>).
/// The wallet's text is the visitor's input: cleaned and cut like typed text (<see cref="WalletText"/>).
/// A site that wants to call the wallet itself takes the channel from <see cref="DirectHub.Channel"/> and calls
/// <see cref="CallAsync"/>.
/// </summary>
public sealed class WalletChannel
{
    /// <summary>The largest frame the server takes from a wallet (CONTRACT-DIRECT.md §4); a bigger one ends the connection.</summary>
    public const int MaxFrameBytes = 4 * 1024 * 1024;
    const int SmallBuffer = 64 * 1024;

    readonly WebSocket _ws;
    readonly RelayOptions _options;
    readonly WalletChannelLimits _limits;
    readonly SemaphoreSlim _send = new(1, 1);
    /// <summary>A call waiting for its result. <paramref name="MaxChars"/> is how much of the answer is kept;
    /// <paramref name="Partial"/> gathers the <c>chunk</c> frames as they arrive (the result replaces them).</summary>
    sealed record Waiting(TaskCompletionSource<WalletAnswer> Answer, int MaxChars, Action<string>? OnChunk, StringBuilder Partial);

    readonly ConcurrentDictionary<int, Waiting> _pending = new();
    readonly TaskCompletionSource _welcomed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    byte[] _buffer = new byte[SmallBuffer];                  // one buffer for incoming messages, grown for a big one and given back after it
    int _nextId;
    volatile GrantView _view = new([], TextLimits.NoLimits, [], [], new Dictionary<string, ModelView[]>());

    /// <param name="ws">The accepted web socket of the wallet.</param>
    /// <param name="options">The site's settings (the participants of one skill's list are read up to <see cref="RelayOptions.MaxModels"/>).</param>
    /// <param name="limits">The timeouts; <see cref="WalletChannelLimits.Default"/> when nothing is said.</param>
    public WalletChannel(WebSocket ws, RelayOptions? options = null, WalletChannelLimits? limits = null)
    {
        _ws = ws;
        _options = options ?? new RelayOptions();
        _limits = limits ?? _options.ChannelLimits;
    }

    /// <summary>The pairing token this connection was welcomed with.</summary>
    public string? Pairing { get; private set; }

    /// <summary>The skills the visitor allowed this site, as the wallet's <c>hello</c> said (<c>grant.skills</c>); empty when it said none.</summary>
    public IReadOnlySet<string> Skills { get; private set; } = new HashSet<string>();

    /// <summary>What the site is held to, as the wallet's <c>hello</c> or its last <c>grant</c> frame said.</summary>
    public TextLimits Limits => _view.Limits;

    /// <summary>The participants of <c>text.generate</c> this site was given, in the visitor's order (CONTRACT.md §3.3), as in 0.0.3.</summary>
    public ModelView[] Models => _view.Models;

    /// <summary>The participants this site was given, one list per skill, in the visitor's order (CONTRACT.md §3.3).</summary>
    public IReadOnlyDictionary<string, ModelView[]> ModelsBySkill => _view.ModelsBySkill;

    /// <summary>The feature names the wallet says it supports (wallet 0.36; empty from an older wallet) (CONTRACT.md §3.3).</summary>
    public IReadOnlyList<string> Capabilities => _view.Capabilities;

    /// <summary>What the hub tells the page (skills, limits, participants per skill, capabilities).</summary>
    public GrantView View() => _view;

    /// <summary>Did the visitor allow this skill?</summary>
    public bool Has(string skill) => Skills.Contains(skill);

    /// <summary>Is this ref one of the participants of the current grant for <paramref name="skill"/>? (A call may name nothing else.)</summary>
    public bool Knows(string skill, string? reference) =>
        reference is not null && _view.ModelsOf(skill).Any(m => string.Equals(m.Ref, reference, StringComparison.Ordinal));

    /// <summary>Is this ref one of the text participants of the current grant? (The job route asks for <c>text.generate</c> only.)</summary>
    public bool Knows(string? reference) => Knows("text.generate", reference);

    /// <summary>How many calls are waiting for the wallet's result (the grant's <c>maxParallelCalls</c> is kept against this).</summary>
    public int Pending => _pending.Count;

    /// <summary>The size of the buffer for incoming messages now (diagnostics, tests): small, except while a big message is being read.</summary>
    public int BufferBytes => _buffer.Length;

    /// <summary>One incoming JSON message, read without copying it: only what the server uses.</summary>
    sealed record Frame(string? Type, string? Session, string? Pairing, int? Id, bool Ok, string? Text, string? Chunk,
                        string[]? Skills = null, JsonElement? Models = null, JsonElement? Limits = null, bool HasLimits = false,
                        string[]? Capabilities = null, string? Reason = null);

    /// <summary>Serves one wallet connection until it closes.</summary>
    public static async Task RunAsync(WebSocket ws, DirectHub hub, ILogger log, CancellationToken ct, WalletChannelLimits? limits = null)
    {
        var channel = new WalletChannel(ws, hub.Options, limits);
        try
        {
            var helloBy = DateTime.UtcNow + channel._limits.Hello;
            while (true)
            {
                var left = helloBy - DateTime.UtcNow;
                var message = left > TimeSpan.Zero ? await channel.ReadAsync(left, ct) : null;
                if (message is null) { await channel.CloseAsync(WebSocketCloseStatus.PolicyViolation, "no hello"); return; }   // closed, too big, too slow
                if (message.Value.Binary) continue;                                         // unknown frames are ignored
                if (!TryRead(channel.Data(message.Value.Length), out var frame)) { await channel.CloseAsync(WebSocketCloseStatus.InvalidPayloadData, "malformed"); return; }
                if (frame?.Type != "hello") continue;
                channel.SetGrant(frame.Skills, frame.Capabilities, frame.Limits, frame.HasLimits, frame.Models, keepSkills: false);
                var welcome = hub.Accept(channel, frame.Session, frame.Pairing);
                if (welcome is not { } w)
                {
                    await channel.SendAsync(new { type = "error", code = "bad-session" }, ct);
                    await channel.CloseAsync(WebSocketCloseStatus.PolicyViolation, "bad-session");
                    return;
                }
                channel.Pairing = w.Token;
                await channel.SendAsync(new { type = "welcome", pairing = w.Token, manifest = w.Manifest }, ct);
                channel._welcomed.TrySetResult();
                log.LogInformation("wallet connected, skills {Skills}, model lists {Models}, capabilities {Capabilities}, parallel {Parallel}",
                    channel.Skills.Count, channel.ModelsBySkill.Count, channel.Capabilities.Count, channel.Limits.MaxParallelCalls);
                break;
            }

            while (true)
            {
                var message = await channel.ReadAsync(channel._limits.Idle, ct);
                if (message is null) return;
                if (message.Value.Binary) continue;
                if (!TryRead(channel.Data(message.Value.Length), out var frame)) { await channel.CloseAsync(WebSocketCloseStatus.InvalidPayloadData, "malformed"); return; }
                switch (frame?.Type)
                {
                    case "result" when frame.Id is { } id:
                        channel.Result(id, frame.Ok && frame.Text is not null, frame.Text, frame.Reason);       // ok without a text is no answer
                        break;
                    case "chunk" when frame.Id is { } id:
                        channel.Chunk(id, frame.Chunk);
                        break;
                    case "grant":
                        // a grant frame carries only the models and the limits (CONTRACT-DIRECT.md §4): the skills and the capabilities the visitor allowed stay as they are
                        channel.SetGrant(frame.Skills, frame.Capabilities, frame.Limits, frame.HasLimits, frame.Models, keepSkills: true);
                        log.LogInformation("grant changed: skills {Skills}, model lists {Models}, parallel {Parallel}",
                            channel.Skills.Count, channel.ModelsBySkill.Count, channel.Limits.MaxParallelCalls);
                        break;
                    case "bye":
                        await channel.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye");
                        return;
                }
            }
        }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException or ObjectDisposedException or TimeoutException) { }
        catch (Exception e) { log.LogWarning(e, "Wallet connection failed"); }
        finally
        {
            hub.Gone(channel);
            channel.CancelAll();
        }
    }

    /// <summary>
    /// One call down this connection: a <c>call</c> frame with the skill and its input and params (CONTRACT.md §3), and the wallet's
    /// answer. The answer keeps its lines and is cut at <paramref name="maxChars"/>. <paramref name="onChunk"/> gets the streamed pieces
    /// as they arrive. Never throws; the token stops waiting (a late result is then ignored).
    /// </summary>
    /// <param name="skill">The skill, <c>text.generate</c> (the only one 0.1 defines for a page) or another one the grant covers.</param>
    /// <param name="input">The skill's input, serialised as JSON.</param>
    /// <param name="params">The skill's parameters, serialised as JSON.</param>
    /// <param name="maxChars">How much of the answer is kept.</param>
    /// <param name="timeout">How long to wait: the wallet's own limit for a call may be up to 600 s, so a site's timeout is not shorter.</param>
    /// <param name="ct">Stops waiting; the call is then counted as cancelled.</param>
    /// <param name="onChunk">The whole text so far, each time a piece arrives.</param>
    /// <param name="onCallId">The id the call went to the wallet with, once the frame is out: <see cref="Cancel(int)"/> needs it to tell the wallet to stop.</param>
    public Task<WalletAnswer> CallAsync(string skill, object input, object @params, int maxChars, TimeSpan timeout, CancellationToken ct, Action<string>? onChunk = null, Action<int>? onCallId = null) =>
        CallCoreAsync(skill, input, @params, Math.Max(1, maxChars), timeout, ct, onChunk, onCallId);

    async Task<WalletAnswer> CallCoreAsync(string skill, object input, object @params, int maxChars, TimeSpan timeout, CancellationToken ct,
                                           Action<string>? onChunk, Action<int>? onCallId)
    {
        if (_ws.State != WebSocketState.Open) return new WalletAnswer(WalletOutcome.Cancelled);
        var id = Interlocked.Increment(ref _nextId);
        var answer = new TaskCompletionSource<WalletAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = new Waiting(answer, maxChars, onChunk ?? NoChunks, new StringBuilder());
        try
        {
            try { await _welcomed.Task.WaitAsync(timeout, ct); }
            catch (TimeoutException) { return new WalletAnswer(WalletOutcome.TimedOut); }
            try
            {
                await SendAsync(new { type = "call", id, skill, input, @params }, ct);
                onCallId?.Invoke(id);
            }
            catch (TimeoutException) { return new WalletAnswer(WalletOutcome.Failed); }
            try { return await answer.Task.WaitAsync(timeout, ct); }
            catch (TimeoutException) { return new WalletAnswer(WalletOutcome.TimedOut); }
        }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException or ObjectDisposedException or InvalidOperationException or TimeoutException)
        {
            return new WalletAnswer(WalletOutcome.Cancelled);
        }
        finally { _pending.TryRemove(id, out _); }
    }

    static readonly Action<string> NoChunks = _ => { };

    /// <summary>One streamed piece of an answer (<c>chunk</c>, CONTRACT-DIRECT.md §4). A preview only: the result replaces it.</summary>
    public bool Chunk(int id, string? text)
    {
        if (text is null || !_pending.TryGetValue(id, out var waiting) || waiting.OnChunk is not { } told || told == NoChunks) return false;
        var piece = WalletText.CleanPiece(text, waiting.MaxChars);      // the pieces are glued together: nothing is trimmed away here
        var whole = waiting.Partial;
        lock (whole)
        {
            var room = waiting.MaxChars - whole.Length;
            if (room <= 0) return false;
            if (piece.Length > room) piece = piece[..room];
            if (piece.Length > 0 && char.IsHighSurrogate(piece[^1])) piece = piece[..^1];
            if (piece.Length == 0) return false;
            whole.Append(piece);
            told(whole.ToString().TrimStart());                          // shown as the model writes it; the result replaces it in the end
        }
        return true;
    }

    /// <summary>The reasons a wallet may give for an <c>ok:false</c> (CONTRACT-DIRECT.md section 4); any other word is "failed".</summary>
    static readonly HashSet<string?> Reasons = new(StringComparer.Ordinal) { "timeout", "cancelled", "limit", "failed" };

    /// <summary>The wallet's result for a call; false when no such call is waiting (late, repeated, made up, or let go by the page).</summary>
    public bool Result(int id, bool ok, string? text, string? reason = null)
    {
        if (!_pending.TryRemove(id, out var waiting)) return false;
        if (!ok) return waiting.Answer.TrySetResult(new WalletAnswer(WalletOutcome.Failed, "", Reasons.Contains(reason) ? reason : "failed"));
        var clean = WalletText.CleanBlock(text, waiting.MaxChars);
        return waiting.Answer.TrySetResult(new WalletAnswer(WalletOutcome.Text, clean));
    }

    /// <summary>The site stops a call it still waits for: the wallet is told with <c>{"type":"cancel","id":N}</c>
    /// (CONTRACT-DIRECT.md §4, wallet 0.36) and a late result for it is dropped. False when no such call is waiting.</summary>
    public bool Cancel(int id)
    {
        if (!_pending.TryRemove(id, out var waiting)) return false;
        waiting.Answer.TrySetResult(new WalletAnswer(WalletOutcome.Cancelled));
        CancelCall(id);
        return true;
    }

    /// <summary>Tells the wallet to stop a call, whether or not the server still waits for it (the job route has
    /// already dropped the answer when this runs). Best effort: the wallet may be gone.</summary>
    public void CancelCall(int id) => _ = SendCancelAsync(id);

    async Task SendCancelAsync(int id)
    {
        try { await SendAsync(new { type = "cancel", id }, CancellationToken.None); }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException or ObjectDisposedException or InvalidOperationException or TimeoutException) { }
    }

    /// <summary>The server lets this wallet go: «bye» and a close.</summary>
    public async Task ByeAsync()
    {
        try
        {
            await SendAsync(new { type = "bye" }, CancellationToken.None);
            await CloseAsync(WebSocketCloseStatus.NormalClosure, "bye");
        }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException or ObjectDisposedException or InvalidOperationException or TimeoutException) { }
    }

    void CancelAll()
    {
        foreach (var id in _pending.Keys)
            if (_pending.TryRemove(id, out var waiting)) waiting.Answer.TrySetResult(new WalletAnswer(WalletOutcome.Cancelled));
    }

    /// <summary>What the wallet told the site about its grant: the skills, the capabilities, the limits and the participants (hello, and later <c>grant</c> frames).
/// <paramref name="keepSkills"/> is for a <c>grant</c> frame, which says nothing about skills or capabilities: what the visitor allowed earlier stands.</summary>
    void SetGrant(string[]? skills, string[]? capabilities, JsonElement? limits, bool hasLimits, JsonElement? models, bool keepSkills)
    {
        if (!keepSkills || skills is not null) Skills = new HashSet<string>(skills ?? [], StringComparer.Ordinal);
        var caps = capabilities is null && keepSkills ? _view.Capabilities : CleanCapabilities(capabilities);
        var parsed = TextLimitsOf(limits, hasLimits);
        var bySkill = ModelsOf(models, Math.Max(1, _options.MaxModels));
        _view = new GrantView([.. Skills.Order(StringComparer.Ordinal)], parsed, bySkill.TryGetValue("text.generate", out var text) ? text : [], caps, bySkill);
    }

    /// <summary>The wallet's closed capability list (CONTRACT.md §3.3): unknown names are dropped, repeats too.</summary>
    static readonly string[] CapabilityNames = { "needs", "can", "kind", "cancel", "image.generate", "images-in", "parts", "frame-limit" };

    static string[] CleanCapabilities(string[]? names)
    {
        if (names is null) return [];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var name in names)
        {
            if (!CapabilityNames.Contains(name) || !seen.Add(name)) continue;
            result.Add(name);
            if (result.Count == CapabilityNames.Length) break;
        }
        return [.. result];
    }

    /// <summary>The limits as the wallet states them; a bad or missing number becomes the contract's default, never «no limit».
    /// Everything is clamped to the protocol's ceilings so a wallet (or a bug) cannot make the site hold a call open forever.</summary>
    static TextLimits TextLimitsOf(JsonElement? limits, bool hasLimits)
    {
        var d = hasLimits ? TextLimits.Defaults : TextLimits.NoLimits;
        if (limits is not { ValueKind: JsonValueKind.Object } o) return d;
        return new TextLimits(
            Number(o, "maxTextChars", d.MaxTextChars, 0, 400_000),
            Number(o, "maxAnswerTokens", d.MaxAnswerTokens, 1, 32_000),
            Number(o, "maxCallSeconds", d.MaxCallSeconds, 1, 600),
            Number(o, "maxParallelCalls", d.MaxParallelCalls, 1, 32),
            Number(o, "maxCallsPerMinute", d.MaxCallsPerMinute, 0, 600));

        static int Number(JsonElement o, string name, int fallback, int low, int high) =>
            o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) && double.IsFinite(n)
                ? Math.Clamp((int)Math.Round(n), low, high)
                : fallback;
    }

    /// <summary>The participants of the grant, one list per skill: <c>{"text.generate":[{ref,name,id,runs}],"image.generate":[...]}</c>
    /// (CONTRACT.md §3.3). Everything is cut and cleaned here: a name, an id, a <c>runs</c> text and a <c>can</c>/<c>abilities</c>
    /// key are strings from the wallet, shown to the visitor as text and never as markup; verdicts are booleans only, at most four
    /// keys each, and only the properties or abilities the site can ask about.</summary>
    static Dictionary<string, ModelView[]> ModelsOf(JsonElement? models, int maxModels)
    {
        var result = new Dictionary<string, ModelView[]>(StringComparer.Ordinal);
        if (models is not { ValueKind: JsonValueKind.Object } o) return result;
        var skills = 0;
        foreach (var skill in o.EnumerateObject())
        {
            if (skills >= MaxSkills || result.ContainsKey(skill.Name)) continue;
            if (!IsSkillId(skill.Name) || skill.Value.ValueKind != JsonValueKind.Array) continue;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var list = new List<ModelView>();
            foreach (var item in skill.Value.EnumerateArray())
            {
                if (list.Count >= maxModels) break;
                if (item.ValueKind != JsonValueKind.Object) continue;
                var reference = Str(item, "ref", 16);
                if (reference is null || !IsRef(reference) || !seen.Add(reference)) continue;
                var id = Str(item, "id", 128);
                var name = Str(item, "name", 64) ?? reference;
                // an agent of the visitor's own can search the web and may take minutes; a model answers from its own knowledge. The wallet says which
                // (`kind`, also when the visitor hid the names); an older wallet does not, and then an id «agent/...» is the only sign there is
                var kind = Str(item, "kind", 16);
                var agent = kind is not null ? kind == "agent" : id is not null && id.StartsWith("agent/", StringComparison.OrdinalIgnoreCase);
                list.Add(new ModelView(reference, name, id, Str(item, "runs", 40) ?? "", agent ? "agent" : "model",
                                       BoolMap(item, "can", CanNames), agent ? BoolMap(item, "abilities", null) : null));
            }
            result[skill.Name] = [.. list];
            skills++;
        }
        return result;

        static bool IsRef(string r) => r.Length is >= 1 and <= 16 && r.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

        static string? Str(JsonElement e, string name, int max)
        {
            if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String) return null;
            var cleaned = WalletText.CleanLine(v.GetString(), max);
            return cleaned.Length == 0 ? null : cleaned;
        }
    }

    /// <summary>The most skills whose model lists are read out of one grant (the extension reads eight too).</summary>
    const int MaxSkills = 8;

    /// <summary>The properties a site may ask about in <c>needs</c>; a verdict for any other name is dropped.</summary>
    static readonly string[] CanNames = { "context", "input", "output", "runs" };

    static readonly Regex SkillIdPattern = new(@"\A[a-z][a-z0-9-]*\.[a-z][a-z0-9-]*\z", RegexOptions.CultureInvariant);

    static bool IsSkillId(string name) => SkillIdPattern.IsMatch(name);

    /// <summary>A verdict object of the grant's entry (<c>can</c>, <c>abilities</c>): boolean values only, at most four keys,
    /// only names the site can ask about (<paramref name="allowed"/>, or any safe ability name when it is null), cleaned like
    /// any other field from a wallet.</summary>
    static IReadOnlyDictionary<string, bool>? BoolMap(JsonElement entry, string name, string[]? allowed)
    {
        if (!entry.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object) return null;
        var map = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (map.Count >= 4) break;
            if (allowed is not null && !allowed.Contains(property.Name)) continue;
            if (!IsVerdictName(property.Name)) continue;
            if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) continue;
            map[property.Name] = property.Value.GetBoolean();
        }
        return map.Count == 0 ? null : map;
    }

    /// <summary>A safe key for <c>can</c> or <c>abilities</c>: short, and only letters, digits and <c>.-_:</c> (the vocabulary and <c>x-</c> names fit).</summary>
    static bool IsVerdictName(string name) =>
        name.Length is >= 1 and <= 64 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or ':');

    /// <summary>Writes one message within <see cref="WalletChannelLimits.Send"/>; a wallet that does not take it in is dropped (TimeoutException).</summary>
    async Task SendAsync(object message, CancellationToken ct)
    {
        if (_ws.State != WebSocketState.Open) return;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, JsonSerializerOptions.Web);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(_limits.Send);
        try
        {
            await _send.WaitAsync(limit.Token);
            try { await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, limit.Token); }
            finally { _send.Release(); }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _ws.Abort();                                                    // it stopped reading: nothing more will get through
            throw new TimeoutException("the wallet does not take in what the server sends");
        }
    }

    async Task CloseAsync(WebSocketCloseStatus status, string reason)
    {
        try
        {
            if (_ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await _ws.CloseOutputAsync(status, reason, CancellationToken.None).WaitAsync(_limits.Send);
        }
        catch (Exception e) when (e is WebSocketException or ObjectDisposedException or TimeoutException or InvalidOperationException) { }
    }

    ReadOnlyMemory<byte> Data(int length) => _buffer.AsMemory(0, length);

    /// <summary>The next whole message into the one buffer (<see cref="Data"/>, valid until the next read); null when the connection closed, nothing began
    /// within <paramref name="idle"/>, a begun message did not end within <see cref="WalletChannelLimits.Message"/>, or it grew past <see cref="MaxFrameBytes"/> (then it is closed).</summary>
    async Task<(bool Binary, int Length)?> ReadAsync(TimeSpan idle, CancellationToken ct)
    {
        if (_buffer.Length > SmallBuffer) _buffer = new byte[SmallBuffer];   // a big message is over: its buffer is let go
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(idle);
        var length = 0;
        var begun = false;
        while (true)
        {
            if (length == _buffer.Length)                                     // grow (never past one byte over the limit, which is how «too big» is seen)
            {
                var bigger = new byte[Math.Min(MaxFrameBytes + 1, _buffer.Length * 2)];
                _buffer.AsSpan(0, length).CopyTo(bigger);
                _buffer = bigger;
            }
            ValueWebSocketReceiveResult r;
            try { r = await _ws.ReceiveAsync(_buffer.AsMemory(length), wait.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }      // silent or too slow: the socket is aborted by the cancel
            if (r.MessageType == WebSocketMessageType.Close)
            {
                await CloseAsync(WebSocketCloseStatus.NormalClosure, "bye");
                return null;
            }
            if (!begun)
            {
                begun = true;
                wait.CancelAfter(_limits.Message);                            // the rest of this message must follow soon
            }
            length += r.Count;
            if (length > MaxFrameBytes)
            {
                await CloseAsync(WebSocketCloseStatus.MessageTooBig, "frame too big");
                return null;
            }
            if (r.EndOfMessage) return (r.MessageType == WebSocketMessageType.Binary, length);
        }
    }

    /// <summary>False: not JSON at all (the connection is closed). True with null: JSON, but not an object (ignored like any unknown frame).</summary>
    static bool TryRead(ReadOnlyMemory<byte> data, out Frame? frame)
    {
        frame = null;
        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return true;
            int? id = root.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.Number && i.TryGetInt32(out var n) ? n : null;
            var ok = root.TryGetProperty("ok", out var k) && k.ValueKind == JsonValueKind.True;
            var text = root.TryGetProperty("output", out var o) && o.ValueKind == JsonValueKind.Object ? Str(o, "text") : null;
            // a hello carries the grant inside «grant»; a later grant frame carries models and limits at the top level (CONTRACT-DIRECT.md §4).
            // Every access goes through Object()/Array() first: TryGetProperty on a JsonElement that is not an object throws.
            var grant = Object(root, "grant");
            var hasGrant = grant is not null || root.TryGetProperty("models", out _);
            var limitsNode = Object(grant, "limits") ?? Object(root, "limits");
            var modelsNode = Object(grant, "models") ?? Object(root, "models");
            string[]? skills = null;
            if (Array(grant, "skills") is { } skillsNode)
                skills = skillsNode.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).Take(32).ToArray();
            string[]? capabilities = null;
            if (Array(grant, "capabilities") is { } capabilitiesNode)
                capabilities = capabilitiesNode.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).Take(32).ToArray();
            frame = new Frame(Str(root, "type"), Str(root, "session"), Str(root, "pairing"), id, ok, text,
                              root.TryGetProperty("text", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null,
                              skills, modelsNode?.Clone(), limitsNode?.Clone(), limitsNode is not null, capabilities, Str(root, "reason"));
            return true;
        }
        catch (JsonException) { return false; }
    }

    static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>The property as an object, or null when it is missing or of another kind (never an exception).</summary>
    static JsonElement? Object(JsonElement? e, string name) =>
        e is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    /// <summary>The property as an array, or null when it is missing or of another kind.</summary>
    static JsonElement? Array(JsonElement? e, string name) =>
        e is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v : null;
}