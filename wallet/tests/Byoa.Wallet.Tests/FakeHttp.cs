using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Byoa.Wallet.Tests;

/// <summary>
/// A tiny scripted HTTP server for one test, on a fixed port in the wallet's test range. It answers what the test
/// queued (or what its handler computes) and records every request, so a test can check what a source sent — and
/// it stays on the loopback only, like every test server in this project.
/// </summary>
sealed class FakeHttp : IDisposable
{
    public sealed record Call(string Method, string Path, string Body, IReadOnlyDictionary<string, string> Headers);

    readonly TcpListener _listener;
    readonly Task _loop;
    readonly Queue<(int Status, string Body)> _scripted = new();
    readonly List<Call> _requests = [];
    readonly object _gate = new();

    public FakeHttp(int port)
    {
        Port = port;
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        _loop = Task.Run(AcceptLoop);
    }

    public int Port { get; }
    public string Url => "http://127.0.0.1:" + Port + "/";

    /// <summary>Queues one answer for the next request; a request without a queued answer gets 404.</summary>
    public void Respond(int status, string body)
    {
        lock (_gate) _scripted.Enqueue((status, body));
    }

    /// <summary>When set, answers every request from here instead of the queue.</summary>
    public Func<Call, (int Status, string Body)>? Handler { get; set; }

    public IReadOnlyList<Call> Requests { get { lock (_gate) return [.. _requests]; } }

    async Task AcceptLoop()
    {
        while (true)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
            }
            catch
            {
                return;                                             // the listener was stopped with the test over
            }

            _ = Serve(client);
        }
    }

    async Task Serve(TcpClient client)
    {
        using var _ = client;
        using var stream = client.GetStream();
        var call = await ReadAsync(stream).ConfigureAwait(false);
        if (call is null) return;
        lock (_gate) _requests.Add(call);
        int status;
        string body;
        if (Handler is { } handler) (status, body) = handler(call);
        else lock (_gate) (status, body) = _scripted.Count > 0 ? _scripted.Dequeue() : (404, "{}");
        var payload = Encoding.UTF8.GetBytes(body);
        var head = Encoding.ASCII.GetBytes(
            "HTTP/1.1 " + status + " " + Reason(status) + "\r\n" +
            "Content-Type: application/json\r\n" +
            "Content-Length: " + payload.Length + "\r\n" +
            "Connection: close\r\n\r\n");
        await stream.WriteAsync(head).ConfigureAwait(false);
        await stream.WriteAsync(payload).ConfigureAwait(false);
    }

    static string Reason(int status) => status switch
    {
        200 => "OK", 302 => "Found", 400 => "Bad Request", 401 => "Unauthorized",
        404 => "Not Found", 500 => "Internal Server Error", _ => "Status",
    };

    static async Task<Call?> ReadAsync(NetworkStream stream)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[8192];
        var headEnd = FindHeadEnd(buffer);
        while (headEnd < 0)
        {
            var read = await stream.ReadAsync(chunk).ConfigureAwait(false);
            if (read == 0) return null;
            buffer.Write(chunk, 0, read);
            headEnd = FindHeadEnd(buffer);
        }

        var all = buffer.ToArray();
        var head = Encoding.ASCII.GetString(all, 0, headEnd);
        var lines = head.Split("\r\n");
        var first = lines[0].Split(' ');
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var at = line.IndexOf(':');
            if (at > 0) headers[line[..at].Trim()] = line[(at + 1)..].Trim();
        }

        var length = headers.TryGetValue("Content-Length", out var given) && int.TryParse(given, out var size) ? size : 0;
        while (buffer.Length - headEnd - 4 < length)
        {
            var read = await stream.ReadAsync(chunk).ConfigureAwait(false);
            if (read == 0) break;
            buffer.Write(chunk, 0, read);
        }

        all = buffer.ToArray();
        var body = Encoding.UTF8.GetString(all, headEnd + 4, Math.Max(0, all.Length - headEnd - 4));
        return new Call(first[0], first.Length > 1 ? first[1] : "", body, headers);
    }

    static int FindHeadEnd(MemoryStream buffer)
    {
        var all = buffer.ToArray();
        for (var i = 0; i + 4 <= all.Length; i++)
        {
            if (all[i] == (byte)'\r' && all[i + 1] == (byte)'\n' && all[i + 2] == (byte)'\r' && all[i + 3] == (byte)'\n') return i;
        }

        return -1;
    }

    public void Dispose()
    {
        try { _listener.Stop(); } catch { /* already gone */ }
        try { _loop.Wait(200); } catch { /* nothing to wait for */ }
    }
}
