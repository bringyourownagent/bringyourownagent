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
/// <param name="Capabilities">The feature names of the closed list the wallet supports (wallet 0.36); null when the wallet sent none (an older wallet: it cannot check <c>needs</c>), empty when it sent an empty list (it understands none).</param>
/// <param name="ModelsBySkill">Every skill's participants, one list per skill (<c>text.generate</c>, <c>image.generate</c>...), as the wallet sent them (CONTRACT.md §3.3); <c>Models</c> is its <c>text.generate</c> entry.</param>
public sealed record GrantView(string[] Skills, TextLimits Limits, ModelView[] Models, string[]? Capabilities, IReadOnlyDictionary<string, ModelView[]> ModelsBySkill)
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
/// Codes: 192 random bits, URL-safe, single use, <see cref="RelayOptions.CodeLifeSeconds"/> (600 s), at most
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
        // the hand-off address of CONTRACT-PAIR.md §2, when the site offers one: a token and a short code for the same link, one use between them
        public string? HandToken, HandShort;
        /// <summary>The link the hand-off releases; dropped as it is released, so that it is given out once.</summary>
        public string? HandLink;
        public DateTimeOffset HandExpires;
        public bool HandUsed;
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
    readonly Dictionary<string, Code> _handByToken = new(StringComparer.Ordinal);
    readonly Dictionary<string, Code> _handByShort = new(StringComparer.Ordinal);
    readonly Dictionary<string, Queue<DateTimeOffset>> _handWrong = new(StringComparer.Ordinal);       // wrong short codes, by visitor address
    readonly Queue<DateTimeOffset> _handWrongAll = new();                                               // wrong short codes, all addresses together
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
    /// last minute or the server holds too many codes already. No hand-off address: <see cref="Issue"/> makes one.</summary>
    public (string Code, string Link, string Check)? NewCode(string room, string? ip, string endpoint) =>
        Issue(room, ip, endpoint) is { } issued ? (issued.Code, issued.Link, issued.Check) : null;

    /// <summary>
    /// <see cref="NewCode"/> and, when <paramref name="handOffBase"/> is given (<c>https://host/byoa/hand</c>), the hand-off address of
    /// CONTRACT-PAIR.md §2 for the same code: <c>{handOffBase}/{token}</c> and the short code. The token is 192 random bits; the short
    /// code is 8 characters of the check alphabet. Both live <see cref="RelayOptions.HandOffLifeSeconds"/> (never longer than the code),
    /// are good for one use between them, and die with the code.
    /// </summary>
    public IssuedCode? Issue(string room, string? ip, string endpoint, string? handOffBase = null)
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
            var link = Link(endpoint, code.Manifest, code.Value);
            string? handoff = null, typed = null;
            if (handOffBase is not null)
            {
                code.HandLink = link;
                code.HandToken = NewSecret();
                code.HandShort = NewShortCode();
                var life = now + TimeSpan.FromSeconds(Math.Max(1, Options.HandOffLifeSeconds));
                code.HandExpires = life < code.Expires ? life : code.Expires;
                _handByToken[code.HandToken] = code;
                _handByShort[code.HandShort] = code;
                handoff = handOffBase.TrimEnd('/') + "/" + code.HandToken;
                typed = FormatShortCode(code.HandShort);
            }
            return new IssuedCode(code.Value, link, ConnectCheck.Compute(code.Value), handoff, typed);
        }
    }

    // 8 characters of the check alphabet (31 symbols, about 40 bits), not in use by another live short code
    string NewShortCode()
    {
        Span<char> chars = stackalloc char[ShortCodeLength];
        while (true)
        {
            for (var i = 0; i < chars.Length; i++) chars[i] = ConnectCheck.Alphabet[RandomNumberGenerator.GetInt32(ConnectCheck.Alphabet.Length)];
            var value = new string(chars);
            if (!_handByShort.ContainsKey(value)) return value;
        }
    }

    /// <summary>The length of a short code, without the hyphen.</summary>
    public const int ShortCodeLength = 8;

    /// <summary>A short code as it is shown and typed: <c>K7MQ-3XPZ</c>.</summary>
    public static string FormatShortCode(string eight) => eight.Length == ShortCodeLength ? eight[..4] + "-" + eight[4..] : eight;

    /// <summary>What a person typed, as the eight characters a short code is: capitals, no hyphen or spaces. Null when it cannot be a short code
    /// (wrong length, or a character that is not in the check alphabet).</summary>
    public static string? NormalizeShortCode(string? typed)
    {
        if (typed is null || typed.Length > 40) return null;
        var chars = new List<char>(ShortCodeLength);
        foreach (var c in typed)
        {
            if (c is '-' or ' ' or '‐' or '‑' or '–' or ' ') continue;
            var up = char.ToUpperInvariant(c);
            if (ConnectCheck.Alphabet.IndexOf(up) < 0) return null;
            chars.Add(up);
        }
        return chars.Count == ShortCodeLength ? new string(chars.ToArray()) : null;
    }

    /// <summary>
    /// A click on the hand-off page: swap a token (from the address) or a typed short code for the link, once (CONTRACT-PAIR.md §2). The
    /// token answers <see cref="HandOffOutcome.Used"/> when it was spent and <see cref="HandOffOutcome.Gone"/> when it is unknown or has
    /// died; a short code answers <see cref="HandOffOutcome.Wrong"/> for a wrong, a used and an expired code alike. More than
    /// <see cref="RelayOptions.HandOffWrongTriesPer10Minutes"/> wrong short codes from one address in ten minutes, or more than
    /// <see cref="RelayOptions.HandOffWrongTriesPerMinuteAll"/> in a minute from all, are <see cref="HandOffOutcome.Throttled"/>, whatever
    /// the code. The code stays valid after its link is released, so that the wallet can still connect.
    /// </summary>
    /// <param name="token">The token of the hand-off address, or null.</param>
    /// <param name="shortCode">What the visitor typed, or null. Used only when there is no token.</param>
    /// <param name="ip">The visitor's address, for the limit on wrong short codes.</param>
    public HandOffResult HandOffOpen(string? token, string? shortCode, string? ip)
    {
        var now = Now;
        var key = IpKey.Of(ip);
        lock (_gate)
        {
            Sweep(now);
            Code? code;
            if (token is not null)
            {
                if (!_handByToken.TryGetValue(token, out code)) return new HandOffResult(HandOffOutcome.Gone, null);
                if (code.HandUsed) return new HandOffResult(HandOffOutcome.Used, null);
                return HandAlive(code, now) ? ReleaseHandOff(code) : new HandOffResult(HandOffOutcome.Gone, null);
            }

            if (!_handWrong.TryGetValue(key, out var mine)) _handWrong[key] = mine = new Queue<DateTimeOffset>();
            while (mine.Count > 0 && now - mine.Peek() >= HandWrongWindow) mine.Dequeue();
            while (_handWrongAll.Count > 0 && now - _handWrongAll.Peek() >= TimeSpan.FromMinutes(1)) _handWrongAll.Dequeue();
            if (mine.Count >= Math.Max(1, Options.HandOffWrongTriesPer10Minutes) || _handWrongAll.Count >= Math.Max(1, Options.HandOffWrongTriesPerMinuteAll))
                return new HandOffResult(HandOffOutcome.Throttled, null);

            var normal = NormalizeShortCode(shortCode);
            if (normal is not null && _handByShort.TryGetValue(normal, out code) && HandAlive(code, now)) return ReleaseHandOff(code);
            mine.Enqueue(now);
            _handWrongAll.Enqueue(now);
            return new HandOffResult(HandOffOutcome.Wrong, null);
        }
    }

    static readonly TimeSpan HandWrongWindow = TimeSpan.FromMinutes(10);

    // alive: not spent, within its own 120 seconds, and its code still waiting for a wallet (a code that was cancelled, used or has expired takes the hand-off with it)
    static bool HandAlive(Code c, DateTimeOffset now) =>
        c.HandToken is not null && c.HandLink is not null && !c.HandUsed && now < c.HandExpires && !c.Used && !c.Cancelled && now < c.Expires;

    static HandOffResult ReleaseHandOff(Code c)
    {
        var link = c.HandLink;
        c.HandUsed = true;
        c.HandLink = null;                      // given out once; the code itself stays valid until its own expiry
        return new HandOffResult(HandOffOutcome.Released, link);
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
        foreach (var c in _codes.Values.Where(c => c.Used ? now - c.Expires > UsedCodeKept : c.Cancelled || now >= c.Expires).ToList())
        {
            _codes.Remove(c.Value);
            if (c.HandToken is not null) _handByToken.Remove(c.HandToken);
            if (c.HandShort is not null) _handByShort.Remove(c.HandShort);
        }
        foreach (var k in _handWrong.Where(a => a.Value.Count == 0 || now - a.Value.Last() >= HandWrongWindow).Select(a => a.Key).ToList()) _handWrong.Remove(k);
        var idle = TimeSpan.FromHours(Math.Max(1, Options.PairingIdleHours));
        foreach (var p in _pairings.Values.Where(p => p.Live is null && now - p.LastUsed > idle).ToList()) _pairings.Remove(p.Token);
        foreach (var k in _asked.Where(a => a.Value.Count == 0 || now - a.Value.Last() > TimeSpan.FromMinutes(2)).Select(a => a.Key).ToList()) _asked.Remove(k);
    }
}