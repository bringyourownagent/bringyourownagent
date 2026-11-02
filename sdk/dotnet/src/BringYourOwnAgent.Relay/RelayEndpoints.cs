using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.WebSockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BringYourOwnAgent.Relay;

/// <summary>What the page sends about one code or one pairing: <c>{"session": "..."}</c> or <c>{"pairing": "..."}</c>.</summary>
/// <param name="Session">The one-time code of the link the page opened.</param>
/// <param name="Pairing">The pairing token the server gave the page when the wallet connected.</param>
public sealed record RelayRequest(string? Session, string? Pairing);

/// <summary>What the page sends about one job: <c>{"id": "..."}</c>.</summary>
public sealed record JobPollRequest(string? Id);

/// <summary>
/// Adds the site's side of the BYOA «direct» transport (CONTRACT-DIRECT.md) to an ASP.NET Core site, in two lines:
/// <code>
/// builder.Services.AddByoaRelay(options =&gt; options.ManifestJson = File.ReadAllText("byoa-manifest.json"));
/// ...
/// app.UseWebSockets();
/// app.MapByoaRelay();
/// </code>
/// The wallet's channel is <see cref="RelayOptions.WalletPath"/> (<c>/byoa/ws</c>) and the page's endpoints are
/// <c>{RoomPathPrefix}/{room}/{RelayPath}/code</c> and the six next to it: <c>wait</c>, <c>cancel</c>, <c>status</c>, <c>release</c>,
/// <c>job</c>, <c>job/poll</c>, <c>job/cancel</c>.
/// </summary>
public static class RelayEndpoints
{
    /// <summary>
    /// Registers the relay with the site's dependency injection: the <see cref="DirectHub"/> (the codes, the pairings, the manifest)
    /// and the <see cref="WalletJobs"/> (the calls a page asks for). Both are singletons and hold everything in memory.
    /// </summary>
    /// <param name="services">The site's services.</param>
    /// <param name="configure">The site's settings; <see cref="RelayOptions.ManifestJson"/> must be set.</param>
    public static IServiceCollection AddByoaRelay(this IServiceCollection services, Action<RelayOptions> configure)
    {
        if (services is null) throw new ArgumentNullException(nameof(services));
        if (configure is null) throw new ArgumentNullException(nameof(configure));
        var options = new RelayOptions();
        configure(options);
        if (string.IsNullOrWhiteSpace(options.ManifestJson))
            throw new ArgumentException("the BYOA manifest is missing: the links carry it, so set RelayOptions.ManifestJson", nameof(configure));
        if (options.HandOff) CheckHandOff(options);
        services.AddSingleton(options);
        services.AddSingleton(_ => new DirectHub(options));
        services.AddSingleton(sp => new WalletJobs(sp.GetRequiredService<DirectHub>(), options));
        return services;
    }

    // a site that turns the hand-off on gets a mistake in its settings at start-up, not on the first visitor's phone
    static void CheckHandOff(RelayOptions options)
    {
        static bool Secure(string address) =>
            Uri.TryCreate(address, UriKind.Absolute, out var u) && string.IsNullOrEmpty(u.UserInfo) && string.IsNullOrEmpty(u.Fragment)
            && (u.Scheme == Uri.UriSchemeHttps || (u.Scheme == Uri.UriSchemeHttp && (u.Host is "localhost" or "127.0.0.1")));
        if (!string.IsNullOrWhiteSpace(options.HandOffBaseUrl) && !Secure(options.HandOffBaseUrl))
            throw new ArgumentException("RelayOptions.HandOffBaseUrl must be an https address (http only for localhost): the hand-off address is an https address", nameof(options));
        if (!string.IsNullOrWhiteSpace(options.HandOffWebWallet) && !Secure(options.HandOffWebWallet))
            throw new ArgumentException("RelayOptions.HandOffWebWallet must be an https address (http only for localhost)", nameof(options));
        _ = HandOffPage.ContentSecurityPolicy;           // the page's script and style are in the package: fail now if they are not
    }

    /// <summary>
    /// Maps the wallet's WebSocket and the page's endpoints. The site must call <c>app.UseWebSockets()</c> itself: the relay cannot
    /// add middleware to a pipeline that is already built.
    /// </summary>
    public static IEndpointRouteBuilder MapByoaRelay(this IEndpointRouteBuilder app)
    {
        if (app is null) throw new ArgumentNullException(nameof(app));
        var options = app.ServiceProvider.GetService<RelayOptions>()
            ?? throw new InvalidOperationException("the BYOA relay was not added: call services.AddByoaRelay(...) first");
        var prefix = options.RoomPathPrefix;                 // "/api/r"
        var relay = options.RelayPath;                       // "byoa"
        string Path(string rest) => $"{prefix}/{{room}}/{relay}/{rest}";

        // BYOA «direct» (CONTRACT-DIRECT.md): the visitor's agent wallet connects here by itself, only after the visitor allowed the site in it.
        app.Map(options.WalletPath, async (HttpContext ctx, DirectHub hub, ILogger<WalletChannel> log) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext { KeepAliveInterval = TimeSpan.FromSeconds(30) });
            await WalletChannel.RunAsync(ws, hub, log, ctx.RequestAborted, hub.Options.ChannelLimits);
        });

        // One room = one browser session: the page makes a long random id once and sends it with every request. A code and a pairing belong to
        // the room that asked for the code, so no page can reach another visitor's wallet. The shape is checked before anything else happens.
        static IResult? Bad(string room) => DirectHub.IsRoomId(room) ? null : Results.BadRequest(new { error = "bad-room" });
        IResult TooBig(HttpContext ctx) => ctx.Request.ContentLength is { } length && length > Math.Max(1, options.MaxJobRequestBytes)
            ? Results.StatusCode(StatusCodes.Status413PayloadTooLarge) : null!;

        // a one-time code for the bringyourownagent:// link (rate-limited per visitor address), with the check code the page shows; with
        // HandOff on, also the hand-off address and its short code (CONTRACT-PAIR.md §2), which a QR code can carry where the link cannot
        app.MapPost(Path("code"), (string room, HttpContext ctx, DirectHub hub) =>
        {
            if (Bad(room) is { } bad) return bad;
            if (hub.Issue(room, ClientIp(ctx), hub.Endpoint(ctx.Request), options.HandOff ? HandOffBase(options, ctx.Request) : null) is not { } issued)
                return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            var expiresIn = (int)hub.CodeLife.TotalSeconds;
            return issued.HandOff is null
                ? Results.Ok(new { session = issued.Code, link = issued.Link, check = issued.Check, expiresIn })
                : Results.Ok(new { session = issued.Code, link = issued.Link, check = issued.Check, expiresIn, handoff = issued.HandOff, @short = issued.Short });
        });

        // has the wallet come with that code? "waiting", "connected" (with the pairing token, the participants of every skill and the capabilities) or "expired"
        app.MapPost(Path("wait"), (string room, RelayRequest r, DirectHub hub) =>
        {
            if (Bad(room) is { } bad) return bad;
            var (state, pairing) = hub.CodeState(room, r.Session);
            var grant = pairing is null ? null : hub.Grant(room, pairing);
            return Results.Ok(new { state, pairing, models = grant?.Models, modelsBySkill = grant?.ModelsBySkill, limits = grant?.Limits, capabilities = grant?.Capabilities });
        });

        // the page stopped waiting: the code is dead
        app.MapPost(Path("cancel"), (string room, RelayRequest r, DirectHub hub) =>
        {
            if (Bad(room) is { } bad) return bad;
            return Results.Ok(new { cancelled = hub.Cancel(room, r.Session) });
        });

        // after a reload: is the wallet behind the stored pairing still connected, and who is in the chat now?
        app.MapPost(Path("status"), (string room, RelayRequest r, DirectHub hub) =>
        {
            if (Bad(room) is { } bad) return bad;
            var state = hub.Status(room, r.Pairing);
            var grant = hub.Grant(room, r.Pairing);
            return Results.Ok(new { state = state.ToString().ToLowerInvariant(), models = grant?.Models, modelsBySkill = grant?.ModelsBySkill, limits = grant?.Limits, skills = grant?.Skills, capabilities = grant?.Capabilities });
        });

        // the page lets the wallet go for good: the server says «bye», closes the connection and forgets the pairing
        app.MapPost(Path("release"), (string room, RelayRequest r, DirectHub hub) =>
        {
            if (Bad(room) is { } bad) return bad;
            return Results.Ok(new { released = hub.Release(room, r.Pairing) });
        });

        // one participant answers one question; the answer is fetched by asking again (a long call must not hold a request open behind a proxy)
        app.MapPost(Path("job"), async (string room, HttpContext ctx, WalletJobs jobs, CancellationToken ct) =>
        {
            if (Bad(room) is { } bad) return bad;
            if (TooBig(ctx) is { } big) return big;
            var (r, refusal) = await ReadJobAsync(ctx, options.MaxJobRequestBytes, ct);
            if (refusal is not null) return refusal;
            var started = jobs.Start(room, r!);
            return started.Id is null
                ? Results.Json(new { error = started.Error }, statusCode: started.Error == "busy" ? StatusCodes.Status429TooManyRequests : StatusCodes.Status400BadRequest)
                : Results.Ok(new { id = started.Id });
        });

        app.MapPost(Path("job/poll"), async (string room, JobPollRequest r, WalletJobs jobs, CancellationToken ct) =>
        {
            if (Bad(room) is { } bad) return bad;
            var result = await jobs.PollAsync(room, r.Id, TimeSpan.Zero, ct);
            var images = result.Images?.Select(p => new { mime = p.Mime, size = p.Size, sha256 = p.Sha256, data = p.Data, reduced = p.Reduced }).ToArray();
            return Results.Ok(new { state = result.State, ok = result.Ok, text = result.Text, partial = result.Partial, reason = result.Reason, lacks = result.Lacks, images });
        });

        // the page stops waiting for a job (the protocol has no cancel frame: the job is forgotten and a late answer is dropped)
        app.MapPost(Path("job/cancel"), (string room, JobPollRequest r, WalletJobs jobs) =>
        {
            if (Bad(room) is { } bad) return bad;
            return Results.Ok(new { cancelled = jobs.Cancel(room, r.Id) });
        });

        if (options.HandOff) MapHandOff(app, options);

        return app;
    }

    /// <summary>What the hand-off page posts: a token (the page opened from a QR code) or a short code (the page where it is typed).</summary>
    internal sealed record HandOffRequest(string? Token, string? Short);

    /// <summary>The address the hand-off tokens hang from: <c>https://host/byoa/hand</c>, the site's own host (the request's, or
    /// <see cref="RelayOptions.HandOffBaseUrl"/>), and <c>http</c> only for a request that was not secure.</summary>
    static string HandOffBase(RelayOptions options, HttpRequest request) =>
        (string.IsNullOrWhiteSpace(options.HandOffBaseUrl)
            ? $"{(request.IsHttps ? "https" : "http")}://{request.Host.ToUriComponent()}"
            : options.HandOffBaseUrl!.Trim().TrimEnd('/')) + options.HandOffPath;

    // The hand-off page and its endpoint (CONTRACT-PAIR.md §2). A GET reveals nothing: the same page for every address that looks like a token,
    // no link and no code in it, nothing of the request reflected. The link comes from the POST the page makes from a click, once.
    static void MapHandOff(IEndpointRouteBuilder app, RelayOptions options)
    {
        var path = options.HandOffPath;

        // never cached, never sent on in a Referer, never indexed; a framing page or a sniffed type is refused
        static void Private(HttpContext ctx)
        {
            var headers = ctx.Response.Headers;
            headers.CacheControl = "no-store";
            headers["Referrer-Policy"] = "no-referrer";
            headers["X-Robots-Tag"] = "noindex, nofollow";
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
        }

        IResult Page(HttpContext ctx, string mode)
        {
            Private(ctx);
            ctx.Response.Headers.ContentSecurityPolicy = HandOffPage.ContentSecurityPolicy;
            var labels = options.HandOffLabelsFor?.Invoke(ctx) ?? options.HandOffLabels;
            return Results.Content(HandOffPage.Render(labels, mode, path, options.HandOffWebWallet), "text/html; charset=utf-8");
        }

        app.MapGet(path, (HttpContext ctx) => Page(ctx, "short"));
        app.MapGet(path + "/{token}", (string token, HttpContext ctx) => IsTokenShape(token) ? Page(ctx, "token") : Results.NotFound());

        app.MapPost(path, async (HttpContext ctx, DirectHub hub, CancellationToken ct) =>
        {
            Private(ctx);
            if (!IsJson(ctx)) return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
            var (request, refusal) = await ReadSmallAsync<HandOffRequest>(ctx, 2048, ct);
            if (refusal is not null) return refusal;
            if (request is null || (request.Token is null && request.Short is null)) return Results.BadRequest(new { error = "bad-request" });
            var result = hub.HandOffOpen(request.Token, request.Short, ClientIp(ctx));
            return result.Outcome switch
            {
                HandOffOutcome.Released => Results.Ok(new { link = result.Link }),
                HandOffOutcome.Used => Results.Json(new { error = "used" }, statusCode: StatusCodes.Status410Gone),
                HandOffOutcome.Gone => Results.Json(new { error = "gone" }, statusCode: StatusCodes.Status410Gone),
                HandOffOutcome.Throttled => Results.Json(new { error = "slow" }, statusCode: StatusCodes.Status429TooManyRequests),
                _ => Results.Json(new { error = "wrong" }, statusCode: StatusCodes.Status404NotFound),
            };
        });
    }

    /// <summary>A token as <see cref="DirectHub.NewSecret"/> makes it: URL-safe characters, not too many. A page for an address of any other
    /// shape is a plain 404; nothing is looked up.</summary>
    static bool IsTokenShape(string? token) =>
        token is { Length: >= 16 and <= 128 } && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>A small JSON body: over <paramref name="max"/> bytes is a 413, anything that is not the shape is a 400.</summary>
    static async Task<(T? Body, IResult? Refusal)> ReadSmallAsync<T>(HttpContext ctx, int max, CancellationToken ct) where T : class
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[1024];
        while (true)
        {
            var read = await ctx.Request.Body.ReadAsync(chunk, ct);
            if (read == 0) break;
            if (buffer.Length + read > max) return (null, Results.StatusCode(StatusCodes.Status413PayloadTooLarge));
            buffer.Write(chunk, 0, read);
        }
        try { return (JsonSerializer.Deserialize<T>(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), JsonSerializerOptions.Web), null); }
        catch (JsonException) { return (null, Results.BadRequest(new { error = "bad-request" })); }
    }

    /// <summary>
    /// The body of a call, read here rather than by the framework so that the site's cap holds whatever a page sends: a body over
    /// <paramref name="max"/> bytes is refused with 413 before the server holds it in memory, whether it declared a length or was sent
    /// piece by piece (a body sent in chunks declares none, which is what a cap on the header alone would miss). Anything that is not
    /// the shape of a call is a bad request.
    /// </summary>
    static async Task<(JobRequest? Body, IResult? Refusal)> ReadJobAsync(HttpContext ctx, int max, CancellationToken ct)
    {
        if (!IsJson(ctx)) return (null, Results.StatusCode(StatusCodes.Status415UnsupportedMediaType));
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var read = await ctx.Request.Body.ReadAsync(chunk, ct);
            if (read == 0) break;
            if (buffer.Length + read > max) return (null, Results.StatusCode(StatusCodes.Status413PayloadTooLarge));
            buffer.Write(chunk, 0, read);
        }
        try { return (JsonSerializer.Deserialize<JobRequest>(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), JsonSerializerOptions.Web), null); }
        catch (JsonException) { return (null, Results.BadRequest(new { error = "bad-request" })); }
    }

    /// <summary>A call is JSON — <c>application/json</c>, <c>text/json</c> or <c>application/*+json</c>, the types the framework took
    /// before — and a body without a type is not one.</summary>
    static bool IsJson(HttpContext ctx)
    {
        var media = ctx.Request.ContentType?.Split(';')[0].Trim();
        return media is not null
            && (media.Equals("application/json", StringComparison.OrdinalIgnoreCase)
                || media.Equals("text/json", StringComparison.OrdinalIgnoreCase)
                || (media.StartsWith("application/", StringComparison.OrdinalIgnoreCase) && media.EndsWith("+json", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>The visitor's address behind the proxy this site runs behind (never a header the visitor could send himself):
    /// the site puts <c>app.UseForwardedHeaders(...)</c> in front, and this reads what is left on the connection.</summary>
    static string? ClientIp(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString();
}