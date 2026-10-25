using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Byoa.Wallet.Providers;

/// <summary>
/// The PKCE pieces (RFC 7636) of an OpenRouter sign-in: a code verifier made of the characters the RFC allows, its
/// S256 challenge, and the address the browser is opened at. The verifier and the state stay inside the wallet; the
/// challenge is the part OpenRouter sees first.
/// </summary>
public static class Pkce
{
    public const string Method = "S256";

    /// <summary>A fresh code verifier: 32 random bytes, that is 43 characters of the RFC 7636 unreserved set.</summary>
    public static string NewVerifier() => Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>A fresh state token: it rides the sign-in address and must come back unchanged in the callback.</summary>
    public static string NewState() => Base64Url(RandomNumberGenerator.GetBytes(16));

    /// <summary>The S256 challenge of a verifier: its SHA-256, base64url without padding.</summary>
    public static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    /// <summary>Where the browser goes to start the sign-in; the callback comes back to the wallet's one-shot listener.</summary>
    public static string AuthUrl(string callbackUrl, string challenge, string state)
    {
        return "https://openrouter.ai/auth?callback_url=" + Uri.EscapeDataString(callbackUrl)
            + "&code_challenge=" + Uri.EscapeDataString(challenge)
            + "&code_challenge_method=" + Method
            + "&state=" + Uri.EscapeDataString(state);
    }

    static string Base64Url(byte[] raw) => Convert.ToBase64String(raw).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// One OpenRouter sign-in with a free account: it binds a one-shot listener on this computer only (a free port on
/// 127.0.0.1), opens the browser at OpenRouter, answers the callback with a plain page, checks that the answer
/// carries this sign-in's state, and exchanges the code for the visitor's own API key (POST /api/v1/auth/keys with
/// the PKCE verifier). The key is only returned, never logged or kept here — the caller stores it in the wallet's
/// secret store. The listener closes on every path: a key, a cancellation at OpenRouter, a mismatched state, the
/// wait running out (three minutes), or the wallet being closed.
/// </summary>
public sealed class OpenRouterAuth : IDisposable
{
    /// <summary>How long the browser may take to come back.</summary>
    public static readonly TimeSpan DefaultWait = TimeSpan.FromMinutes(3);

    const int MaxStrays = 20;                                   // favicon and other tabs a browser insists on opening
    const int MaxRequestBytes = 16 * 1024;

    readonly HttpClient _http;
    readonly Uri _exchange;
    readonly TimeSpan _wait;
    TcpListener? _listener;

    OpenRouterAuth(HttpClient http, string? exchangeUrl, TimeSpan wait)
    {
        _http = http;
        _wait = wait;
        // The key travels in this one POST: like every source, https — or this very computer (a test).
        var exchange = string.IsNullOrWhiteSpace(exchangeUrl) ? "https://openrouter.ai/api/v1/auth/keys" : exchangeUrl.Trim();
        if (!Uri.TryCreate(exchange, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host)
            || !(uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp && OpenAiCompatibleSource.IsLoopback(uri.Host)))
            throw new ArgumentException("The key exchange needs a complete https address: the key is never sent in the clear.", nameof(exchangeUrl));
        _exchange = uri;
        Verifier = Pkce.NewVerifier();
        State = Pkce.NewState();
    }

    /// <summary>Binds the one-shot listener and opens the browser at the sign-in address.</summary>
    public static OpenRouterAuth Start(HttpClient http, Action<string> openBrowser, string? exchangeUrl = null, TimeSpan? wait = null)
    {
        var auth = new OpenRouterAuth(http, exchangeUrl, wait ?? DefaultWait);
        try
        {
            auth.Bind();
            openBrowser(auth.AuthUrl);
            return auth;
        }
        catch
        {
            auth.Dispose();                                     // the browser never went out: nothing may stay listening
            throw;
        }
    }

    void Bind()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);     // a free port, this computer only
        _listener.Start();
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        EndPoint = new IPEndPoint(IPAddress.Loopback, port);
        AuthUrl = Pkce.AuthUrl("http://127.0.0.1:" + port + "/callback", Pkce.Challenge(Verifier), State);
    }

    /// <summary>The loopback address the callback listens on: a free port, bound when the sign-in started.</summary>
    public IPEndPoint EndPoint { get; private set; } = new(IPAddress.Loopback, 0);

    /// <summary>This sign-in's PKCE verifier. It goes out only in the key exchange; it is not for logs.</summary>
    public string Verifier { get; }

    /// <summary>The random state this sign-in expects to see echoed in the callback.</summary>
    public string State { get; }

    /// <summary>The address the browser was opened at.</summary>
    public string AuthUrl { get; private set; } = "";

    /// <summary>
    /// Waits for the browser's callback and returns the visitor's OpenRouter key. It fails when the visitor cancelled
    /// at OpenRouter, when the answer does not carry this sign-in's state, or when nothing arrives within the wait.
    /// </summary>
    public async Task<string> WaitAsync(CancellationToken ct = default)
    {
        var listener = _listener ?? throw new InvalidOperationException("the sign-in has not started");
        string code;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(_wait);
            code = await ReceiveCodeAsync(listener, linked.Token, ct).ConfigureAwait(false);
        }
        finally
        {
            Dispose();                                          // one callback is all this listener will ever take
        }

        return await ExchangeAsync(code, ct).ConfigureAwait(false);
    }

    /// <summary>Accepts connections until the browser reaches the callback; stray tabs get a plain 404.</summary>
    async Task<string> ReceiveCodeAsync(TcpListener listener, CancellationToken waitCt, CancellationToken callerCt)
    {
        var strays = 0;
        while (true)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(waitCt).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!callerCt.IsCancellationRequested)
            {
                throw new TimeoutException("The OpenRouter sign-in timed out: the browser did not come back within "
                    + Math.Max(1, (int)Math.Round(_wait.TotalMinutes)) + " minutes. Nothing was received and nothing was sent.");
            }

            using (client)
            {
                var target = await ReadTargetAsync(client, waitCt).ConfigureAwait(false);
                if (target is null || !target.StartsWith("/callback", StringComparison.Ordinal))
                {
                    // A favicon or another tab of the same browser: not the wallet's callback, so keep waiting.
                    if (++strays > MaxStrays) throw new InvalidOperationException("The OpenRouter sign-in never reached the wallet's callback.");
                    await AnswerAsync(client, 404, "Not found. You can close this tab and go back to the wallet.").ConfigureAwait(false);
                    continue;
                }

                var query = ParseQuery(target);
                var error = query.GetValueOrDefault("error");
                if (!string.IsNullOrWhiteSpace(error))
                {
                    await AnswerAsync(client, 400, FailedPage("The sign-in was cancelled at OpenRouter")).ConfigureAwait(false);
                    throw new InvalidOperationException("The OpenRouter sign-in was cancelled: " + error);
                }

                if (!string.Equals(query.GetValueOrDefault("state"), State, StringComparison.Ordinal))
                {
                    await AnswerAsync(client, 400, FailedPage("The wallet could not verify this sign-in")).ConfigureAwait(false);
                    throw new InvalidOperationException("The OpenRouter sign-in failed its state check: the answer does not match the sign-in this wallet started.");
                }

                var code = query.GetValueOrDefault("code");
                if (string.IsNullOrWhiteSpace(code))
                {
                    await AnswerAsync(client, 400, FailedPage("The sign-in carried no code")).ConfigureAwait(false);
                    throw new InvalidOperationException("The OpenRouter sign-in came back without a code.");
                }

                await AnswerAsync(client, 200, "You can close this tab and go back to the wallet.").ConfigureAwait(false);
                return code;
            }
        }
    }

    /// <summary>Trades the callback's code for the visitor's key; the verifier proves this wallet started the sign-in.</summary>
    async Task<string> ExchangeAsync(string code, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["code"] = code,
            ["code_verifier"] = Verifier,
            ["code_challenge_method"] = Pkce.Method,
        };
        using var message = new HttpRequestMessage(HttpMethod.Post, _exchange)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        using var response = await _http.SendAsync(message, ct).ConfigureAwait(false);
        var text = await BoundedHttp.ReadJsonAsync(response, ct).ConfigureAwait(false);
        string? key = null;
        try
        {
            key = JsonNode.Parse(text)?["key"]?.GetValue<string>();
        }
        catch (JsonException)
        {
        }

        if (!response.IsSuccessStatusCode || string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("OpenRouter did not hand out a key (HTTP " + (int)response.StatusCode + "). Nothing was stored.");
        return key;
    }

    /// <summary>The target of the browser's request ("GET /callback?... HTTP/1.1" → "/callback?..."), bounded in size.</summary>
    static async Task<string?> ReadTargetAsync(TcpClient client, CancellationToken ct)
    {
        var stream = client.GetStream();
        var buffer = new byte[MaxRequestBytes];
        var taken = 0;
        while (taken < buffer.Length)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(buffer.AsMemory(taken, buffer.Length - taken), ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException)
            {
                return null;
            }

            if (read == 0) break;
            taken += read;
            if (taken >= 4 && buffer[taken - 4] == '\r' && buffer[taken - 3] == '\n' && buffer[taken - 2] == '\r' && buffer[taken - 1] == '\n') break;
        }

        var line = Encoding.ASCII.GetString(buffer, 0, taken).Split("\r\n")[0];
        var parts = line.Split(' ');
        return parts.Length >= 2 ? parts[1] : null;
    }

    /// <summary>The plain page the visitor reads in the browser tab before going back to the wallet.</summary>
    static async Task AnswerAsync(TcpClient client, int status, string text)
    {
        var body = Encoding.UTF8.GetBytes(text);
        var head = Encoding.ASCII.GetBytes(
            "HTTP/1.1 " + status + " " + Reason(status) + "\r\n"
            + "Content-Type: text/plain; charset=utf-8\r\n"
            + "Content-Length: " + body.Length + "\r\n"
            + "Connection: close\r\n\r\n");
        try
        {
            var stream = client.GetStream();
            await stream.WriteAsync(head).ConfigureAwait(false);
            await stream.WriteAsync(body).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
        {
        }                                                       // the tab was already gone
    }

    static string Reason(int status) => status switch
    {
        200 => "OK", 400 => "Bad Request", 404 => "Not Found", _ => "Status",
    };

    static string FailedPage(string what) => what + ". You can close this tab and go back to the wallet.";

    static Dictionary<string, string> ParseQuery(string target)
    {
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        var at = target.IndexOf('?');
        if (at < 0) return query;
        foreach (var pair in target[(at + 1)..].Split('&'))
        {
            var eq = pair.IndexOf('=');
            var name = (eq < 0 ? pair : pair[..eq]).Trim();
            if (name.Length == 0) continue;
            query[name] = Uri.UnescapeDataString((eq < 0 ? "" : pair[(eq + 1)..]).Replace('+', ' '));
        }

        return query;
    }

    public void Dispose()
    {
        try
        {
            _listener?.Stop();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
