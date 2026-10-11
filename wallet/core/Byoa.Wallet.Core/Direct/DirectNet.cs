using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Byoa.Wallet.Protocol;

namespace Byoa.Wallet.Direct;

/// <summary>A refusal or failure of the direct transport, with a short reason for the activity log.</summary>
public sealed class DirectException(string message, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>The reason from the exception or from inside it (HttpClient and ClientWebSocket wrap it).</summary>
    public static string Reason(Exception e)
    {
        for (var x = e; x is not null; x = x.InnerException)
        {
            if (x is DirectException d) return d.Message;
        }

        return e switch
        {
            OperationCanceledException => "took too long",
            HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError } => "the site's TLS certificate was not accepted",
            HttpRequestException or System.Net.WebSockets.WebSocketException => "could not connect",
            _ => e.GetType().Name,
        };
    }
}

/// <summary>
/// The network side of the direct transport: one HTTP handler for the manifest GET and the WebSocket. Its connect
/// step resolves the host itself and refuses private, loopback and link-local addresses (only loopback for
/// <c>localhost</c> under the developer switch), then connects to an address it checked, so a DNS answer cannot
/// change between the check and the connection. No cookies, no redirects, no proxy (a proxy would hide the address).
/// </summary>
public sealed class DirectNet : IDisposable
{
    public const int MaxManifestBytes = 64 * 1024;
    public static readonly TimeSpan ManifestTimeout = TimeSpan.FromSeconds(5);

    readonly bool _devLocal;
    readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve;
    readonly SocketsHttpHandler _handler;
    readonly HttpClient _http;

    /// <param name="resolve">Name resolution (tests replace it); DNS by default.</param>
    public DirectNet(bool devLocal, Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null)
    {
        _devLocal = devLocal;
        _resolve = resolve ?? ((host, ct) => Dns.GetHostAddressesAsync(host, ct));
        _handler = new SocketsHttpHandler
        {
            UseCookies = false,
            AllowAutoRedirect = false,
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(1),
            ConnectCallback = ConnectAsync,
        };
        Invoker = new HttpMessageInvoker(_handler, disposeHandler: false);
        _http = new HttpClient(_handler, disposeHandler: false) { Timeout = ManifestTimeout };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("BYOA-agent-wallet/" + WalletPaths.Version);
    }

    /// <summary>For <c>ClientWebSocket.ConnectAsync(uri, invoker, ct)</c>: the WebSocket goes through the same checked connect.</summary>
    public HttpMessageInvoker Invoker { get; }

    async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host;
        var port = context.DnsEndPoint.Port;
        var loopbackOnly = _devLocal && host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out var literal))
        {
            addresses = [literal];
        }
        else
        {
            try
            {
                addresses = await _resolve(host, ct).ConfigureAwait(false);
            }
            catch (SocketException e)
            {
                throw new DirectException("the site's name does not resolve", e);
            }
        }

        if (addresses.Length == 0) throw new DirectException("the site's name does not resolve");
        foreach (var address in addresses)
        {
            var allowed = loopbackOnly ? IPAddress.IsLoopback(address) : AddressCheck.IsPublic(address);
            if (!allowed) throw new DirectException("refused: the site's name resolves to a private, loopback or link-local address");
        }

        // localhost (developer switch only) tries 127.0.0.1 before ::1: Windows takes about 2 s to give up on a
        // loopback port nobody listens on, and local test servers often listen on IPv4 only.
        if (loopbackOnly) addresses = [.. addresses.OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)];
        Exception? last = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, port), ct).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException e)
            {
                socket.Dispose();
                last = e;
            }
        }

        throw new DirectException("could not connect to the site", last);
    }

    /// <summary>
    /// GETs the site's manifest before the consent: no cookies, no session code, at most 64 KB in 5 s, and
    /// <c>application/json</c>. Throws <see cref="DirectException"/> with a short reason.
    /// </summary>
    public async Task<JsonObject> FetchManifestAsync(Uri url, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ManifestTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new DirectException("manifest: HTTP " + (int)response.StatusCode);
            var type = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
            if (type is null || !(type == "application/json" || type.EndsWith("+json", StringComparison.Ordinal)))
            {
                throw new DirectException("manifest: not application/json");
            }

            if (response.Content.Headers.ContentLength > MaxManifestBytes) throw new DirectException("manifest: larger than 64 KB");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            var buffer = new byte[MaxManifestBytes + 1];
            var total = 0;
            while (total < buffer.Length)
            {
                var n = await stream.ReadAsync(buffer.AsMemory(total), timeout.Token).ConfigureAwait(false);
                if (n == 0) break;
                total += n;
            }

            if (total > MaxManifestBytes) throw new DirectException("manifest: larger than 64 KB");
            try
            {
                return JsonNode.Parse(buffer.AsSpan(0, total)) as JsonObject ?? throw new DirectException("manifest: not a JSON object");
            }
            catch (JsonException)
            {
                throw new DirectException("manifest: not JSON");
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new DirectException("manifest: no answer in 5 s");
        }
        catch (HttpRequestException e)
        {
            // A refusal from the address check keeps its own words; other failures say they were about the manifest.
            var refused = e.InnerException is DirectException;
            throw new DirectException((refused ? "" : "manifest: ") + DirectException.Reason(e), e);
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        Invoker.Dispose();
        _handler.Dispose();
    }
}
