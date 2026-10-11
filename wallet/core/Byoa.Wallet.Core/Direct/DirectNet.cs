using System.Net;
using System.Net.Sockets;

namespace Byoa.Wallet.Direct;

/// <summary>A refusal or failure of the direct transport, with a short reason for the activity log.</summary>
public sealed class DirectException(string message, Exception? inner = null, bool permanent = false) : Exception(message, inner)
{
    /// <summary>A refusal by the wallet's own checks (not a passing network failure): trying again does not help.</summary>
    public bool Permanent { get; } = permanent;

    /// <summary>True when the exception, or one inside it, is a permanent refusal.</summary>
    public static bool IsPermanent(Exception e)
    {
        for (var x = e; x is not null; x = x.InnerException)
        {
            if (x is DirectException { Permanent: true }) return true;
        }

        return false;
    }

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
/// The network side of the direct transport: the handler the WebSocket goes through, after the visitor's Allow only
/// (the wallet makes no request of any kind before it: the manifest comes in the link). Its connect step resolves
/// the host itself and refuses private, loopback and link-local addresses (only loopback for <c>localhost</c> under
/// the developer switch), then connects to an address it checked, so a DNS answer cannot change between the check and
/// the connection. No cookies, no redirects, no proxy (a proxy would hide the address).
/// </summary>
public sealed class DirectNet : IDisposable
{
    readonly bool _devLocal;
    readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve;
    readonly SocketsHttpHandler _handler;

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
            if (!allowed) throw new DirectException("refused: the site's name resolves to a private, loopback or link-local address", permanent: true);
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

    public void Dispose()
    {
        Invoker.Dispose();
        _handler.Dispose();
    }
}
