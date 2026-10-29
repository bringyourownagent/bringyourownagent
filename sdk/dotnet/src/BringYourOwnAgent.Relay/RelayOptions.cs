using System;

namespace BringYourOwnAgent.Relay;

/// <summary>
/// What a site tells the relay when it adds it (<c>services.AddByoaRelay(options =&gt; { ... })</c>): the manifest its links carry, the
/// paths it answers on, and the limits it holds itself to. Every value has the default of the reference site, so a site that sets
/// nothing but <see cref="ManifestJson"/> behaves exactly like it.
/// </summary>
/// <remarks>
/// The caps here are the site's own, next to the visitor's grant: the site refuses what is over them before the wallet ever hears of it
/// (CONTRACT-DIRECT.md §5 «Trust»). They are never a promise about the wallet: the wallet clamps what it is given, and the limits of the
/// grant come back in <c>hello</c> and are what the site is really held to.
/// </remarks>
public sealed class RelayOptions
{
    /// <summary>
    /// The site's own manifest, as JSON text. The links carry it (CONTRACT-DIRECT.md §7), so the wallet needs no request of any kind
    /// before the visitor allows the site, and <c>welcome</c> echoes exactly the string the link carried. Required: a site that does
    /// not say who it is cannot ask anybody for anything.
    /// </summary>
    public string? ManifestJson { get; set; }

    /// <summary>
    /// Add <c>"connect": { "check": true }</c> to the manifest of the links: the site shows the connect check code while it waits
    /// (CONTRACT-DIRECT.md §3a), and the wallet then asks the visitor to compare it before Allow. The code itself is always computed
    /// and handed to the page by the code endpoint; this flag only tells the wallet that the page shows it. An existing
    /// <c>connect.check</c> in the manifest stays as it is when this is false.
    /// </summary>
    public bool ConnectCheck { get; set; }

    string _walletPath = "/byoa/ws";
    string _roomPathPrefix = "/api/r";
    string _relayPath = "byoa";

    /// <summary>Where the visitor's wallet dials in, as the link's <c>endpoint</c> says (CONTRACT-DIRECT.md §1).</summary>
    public string WalletPath
    {
        get => _walletPath;
        set => _walletPath = Absolute(value, "/byoa/ws");
    }

    /// <summary>The first part of the page's endpoints: <c>{RoomPathPrefix}/{room}/{RelayPath}/code</c> and the six next to it.</summary>
    public string RoomPathPrefix
    {
        get => _roomPathPrefix;
        set => _roomPathPrefix = Absolute(value, "/api/r");
    }

    /// <summary>The part of the page's endpoints that names the relay, after the room id.</summary>
    public string RelayPath
    {
        get => _relayPath;
        set => _relayPath = Segment(value, "byoa");
    }

    /// <summary>A path as the relay uses it: one leading slash, no trailing one; an empty value keeps the default.</summary>
    static string Absolute(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : "/" + value.Trim().Trim('/');

    /// <summary>One segment of a path: no slashes around it; an empty value keeps the default.</summary>
    static string Segment(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim().Trim('/');

    /// <summary>The longest manifest a link may carry, in decoded bytes (CONTRACT-DIRECT.md §7).</summary>
    public int MaxManifestBytes { get; set; } = 4096;

    /// <summary>The site's own cap on the characters of one call, the same number its manifest puts in <c>purpose.limits.maxTextChars</c>.</summary>
    public int MaxCallChars { get; set; } = 60_000;

    /// <summary>The longest answer the site keeps (CONTRACT.md §3.2: a wallet answers at most 128 000 characters).</summary>
    public int MaxAnswerChars { get; set; } = 128_000;

    /// <summary>Messages of one call (CONTRACT.md §3.2: the wallet caps them at 128).</summary>
    public int MaxCallMessages { get; set; } = 128;

    /// <summary>The site's own cap on <c>maxTokens</c> of one call, never above the grant's <c>maxAnswerTokens</c>.</summary>
    public int MaxCallTokens { get; set; } = 8_000;

    /// <summary>What a call asks for when the page names no <c>maxTokens</c>.</summary>
    public int DefaultCallTokens { get; set; } = 3000;

    /// <summary>What a call asks for when the page names no <c>temperature</c>.</summary>
    public double DefaultTemperature { get; set; } = 0.7;

    /// <summary>The most participants one grant is read for (CONTRACT.md §3.3: a site asks for a handful).</summary>
    public int MaxModels { get; set; } = 64;

    /// <summary>A job request bigger than this is refused before it is read (the page sends a chat call, never more).</summary>
    public int MaxJobRequestBytes { get; set; } = 1_500_000;

    /// <summary>How long a one-time code lives (CONTRACT-DIRECT.md §4: 120 seconds).</summary>
    public int CodeLifeSeconds { get; set; } = 120;

    /// <summary>How many codes one visitor address may ask for in a minute.</summary>
    public int CodesPerMinute { get; set; } = 10;

    /// <summary>The most codes the server holds at once.</summary>
    public int MaxCodes { get; set; } = 2000;

    /// <summary>The most pairings the server holds at once; a full table makes room instead of refusing (see <see cref="DirectHub"/>).</summary>
    public int MaxPairings { get; set; } = 1000;

    /// <summary>A pairing nobody used for this long is forgotten (CONTRACT-DIRECT.md §4 suggests a day).</summary>
    public int PairingIdleHours { get; set; } = 24;

    /// <summary>How long the site waits for one call before it counts it as «no answer».</summary>
    public int JobSeconds { get; set; } = 300;

    /// <summary>How many calls one page may start in a minute.</summary>
    public int JobsPerMinute { get; set; } = 30;

    /// <summary>How many answers one page may have waiting for it at once.</summary>
    public int MaxJobsPerRoom { get; set; } = 24;

    /// <summary>The most jobs the server holds at once.</summary>
    public int MaxJobs { get; set; } = 2000;

    /// <summary>From a wallet connection to its <c>hello</c>.</summary>
    public int HelloSeconds { get; set; } = 10;

    /// <summary>Between two messages of a wallet that was welcomed (the wallet pings every 30 s, so a silent one is gone).</summary>
    public int IdleSeconds { get; set; } = 75;

    /// <summary>From the first bytes of a message to its end (a message trickled in byte by byte does not hold a buffer for long).</summary>
    public int MessageSeconds { get; set; } = 30;

    /// <summary>For one message to the wallet to be written (a wallet that stopped reading is dropped, the call counts as a failure).</summary>
    public int SendTimeoutSeconds { get; set; } = 5;

    /// <summary>The web-socket timeouts as <see cref="WalletChannel"/> takes them.</summary>
    public WalletChannelLimits ChannelLimits => new(
        TimeSpan.FromSeconds(HelloSeconds),
        TimeSpan.FromSeconds(IdleSeconds),
        TimeSpan.FromSeconds(MessageSeconds),
        TimeSpan.FromSeconds(SendTimeoutSeconds));
}