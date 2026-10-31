using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;

namespace BringYourOwnAgent.Relay;

/// <summary>What the server knows about a pairing token (a wallet connected without the extension).</summary>
public enum PairingState
{
    /// <summary>A wallet is connected with that pairing right now.</summary>
    Live,
    /// <summary>The pairing is known, but its wallet is away (it may come back by itself).</summary>
    Offline,
    /// <summary>No such pairing here: unknown, another room's, or forgotten after a day of silence.</summary>
    Unknown,
}

/// <summary>One participant as the page sees it: the wallet's own list entry, plus the kind the page shows.</summary>
/// <param name="Ref">What a call names (<c>m1</c>, <c>m2</c>...); it can change between connections, so the page keys its chat by <paramref name="Id"/> where it has one.</param>
/// <param name="Name">The visitor's own name for it, as text, never markup.</param>
/// <param name="Id">The model's real name in the wallet's style (<c>qwen/qwen3.5-4b</c>, <c>agent/claude-code-opus</c>); null when the visitor hid the names.</param>
/// <param name="Runs">Where it runs (<c>local</c>, <c>api-key</c>, <c>subscription</c>), as the wallet said it.</param>
/// <param name="Kind">"agent" when the wallet says so (<c>kind</c> of the grant's entry; an older wallet sends none, then when the model's id starts with <c>agent/</c>) (the wallet runs the visitor's own AI agent: it can search the web and may take minutes), else "model".</param>
/// <param name="Can">Verdicts for the conditions the site asked about in <c>needs</c> (<c>{"input":true}</c>), never values; null when the wallet said none.</param>
/// <param name="Abilities">For an agent entry, verdicts for the abilities the site asked about (<c>{"web.search":true}</c>); null when there are none.</param>
public sealed record ModelView(
    string Ref,
    string Name,
    string? Id,
    string Runs,
    string Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, bool>? Can = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, bool>? Abilities = null);

/// <summary>What the visitor allowed this site, as the wallet's hello (or a later <c>grant</c> frame) said.</summary>
/// <param name="Skills">The skills the visitor allowed, as the wallet's <c>grant.skills</c> said.</param>
/// <param name="Limits">What the site is held to (CONTRACT.md §3.4).</param>
/// <param name="Models">The participants of <c>text.generate</c>, a list as in 0.0.3: a page that reads <c>models</c> as a list keeps working.</param>
/// <param name="Capabilities">The feature names of the closed list the wallet supports (wallet 0.36); empty when it sent none.</param>
/// <param name="ModelsBySkill">Every skill's participants, one list per skill (<c>text.generate</c>, <c>image.generate</c>...), as the wallet sent them (CONTRACT.md §3.3); <c>Models</c> is its <c>text.generate</c> entry.</param>
public sealed record GrantView(string[] Skills, TextLimits Limits, ModelView[] Models, string[] Capabilities, IReadOnlyDictionary<string, ModelView[]> ModelsBySkill)
{
    /// <summary>The participants of one skill, in the visitor's order; empty when the wallet gave none for it.</summary>
    public ModelView[] ModelsOf(string skill) => ModelsBySkill.TryGetValue(skill, out var list) ? list : [];
}

/// <summary>
/// The site's side of the BYOA «direct» transport (CONTRACT-DIRECT.md §5): one-time pairing codes for the
/// <c>bringyourownagent://connect</c> link, the wallet connections that arrived with them, and their long-lived pairing tokens.
/// </summary>
/// <remarks>
/// Everything is in memory: a restart forgets the pairings (a wallet then hears «bad-session» and must be connected again), and no
/// message or answer text is ever written down. A <b>room</b> is one browser session: the page makes a long random id once and sends it
/// with every request (<see cref="IsRoomId"/> accepts only that shape). A code and a pairing belong to the room that asked for the code,
/// so one visitor's page can never reach another visitor's wallet — <see cref="Channel"/>, <see cref="Status"/> and <see cref="Release"/>
/// all refuse a pairing that is not this room's.
/// Codes: 192 random bits, URL-safe, single use, <see cref="RelayOptions.CodeLifeSeconds"/> (120 s), at most
/// <see cref="RelayOptions.CodesPerMinute"/> (10) a minute per visitor address, at most <see cref="RelayOptions.MaxCodes"/> alive.
/// Pairing tokens: 192 random bits. At most one live wallet connection per pairing: a new hello with the same token replaces the old one.
/// At most <see cref="RelayOptions.MaxPairings"/> (1000) pairings: a new one pushes out the oldest that has no wallet connected instead of
/// being refused; pairings nobody used for <see cref="RelayOptions.PairingIdleHours"/> are forgotten. The link carries the manifest itself
/// (base64url of its JSON, CONTRACT-DIRECT.md §7) and <c>welcome</c> echoes exactly the string the link carried.
/// </remarks>
public sealed class DirectHub
{
    static readonly TimeSpan UsedCodeKept = TimeSpan.FromMinutes(10);           // the page may still ask about it

    sealed class Code(string value, string room, DateTimeOffset expires, string manifest)
    {
        public string Value { get; } = value;
        public string Room { get; } = room;
        public DateTimeOffset Expires { get; } = expires;
        /// <summary>The manifest string this code's link carried (welcome echoes exactly this).</summary>
        public string Manifest { get; } = manifest;
        public bool Used, Cancelled;
        public string? Pairing;
    }

    sealed class Pairing(string token, string room)
    {
        public string Token { get; } = token;
        public string Room { get; } = room;
        public WalletChannel? Live;
        public DateTimeOffset LastUsed;
    }

    readonly object _gate = new();
    readonly Dictionary<string, Code> _codes = new(StringComparer.Ordinal);
    readonly Dictionary<string, Pairing> _pairings = new(StringComparer.Ordinal);
    readonly Dictionary<string, Queue<DateTimeOffset>> _asked = new(StringComparer.Ordinal);
    readonly TimeProvider _clock;

    /// <param name="options">The site's settings; <see cref="RelayOptions.ManifestJson"/> must be set.</param>
    /// <param name="clock">The clock codes and pairings age by (a test moves it by hand).</param>
    public DirectHub(RelayOptions options, TimeProvider? clock = null)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
        _clock = clock ?? TimeProvider.System;
        var manifest = options.ManifestJson ?? throw new InvalidOperationException("no BYOA manifest: set RelayOptions.ManifestJson (the links carry it)");
        Manifest = Encode(manifest, options.ConnectCheck, Math.Max(1, options.MaxManifestBytes));
    }

    /// <summary>What this hub was built with.</summary>
    public RelayOptions Options { get; }

    DateTimeOffset Now => _clock.GetUtcNow();

    /// <summary>How long a one-time code lives.</summary>
    public TimeSpan CodeLife => TimeSpan.FromSeconds(Math.Max(1, Options.CodeLifeSeconds));

    /// <summary>The manifest as the link carries it: base64url (no padding) of its compact JSON bytes.</summary>
    public string Manifest { get; }

    /// <summary>A room id is the page's own long random id and nothing else: URL-safe, 16 to 64 characters.</summary>
    public static bool IsRoomId(string? room) =>
        room is { Length: >= 16 and <= 64 } && room.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>Compact JSON, UTF-8, base64url without padding; refuses a manifest that is not a JSON object or is over
    /// <paramref name="maxBytes"/>. <paramref name="connectCheck"/> adds <c>"connect": { "check": true }</c>, which is what a site
    /// that shows the check code puts in its manifest (CONTRACT-DIRECT.md §3a).</summary>
    public static string Encode(string manifestJson, bool connectCheck = false, int maxBytes = 4096)
    {
        var node = JsonNode.Parse(manifestJson) as JsonObject ?? throw new InvalidOperationException("the BYOA manifest is not a JSON object");
        if (connectCheck)
        {
            var connect = node["connect"] as JsonObject ?? new JsonObject();
            connect["check"] = true;
            node["connect"] = connect;
        }
        var bytes = Encoding.UTF8.GetBytes(node.ToJsonString());
        if (bytes.Length > maxBytes) throw new InvalidOperationException($"the BYOA manifest is {bytes.Length} bytes, a link carries at most {maxBytes}");
        return Base64Url.EncodeToString(bytes);
    }

    /// <summary>A fresh secret: 192 random bits, URL-safe (no padding).</summary>
    public static string NewSecret() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(24));

    /// <summary>The wallet's endpoint on the host the page came from: <c>wss://host/byoa/ws</c> (behind a proxy: from
    /// X-Forwarded-Proto; locally <c>ws://</c>, which only the wallet's developer switch accepts).</summary>
    public string Endpoint(HttpRequest request) => $"{(request.IsHttps ? "wss" : "ws")}://{request.Host.ToUriComponent()}{Options.WalletPath}";

    /// <summary>The link the page opens: <c>bringyourownagent://connect?v=0.1&amp;endpoint=&lt;urlencoded wss URL&gt;&amp;manifest=&lt;base64url&gt;&amp;session=&lt;code&gt;</c>.</summary>
    public static string Link(string endpoint, string manifest, string code) =>
        $"bringyourownagent://connect?v=0.1&endpoint={Uri.EscapeDataString(endpoint)}&manifest={Uri.EscapeDataString(manifest)}&session={Uri.EscapeDataString(code)}";

    /// <summary>A new one-time code for this room's page and the link that carries it (built here, so the server knows exactly which
    /// manifest string it issued), with the check code the page shows while it waits; null when this address asked for too many in the
    /// last minute or the server holds too many codes already.</summary>
    public (string Code, string Link, string Check)? NewCode(string room, string? ip, string endpoint)
    {
        if (!IsRoomId(room)) return null;
        var now = Now;
        var key = IpKey.Of(ip);
        lock (_gate)
        {
            Sweep(now);
            if (!_asked.TryGetValue(key, out var times)) _asked[key] = times = new Queue<DateTimeOffset>();
            while (times.Count > 0 && now - times.Peek() >= TimeSpan.FromMinutes(1)) times.Dequeue();
            if (times.Count >= Math.Max(1, Options.CodesPerMinute) || _codes.Count >= Math.Max(1, Options.MaxCodes)) return null;
            times.Enqueue(now);
            var code = new Code(NewSecret(), room, now + CodeLife, Manifest);
            _codes[code.Value] = code;
            return (code.Value, Link(endpoint, code.Manifest, code.Value), ConnectCheck.Compute(code.Value));
        }
    }

    /// <summary>Has the wallet come with this code? "waiting", "connected" (with the pairing token, for the page to keep) or "expired"
    /// (unknown, cancelled, too old, or another room's).</summary>
    public (string State, string? Pairing) CodeState(string room, string? code)
    {
        lock (_gate)
        {
            if (code is null || !_codes.TryGetValue(code, out var c) || c.Room != room || c.Cancelled) return ("expired", null);
            if (c.Used) return c.Pairing is not null ? ("connected", c.Pairing) : ("expired", null);
            return Now < c.Expires ? ("waiting", null) : ("expired", null);
        }
    }

    /// <summary>The page stopped waiting: the code is dead (a wallet arriving with it later hears «bad-session»).</summary>
    public bool Cancel(string room, string? code)
    {
        lock (_gate)
        {
            if (code is null || !_codes.TryGetValue(code, out var c) || c.Room != room || c.Used) return false;
            c.Cancelled = true;
            return true;
        }
    }

    /// <summary>A wallet's hello: a fresh code (single use) or a known pairing token. Returns the pairing token and the manifest for «welcome»
    /// (with a code: exactly the string its link carried; with a pairing: the current manifest), or null for «bad-session».</summary>
    public (string Token, string Manifest)? Accept(WalletChannel channel, string? session, string? pairing)
    {
        var bye = new List<WalletChannel>();
        (string, string)? welcome = null;
        lock (_gate)
        {
            var now = Now;
            Sweep(now);
            if (session is not null)
            {
                if (_codes.TryGetValue(session, out var c) && !c.Used && !c.Cancelled && now < c.Expires && MakeRoom(bye))
                {
                    c.Used = true;
                    var p = new Pairing(NewSecret(), c.Room) { Live = channel, LastUsed = now };
                    _pairings[p.Token] = p;
                    c.Pairing = p.Token;
                    welcome = (p.Token, c.Manifest);
                }
            }
            else if (pairing is not null && _pairings.TryGetValue(pairing, out var p))
            {
                if (p.Live is not null && !ReferenceEquals(p.Live, channel)) bye.Add(p.Live);          // at most one connection per pairing: the newest wins
                p.Live = channel;
                p.LastUsed = now;
                welcome = (p.Token, Manifest);
            }
        }
        foreach (var old in bye) _ = old.ByeAsync();
        return welcome;
    }

    /// <summary>How many pairings the server keeps now.</summary>
    public int Pairings { get { lock (_gate) return _pairings.Count; } }

    // a full table makes room instead of refusing: the oldest pairing with no wallet connected goes first; only when every pairing has
    // a live wallet is the newcomer refused
    bool MakeRoom(List<WalletChannel> bye)
    {
        if (_pairings.Count < Math.Max(1, Options.MaxPairings)) return true;
        var victim = _pairings.Values.Where(p => p.Live is null).MinBy(p => p.LastUsed);
        if (victim is null) return false;
        _pairings.Remove(victim.Token);
        return true;
    }

    /// <summary>A wallet connection ended: the pairing stays, without a live connection, until it is used again or swept.</summary>
    public void Gone(WalletChannel channel)
    {
        lock (_gate)
        {
            foreach (var p in _pairings.Values)
            {
                if (!ReferenceEquals(p.Live, channel)) continue;
                p.Live = null;
                p.LastUsed = Now;
            }
        }
    }

    /// <summary>The live wallet connection of this room's pairing (null: unknown pairing, another room's pairing, or the wallet is away).</summary>
    public WalletChannel? Channel(string? room, string? pairing)
    {
        lock (_gate)
        {
            if (room is null || pairing is null || !_pairings.TryGetValue(pairing, out var p) || p.Room != room) return null;
            return p.Live;
        }
    }

    /// <summary>What the server knows about this room's pairing (asked by a page after a reload, before it asks anything of the wallet).</summary>
    public PairingState Status(string? room, string? pairing)
    {
        lock (_gate)
        {
            Sweep(Now);                                          // a page asking after a long absence learns «unknown», not «offline»
            if (room is null || pairing is null || !_pairings.TryGetValue(pairing, out var p) || p.Room != room) return PairingState.Unknown;
            return p.Live is null ? PairingState.Offline : PairingState.Live;
        }
    }

    /// <summary>The participants (one list per skill), the capabilities and the limits this room's wallet last told the server (null: no live wallet).</summary>
    public GrantView? Grant(string? room, string? pairing) => Channel(room, pairing)?.View();

    /// <summary>The page lets the wallet go for good: the connection is told «bye» and closed, the pairing is forgotten.</summary>
    public bool Release(string? room, string? pairing)
    {
        WalletChannel? live;
        lock (_gate)
        {
            if (room is null || pairing is null || !_pairings.TryGetValue(pairing, out var p) || p.Room != room) return false;
            _pairings.Remove(pairing);
            live = p.Live;
        }
        if (live is not null) _ = live.ByeAsync();
        return true;
    }

    // old codes and pairings nobody uses any more are forgotten, and the rate table does not grow without end
    void Sweep(DateTimeOffset now)
    {
        foreach (var c in _codes.Values.Where(c => c.Used ? now - c.Expires > UsedCodeKept : c.Cancelled || now >= c.Expires).ToList()) _codes.Remove(c.Value);
        var idle = TimeSpan.FromHours(Math.Max(1, Options.PairingIdleHours));
        foreach (var p in _pairings.Values.Where(p => p.Live is null && now - p.LastUsed > idle).ToList()) _pairings.Remove(p.Token);
        foreach (var k in _asked.Where(a => a.Value.Count == 0 || now - a.Value.Last() > TimeSpan.FromMinutes(2)).Select(a => a.Key).ToList()) _asked.Remove(k);
    }
}