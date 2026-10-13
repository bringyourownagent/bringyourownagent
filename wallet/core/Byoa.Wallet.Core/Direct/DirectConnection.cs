using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using Byoa.Wallet.Activity;
using Byoa.Wallet.Engine;
using Byoa.Wallet.Settings;

namespace Byoa.Wallet.Direct;

/// <summary>Timing and limits of the direct transport (CONTRACT-DIRECT.md §4); tests shorten them.</summary>
public sealed class DirectOptions
{
    public bool DevLocal { get; init; }

    /// <summary>Waits before reconnecting: 1, 2, 5, 10, then 30 s.</summary>
    public IReadOnlyList<TimeSpan> Backoff { get; init; } =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];

    public TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan WelcomeTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public int MaxCallsInFlight { get; init; } = 4;
    public int MaxFrameBytes { get; init; } = 4 * 1024 * 1024;
    public int MaxConnectionsPerOrigin { get; init; } = 4;

    /// <summary>How long a first connection keeps trying with its session code (the site keeps a code 120 s).</summary>
    public TimeSpan SessionRetryWindow { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Links acted on at once; more are ignored (a program flooding the wallet with links).</summary>
    public int MaxPendingLinks { get; init; } = 4;

    /// <summary>Name resolution for the address check (tests replace it); DNS by default.</summary>
    public Func<string, CancellationToken, Task<IPAddress[]>>? Resolve { get; init; }
}

public enum DirectState
{
    Connecting,
    Connected,
    Waiting,
    Closed,
}

/// <summary>
/// The wallet's own connection to one site's server after Allow: <c>hello</c> → <c>welcome</c> (or <c>error</c>),
/// then the site's <c>call</c> frames answered with <c>result</c> frames through exactly the engine's gate, limits,
/// routing, fallbacks and "ask before paid". A ping every 30 s; <c>bye</c> from either side ends it.
/// The welcome echoes the manifest: after a link it must be the link's, byte for byte (otherwise bye, and the link's
/// answer is dropped); only that echo stores the visitor's answer to the link. After a reconnect with the pairing the
/// welcome carries the site's current manifest, and if that asks for more than the site is allowed, the wallet says
/// bye and the site needs a new link and the visitor's consent.
/// A first connection that cannot reach the site tries again with backoff while its session code may still be valid
/// (60 s), until the site says the code is bad. Later, when the connection drops, it reconnects with backoff only
/// while the site keeps "decide on my own" and its pairing is stored.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DirectConnection
{
    enum Outcome
    {
        Stop,
        Dropped,
    }

    enum FrameKind
    {
        Json,
        Closed,
        Broken,
        TooBig,
        NotJson,
        Timeout,
    }

    readonly DirectTransport _owner;
    readonly CancellationTokenSource _stop = new();
    readonly SemaphoreSlim _send = new(1, 1);
    readonly TaskCompletionSource<bool> _welcomed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly Stopwatch _sinceLink = Stopwatch.StartNew();
    string? _session;
    string? _token;
    GrantSession? _grant;
    string? _provisional;                                // the grant of a link's answer that the site has not confirmed yet
    string? _stopReason;
    bool _toldFailure;
    int _inFlight;

    /// <param name="manifest">The manifest as base64url: the link's, or the one stored with the pairing.</param>
    internal DirectConnection(DirectTransport owner, string origin, Uri endpoint, string manifest, string? session, string? token, GrantSession? grant)
    {
        _owner = owner;
        Origin = origin;
        Endpoint = endpoint;
        Manifest = manifest;
        _session = session;
        _token = token;
        _grant = grant;
        // Only a link's answer that the engine holds for this grant is this connection's to drop; a silent grant has none.
        _provisional = session is not null && grant is not null && owner.Engine.HoldsLinkDecision(grant.Id) ? grant.Id : null;
    }

    public string Origin { get; }
    public Uri Endpoint { get; }

    /// <summary>The manifest this connection works under (base64url), as the link or the stored pairing carries it.</summary>
    public string Manifest { get; private set; }
    public DirectState State { get; private set; } = DirectState.Connecting;

    /// <summary>The pairing token from the site's welcome, or the stored one this connection resumes with.</summary>
    public string? Token => _token;

    /// <summary>The grant of a link's answer that this connection waits to have confirmed by the welcome, or null.</summary>
    internal string? AwaitingGrant => _provisional;

    /// <summary>True on the first welcome; false if the connection ended without one.</summary>
    public Task<bool> Welcomed => _welcomed.Task;

    /// <summary>Completes when the connection has ended for good (no more reconnects).</summary>
    public Task Completion { get; private set; } = Task.CompletedTask;

    /// <summary>Ends the connection: <c>bye</c>, close, and no reconnect.</summary>
    public void Stop(string why)
    {
        _stopReason ??= why;
        _stop.Cancel();
    }

    internal void Start() => Completion = Task.Run(RunAsync);

    DirectOptions Options => _owner.Options;
    DateTimeOffset Now => _owner.Now;

    async Task RunAsync()
    {
        var ct = _stop.Token;
        var attempt = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (_grant is null || _owner.Engine.FindGrant(_grant.Id) is null)
                {
                    // A grant that ended (12 hours, a restart of the wallet) is renewed only for a stored pairing,
                    // by the same rule as a page's request without a click: silently, or not at all.
                    if (!CanResume()) break;
                    var (grant, retry) = await _owner.RenewGrantAsync(this, ct).ConfigureAwait(false);
                    if (grant is null)
                    {
                        if (!retry) break;
                        await DelayAsync(attempt++, ct).ConfigureAwait(false);
                        continue;
                    }

                    _grant = grant;
                }

                var (outcome, welcomed) = await SessionAsync(ct).ConfigureAwait(false);
                if (welcomed) attempt = 0;
                if (outcome == Outcome.Stop) break;
                if (_session is not null)
                {
                    // The first connection with the link's code: try again while the code may still be valid.
                    if (_sinceLink.Elapsed >= Options.SessionRetryWindow)
                    {
                        Record("failed", "the site could not be reached within " + (int)Options.SessionRetryWindow.TotalSeconds + " s of the link");
                        break;
                    }

                    SetState(DirectState.Waiting);
                    await DelayAsync(attempt++, ct).ConfigureAwait(false);
                    continue;
                }

                if (!CanResume()) break;
                SetState(DirectState.Waiting);
                await DelayAsync(attempt++, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // stopped while waiting to reconnect
        }
        catch (Exception e)
        {
            Record("closed", "the connection ended: " + e.GetType().Name);
        }
        finally
        {
            // A link's answer that the site never confirmed is dropped together with its grant.
            if (_provisional is not null) _owner.Engine.DropLinkDecision(_provisional);
            SetState(DirectState.Closed);
            _welcomed.TrySetResult(false);
            _owner.Remove(this);
        }
    }

    Task DelayAsync(int attempt, CancellationToken ct)
    {
        var steps = Options.Backoff;
        return Task.Delay(steps[Math.Min(attempt, steps.Count - 1)], ct);
    }

    /// <summary>Reconnecting is allowed while this connection's pairing is the stored one and the site keeps "decide on my own".</summary>
    bool CanResume()
    {
        if (_stop.IsCancellationRequested || _token is null) return false;
        var stored = _owner.Pairings.Get(Origin);
        if (stored is null || stored.Token != _token) return false;
        if (_owner.Engine.PauseSiteIfDue(Origin)) return false;
        if (_owner.Engine.DecidesOnItsOwn(Origin)) return true;          // not while the site is paused
        _owner.Pairings.Forget(Origin, _token);
        return false;
    }

    async Task<(Outcome Outcome, bool Welcomed)> SessionAsync(CancellationToken ct)
    {
        SetState(DirectState.Connecting);
        using var ws = new ClientWebSocket();
        // .NET's keep-alive sends WebSocket pings and ends a connection whose peer stopped answering them.
        ws.Options.KeepAliveInterval = Options.PingInterval;
        ws.Options.KeepAliveTimeout = Options.PingInterval;
        var resumed = _session is null;
        try
        {
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connect.CancelAfter(Options.ConnectTimeout);
            await ws.ConnectAsync(Endpoint, _owner.Net.Invoker, connect.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            if (DirectException.IsPermanent(e))
            {
                // The wallet's own refusal (a private address): no point in trying again.
                Record("failed", DirectException.Reason(e));
                return (Outcome.Stop, false);
            }

            if (!_toldFailure) Record("failed", "could not connect: " + DirectException.Reason(e) + (resumed ? "" : "; trying again"));
            _toldFailure = true;
            return (Outcome.Dropped, false);
        }

        var hello = Hello();
        using var calls = new CancellationTokenSource();
        try
        {
            await SendAsync(ws, hello).ConfigureAwait(false);
            var (kind, first) = await ReceiveAsync(ws, Options.WelcomeTimeout, ct).ConfigureAwait(false);
            if (kind != FrameKind.Json)
            {
                Record("closed", "no welcome from the site (" + Describe(kind) + ")");
                await AbandonAsync(ws, kind).ConfigureAwait(false);
                return (kind is FrameKind.Closed or FrameKind.Broken or FrameKind.Timeout && resumed ? Outcome.Dropped : Outcome.Stop, false);
            }

            var type = Str(first!["type"]);
            if (type == "error")
            {
                var code = Code(first["code"]);
                _session = null;                                 // an explicit answer: the code is spent or bad
                Record("error", "the site refused the connection: " + code);
                if (resumed && _token is not null) _owner.Pairings.Forget(Origin, _token);
                await CloseAsync(ws, WebSocketCloseStatus.NormalClosure).ConfigureAwait(false);
                return (Outcome.Stop, false);
            }

            if (type != "welcome")
            {
                Record("closed", "the site spoke before its welcome");
                await CloseAsync(ws, WebSocketCloseStatus.ProtocolError).ConfigureAwait(false);
                return (Outcome.Stop, false);
            }

            _session = null;                                     // a session code is used once; later hellos use the pairing
            var echoed = Str(first["manifest"]);
            if (!resumed && echoed != Manifest)
            {
                // Another manifest than the link's: this server did not issue the link. The link's answer is dropped (the
                // finally of the connection), and the site's stored grant and pairing stay: any page can send a link that
                // names any site, so a link must not be able to revoke one.
                Record("error", "the site's manifest differs from its link");
                await ByeAsync(ws).ConfigureAwait(false);
                return (Outcome.Stop, false);
            }

            if (!resumed)
            {
                // The site echoed the link's manifest, so it issued the link: the visitor's answer to it is stored now. If the
                // visitor decided about the site meanwhile, the answer is gone and so is the grant: nothing is served on it.
                if (_provisional is not null && !_owner.Engine.ConfirmLinkDecision(_provisional))
                {
                    Record("closed", "the visitor decided about the site before the welcome");
                    await ByeAsync(ws).ConfigureAwait(false);
                    return (Outcome.Stop, false);
                }

                _provisional = null;
            }

            if (resumed && echoed != Manifest)
            {
                // After a reconnect the welcome carries the site's current manifest: within the grant it is taken
                // silently; asking for more needs a new link and the visitor's consent (showing only the difference).
                var current = !_owner.Engine.PauseSiteIfDue(Origin)
                    && DirectLink.TryManifest(echoed, out var json, out _) && _owner.Engine.CoveredByGrant(Origin, json!)
                    ? await _owner.GrantSilentlyAsync(Origin, json!, ct).ConfigureAwait(false)
                    : null;
                if (current is null)
                {
                    Record("closed", echoed is null
                        ? "the site's welcome carries no manifest"
                        : "the site now asks for more than it was allowed: a new link and your consent are needed");
                    await ByeAsync(ws).ConfigureAwait(false);
                    if (_token is not null) _owner.Pairings.Forget(Origin, _token);
                    return (Outcome.Stop, false);
                }

                _grant = current;
                Manifest = echoed!;
            }

            var token = Str(first["pairing"]);
            if (token is not null && IsToken(token))
            {
                _token = token;
                // The pairing is kept only for a site with "decide on my own": only then may the wallet come back on its own.
                if (_owner.Engine.DecidesOnItsOwn(Origin))
                {
                    _owner.Pairings.Set(new Pairing(Origin, Endpoint.ToString(), Manifest, token, Now));
                }
            }

            _toldFailure = false;
            SetState(DirectState.Connected);
            Record("connected", resumed ? "reconnected with the stored pairing" : "paired after the link");
            _welcomed.TrySetResult(true);
            return (await ChannelAsync(ws, calls, ct).ConfigureAwait(false), true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Record("closed", _stopReason ?? "closed by the wallet");
            await ByeAsync(ws).ConfigureAwait(false);
            return (Outcome.Stop, false);
        }
        catch (Exception e) when (e is WebSocketException or IOException or ObjectDisposedException)
        {
            Record("closed", "the connection broke");
            return (Outcome.Dropped, false);
        }
        finally
        {
            calls.Cancel();
        }
    }

    /// <summary>The site's frames after the welcome, until either side ends the connection.</summary>
    async Task<Outcome> ChannelAsync(ClientWebSocket ws, CancellationTokenSource calls, CancellationToken ct)
    {
        var grant = _grant!;
        var untilExpiry = grant.ExpiresAt - Now;
        using var expired = new CancellationTokenSource(untilExpiry > TimeSpan.Zero ? untilExpiry : TimeSpan.Zero);
        using var ending = CancellationTokenSource.CreateLinkedTokenSource(ct, expired.Token);
        // When the wallet ends the connection (Stop, revoke, the grant's end), it says bye and closes while the
        // receive below is still waiting; the site's close then ends the loop.
        using var onEnd = ending.Token.Register(() => _ = ByeAsync(ws));
        using var pings = CancellationTokenSource.CreateLinkedTokenSource(ending.Token);
        var pinger = PingAsync(ws, pings.Token);
        try
        {
            while (true)
            {
                var (kind, frame) = await ReceiveAsync(ws, Timeout.InfiniteTimeSpan, CancellationToken.None).ConfigureAwait(false);
                if (ct.IsCancellationRequested)
                {
                    Record("closed", _stopReason ?? "closed by the wallet");
                    return Outcome.Stop;
                }

                if (expired.IsCancellationRequested)
                {
                    Record("closed", "the grant ended (12 hours)");
                    _grant = null;
                    return Outcome.Dropped;
                }

                switch (kind)
                {
                    case FrameKind.Json:
                        break;
                    case FrameKind.Closed:
                        Record("closed", "the site closed the connection");
                        await CloseAsync(ws, WebSocketCloseStatus.NormalClosure).ConfigureAwait(false);
                        return Outcome.Dropped;
                    case FrameKind.Broken:
                        Record("closed", "the connection broke");
                        return Outcome.Dropped;
                    default:
                        Record("closed", "the site sent " + Describe(kind));
                        await AbandonAsync(ws, kind).ConfigureAwait(false);
                        return Outcome.Stop;
                }

                switch (Str(frame!["type"]))
                {
                    case "call":
                        OnCall(ws, frame, calls.Token);
                        break;
                    case "bye":
                        Record("closed", "the site said bye");
                        await CloseAsync(ws, WebSocketCloseStatus.NormalClosure).ConfigureAwait(false);
                        return Outcome.Stop;
                    case "error":
                        Record("error", "the site ended the connection: " + Code(frame["code"]));
                        await CloseAsync(ws, WebSocketCloseStatus.NormalClosure).ConfigureAwait(false);
                        return Outcome.Stop;
                    default:
                        // unknown frames (pong, future types) are ignored
                        break;
                }
            }
        }
        finally
        {
            calls.Cancel();
            pings.Cancel();
            try
            {
                await pinger.ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or WebSocketException or ObjectDisposedException or IOException or InvalidOperationException)
            {
                // the pings end with the connection
            }
        }
    }

    void OnCall(ClientWebSocket ws, JsonObject frame, CancellationToken ct)
    {
        // A call without a usable id cannot be answered; it is dropped.
        if (frame["id"] is not JsonValue id || !(id.TryGetValue<double>(out _) || (id.TryGetValue<string>(out var s) && s.Length <= 64))) return;
        if (Interlocked.Increment(ref _inFlight) > Options.MaxCallsInFlight)
        {
            Interlocked.Decrement(ref _inFlight);
            _owner.Engine.Record(new ActivityEntry
            {
                Origin = Origin, Kind = "call", Via = WalletEngine.ViaLink, Skill = Str(frame["skill"]), Outcome = "limit",
                Note = "more than " + Options.MaxCallsInFlight + " calls at once on the connection",
            });
            _ = SendQuietAsync(ws, Result(id, new JsonObject { ["ok"] = false }));
            return;
        }

        var payload = new JsonObject
        {
            ["grantId"] = _grant!.Id,
            ["skill"] = frame["skill"]?.DeepClone(),
            ["input"] = frame["input"]?.DeepClone(),
            ["params"] = frame["params"]?.DeepClone(),
        };
        var answerId = id.DeepClone();
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await _owner.Engine.CallViaLinkAsync(Origin, payload, ct).ConfigureAwait(false) as JsonObject ?? new JsonObject { ["ok"] = false };
                await SendQuietAsync(ws, Result(answerId, result)).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }, CancellationToken.None);
    }

    static JsonObject Result(JsonNode id, JsonObject result)
    {
        var frame = new JsonObject { ["type"] = "result", ["id"] = id.DeepClone() };
        foreach (var (key, value) in result) frame[key] = value?.DeepClone();
        return frame;
    }

    JsonObject Hello()
    {
        var grant = _grant!;
        var skills = new JsonArray();
        foreach (var skill in grant.Skills) skills.Add(skill);
        return new JsonObject
        {
            ["type"] = "hello",
            ["v"] = "0.1",
            ["session"] = _session,
            ["pairing"] = _session is null ? _token : null,
            ["wallet"] = new JsonObject { ["name"] = "Agent wallet", ["version"] = "0.1" },
            ["grant"] = new JsonObject { ["skills"] = skills, ["expiresAt"] = GrantSession.Time(grant.ExpiresAt) },
        };
    }

    async Task PingAsync(ClientWebSocket ws, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(Options.PingInterval, ct).ConfigureAwait(false);
            await SendAsync(ws, new JsonObject { ["type"] = "ping" }).ConfigureAwait(false);
        }
    }

    async Task SendAsync(ClientWebSocket ws, JsonObject frame)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(frame);
        await _send.WaitAsync().ConfigureAwait(false);
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, limit.Token).ConfigureAwait(false);
        }
        finally
        {
            _send.Release();
        }
    }

    async Task SendQuietAsync(ClientWebSocket ws, JsonObject frame)
    {
        try
        {
            await SendAsync(ws, frame).ConfigureAwait(false);
        }
        catch (Exception e) when (e is WebSocketException or IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
            // the connection ended; the site gets no answer for this call
        }
    }

    /// <summary>Says bye and closes from the wallet's side; the site's close (or a short wait) finishes it.</summary>
    async Task ByeAsync(ClientWebSocket ws)
    {
        try
        {
            if (ws.State != WebSocketState.Open) return;
            await SendAsync(ws, new JsonObject { ["type"] = "bye" }).ConfigureAwait(false);
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", limit.Token).ConfigureAwait(false);
            _ = Task.Delay(TimeSpan.FromSeconds(3)).ContinueWith(_ => AbortQuietly(ws), TaskScheduler.Default);
        }
        catch (Exception e) when (e is WebSocketException or IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
            ws.Abort();
        }
    }

    static void AbortQuietly(ClientWebSocket ws)
    {
        try
        {
            ws.Abort();
        }
        catch (ObjectDisposedException)
        {
            // already gone
        }
    }

    static async Task CloseAsync(ClientWebSocket ws, WebSocketCloseStatus status)
    {
        try
        {
            if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await ws.CloseAsync(status, null, limit.Token).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is WebSocketException or IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
            ws.Abort();
        }
    }

    static Task AbandonAsync(ClientWebSocket ws, FrameKind kind) => kind switch
    {
        FrameKind.TooBig => CloseAsync(ws, WebSocketCloseStatus.MessageTooBig),
        FrameKind.NotJson => CloseAsync(ws, WebSocketCloseStatus.InvalidPayloadData),
        FrameKind.Closed => CloseAsync(ws, WebSocketCloseStatus.NormalClosure),
        _ => Task.Run(ws.Abort),
    };

    async Task<(FrameKind Kind, JsonObject? Frame)> ReceiveAsync(ClientWebSocket ws, TimeSpan timeout, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout != Timeout.InfiniteTimeSpan) limit.CancelAfter(timeout);
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (true)
            {
                var r = await ws.ReceiveAsync(buffer, limit.Token).ConfigureAwait(false);
                if (r.MessageType == WebSocketMessageType.Close) return (FrameKind.Closed, null);
                if (r.MessageType == WebSocketMessageType.Binary) return (FrameKind.NotJson, null);
                if (message.Length + r.Count > Options.MaxFrameBytes) return (FrameKind.TooBig, null);
                message.Write(buffer, 0, r.Count);
                if (r.EndOfMessage) break;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (FrameKind.Timeout, null);
        }
        catch (Exception e) when (e is WebSocketException or IOException or ObjectDisposedException)
        {
            return (FrameKind.Broken, null);
        }

        try
        {
            return JsonNode.Parse(message.GetBuffer().AsSpan(0, (int)message.Length)) is JsonObject o && Str(o["type"]) is not null
                ? (FrameKind.Json, o)
                : (FrameKind.NotJson, null);
        }
        catch (JsonException)
        {
            return (FrameKind.NotJson, null);
        }
    }

    static string Describe(FrameKind kind) => kind switch
    {
        FrameKind.TooBig => "a frame over 4 MB",
        FrameKind.NotJson => "a frame that is not a JSON message",
        FrameKind.Closed => "a close",
        FrameKind.Timeout => "no answer in time",
        _ => "a broken connection",
    };

    /// <summary>A pairing token: 22 to 1024 URL-safe characters (opaque to the wallet).</summary>
    internal static bool IsToken(string? token) => token is not null && token.Length is >= 22 and <= 1024
        && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    static string Code(JsonNode? node)
    {
        var code = Str(node) ?? "unknown";
        return code.Length <= 40 && code.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.') ? code : "unknown";
    }

    static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    void Record(string outcome, string note) => _owner.Record(Origin, outcome, note);

    void SetState(DirectState state)
    {
        if (State == state) return;
        State = state;
        _owner.OnChanged();
    }
}
