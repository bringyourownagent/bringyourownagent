using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using Byoa.Wallet.Activity;
using Byoa.Wallet.Engine;
using Byoa.Wallet.Settings;

namespace Byoa.Wallet.Direct;

/// <summary>
/// The direct transport (CONTRACT-DIRECT.md with its §7): a <c>bringyourownagent://connect</c> link launches the
/// wallet; it reads the site's manifest from the link itself, asks the visitor with the same notice and consent as
/// for a page's request, and only after Allow opens its own WebSocket to the site's server. Before Allow the wallet
/// makes no network request of any kind, so a site cannot tell a declined link from a visitor without a wallet. Grants, limits, blocks and "decide on my own" are the
/// engine's, shared with the extension transport for the same origin. A link of a blocked site, a bad link and a
/// repeated link do nothing but leave an activity entry.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DirectTransport : IDisposable
{
    static readonly TimeSpan RememberLinks = TimeSpan.FromMinutes(10);

    readonly object _gate = new();
    readonly List<DirectConnection> _connections = [];
    readonly Dictionary<string, DateTimeOffset> _seenSessions = new(StringComparer.Ordinal);
    readonly TimeProvider _time;
    int _pendingLinks;
    bool _disposed;

    public DirectTransport(WalletEngine engine, PairingStore pairings, DirectOptions? options = null, TimeProvider? time = null)
    {
        Engine = engine;
        Pairings = pairings;
        Options = options ?? new DirectOptions();
        _time = time ?? TimeProvider.System;
        Net = new DirectNet(Options.DevLocal, Options.Resolve);
        engine.SiteClosed += OnSiteClosed;
        engine.LinkAnswersDropped += OnLinkAnswersDropped;
    }

    public WalletEngine Engine { get; }
    public PairingStore Pairings { get; }
    public DirectOptions Options { get; }
    internal DirectNet Net { get; }
    internal DateTimeOffset Now => _time.GetUtcNow();

    /// <summary>Raised when a connection opens, closes or changes state.</summary>
    public event Action? Changed;

    public IReadOnlyList<DirectConnection> Connections
    {
        get
        {
            lock (_gate) return _connections.ToList();
        }
    }

    public bool IsConnected(string origin) => Connections.Any(c => c.Origin == origin && c.State == DirectState.Connected);

    /// <summary>
    /// Acts on a link: checks it and its manifest, asks the visitor (or grants silently under "decide on my own"),
    /// and after a grant starts the connection. Returns the connection, or null when nothing was opened.
    /// </summary>
    public async Task<DirectConnection?> HandleLinkAsync(string? url, CancellationToken ct = default, bool userActivation = true)
    {
        if (Interlocked.Increment(ref _pendingLinks) > Options.MaxPendingLinks)
        {
            Interlocked.Decrement(ref _pendingLinks);
            Record("", "ignored", "too many links at once");
            return null;
        }

        try
        {
            return await ActOnLinkAsync(url, userActivation, ct).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _pendingLinks);
        }
    }

    async Task<DirectConnection?> ActOnLinkAsync(string? url, bool userActivation, CancellationToken ct)
    {
        if (!DirectLink.TryParse(url, Options.DevLocal, out var link, out var why))
        {
            Record("", "invalid", "a link was ignored: " + why);
            return null;
        }

        var origin = link!.Origin;
        lock (_gate)
        {
            var now = Now;
            foreach (var old in _seenSessions.Where(kv => now - kv.Value > RememberLinks).Select(kv => kv.Key).ToList()) _seenSessions.Remove(old);
            if (!_seenSessions.TryAdd(link.Session, now))
            {
                Record(origin, "ignored", "the same link again");
                return null;
            }
        }

        // A blocked site's link does nothing at all: no notice, no connection.
        if (Engine.FindSite(origin) is { Status: SiteStatus.Blocked })
        {
            Record(origin, "blocked", "blocked site: the link was ignored");
            return null;
        }

        // Keep the browser's activation bit from the hand-off. The manifest is the link's own: nothing has been sent
        // to the site so far, and a link with no click must stay a quiet request.
        var answer = await Engine.RequestViaLinkAsync(origin, link.ManifestJson, userActivation, ct).ConfigureAwait(false);
        if (GrantOf(answer) is not { } grant) return null;
        var connection = new DirectConnection(this, origin, link.Endpoint, link.Manifest, link.Session, token: null, grant);
        if (Add(connection)) return connection;
        Engine.DropLinkDecision(grant.Id);                  // the wallet is closing: the link's answer and its grant go unused
        return null;
    }

    /// <summary>At start: reconnects to the sites whose pairing is stored and that keep "decide on my own".</summary>
    public void ResumePairings()
    {
        foreach (var pairing in Pairings.All())
        {
            if (Engine.PauseSiteIfDue(pairing.Origin)
                || !Engine.DecidesOnItsOwn(pairing.Origin)
                || !DirectLink.TryTarget(pairing.Endpoint, Options.DevLocal, out var endpoint, out var origin, out _, out _)
                || origin != pairing.Origin
                || !DirectLink.TryManifest(pairing.Manifest, out _, out _))
            {
                Pairings.Forget(pairing.Origin, pairing.Token);
                continue;
            }

            if (Connections.Any(c => c.Origin == pairing.Origin && c.Token == pairing.Token)) continue;
            Add(new DirectConnection(this, pairing.Origin, endpoint!, pairing.Manifest, session: null, pairing.Token, grant: null));
        }
    }

    /// <summary>A new grant for a stored pairing, from the manifest stored with it, by the silent rule (no network).</summary>
    internal async Task<(GrantSession? Grant, bool Retry)> RenewGrantAsync(DirectConnection connection, CancellationToken ct)
    {
        if (Engine.PauseSiteIfDue(connection.Origin)) return (null, false);
        if (!DirectLink.TryManifest(connection.Manifest, out var manifest, out _)) return (null, false);
        return (await GrantSilentlyAsync(connection.Origin, manifest!, ct).ConfigureAwait(false), false);
    }

    /// <summary>A grant by "decide on my own" alone (no notice): null when the site is not covered or not on its own.</summary>
    internal async Task<GrantSession?> GrantSilentlyAsync(string origin, JsonObject manifest, CancellationToken ct)
    {
        var answer = await Engine.RequestViaLinkAsync(origin, manifest, userActivation: false, ct).ConfigureAwait(false);
        return GrantOf(answer);
    }

    GrantSession? GrantOf(JsonNode? answer) =>
        answer is JsonObject o && o["grantId"] is JsonValue v && v.TryGetValue<string>(out var id) ? Engine.FindGrant(id) : null;

    /// <summary>Takes a connection and starts it. False, and nothing started, once the transport is disposed.</summary>
    bool Add(DirectConnection connection)
    {
        List<DirectConnection> replaced;
        lock (_gate)
        {
            if (_disposed) return false;
            var same = _connections.Where(c => c.Origin == connection.Origin).ToList();
            replaced = same.Take(Math.Max(0, same.Count - Options.MaxConnectionsPerOrigin + 1)).ToList();
            _connections.Add(connection);
        }

        foreach (var old in replaced) old.Stop("replaced by a newer connection to the same site");
        connection.Start();
        OnChanged();
        return true;
    }

    internal void Remove(DirectConnection connection)
    {
        lock (_gate) _connections.Remove(connection);
        OnChanged();
    }

    /// <summary>Revoke and Block: the site's connections close (with bye) and its pairing is forgotten.</summary>
    void OnSiteClosed(string origin)
    {
        Pairings.Forget(origin);
        foreach (var c in Connections.Where(c => c.Origin == origin)) c.Stop("the visitor changed the site in the wallet");
    }

    /// <summary>A newer decision dropped link answers that waited for their welcomes: only those link connections end.</summary>
    void OnLinkAnswersDropped(IReadOnlyList<string> grantIds)
    {
        foreach (var c in Connections.Where(c => c.AwaitingGrant is { } id && grantIds.Contains(id))) c.Stop("the visitor decided about the site in the wallet");
    }

    internal void Record(string origin, string outcome, string note) =>
        Engine.Record(new ActivityEntry { Origin = origin, Kind = "link", Via = WalletEngine.ViaLink, Outcome = outcome, Note = note });

    internal void OnChanged() => Changed?.Invoke();

    /// <summary>Closes every connection (bye) and waits a moment for them to finish.</summary>
    public void Dispose()
    {
        List<DirectConnection> all;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            all = _connections.ToList();
        }

        Engine.SiteClosed -= OnSiteClosed;
        Engine.LinkAnswersDropped -= OnLinkAnswersDropped;
        foreach (var c in all) c.Stop("the wallet is closing");
        try
        {
            Task.WhenAll(all.Select(c => c.Completion)).Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException)
        {
            // they are closing anyway
        }

        Net.Dispose();
    }
}
