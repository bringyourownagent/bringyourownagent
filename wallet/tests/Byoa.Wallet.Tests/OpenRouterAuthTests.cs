using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Byoa.Wallet.Providers;

namespace Byoa.Wallet.Tests;

/// <summary>
/// The OpenRouter sign-in: the PKCE pieces, the one-shot loopback callback and the key exchange — the browser is
/// faked with a plain HttpClient against the local listener, the exchange with a FakeHttp server. No real service.
/// </summary>
public class OpenRouterAuthTests
{
    const int Port = 5953;

    static HttpClient Plain() => new(new SocketsHttpHandler { AllowAutoRedirect = false });

    /// <summary>The fake browser: it reads the page whatever the status is, like a person reading the tab.</summary>
    static async Task<string> BrowserAsync(string url)
    {
        using var response = await Plain().GetAsync(url);
        return await response.Content.ReadAsStringAsync();
    }

    static Dictionary<string, string> QueryOf(string url)
    {
        var at = url.IndexOf('?');
        var query = new Dictionary<string, string>();
        if (at < 0) return query;
        foreach (var pair in url[(at + 1)..].Split('&'))
        {
            var eq = pair.IndexOf('=');
            if (eq < 0) continue;
            query[pair[..eq]] = Uri.UnescapeDataString(pair[(eq + 1)..]);
        }

        return query;
    }

    [Fact]
    public void TheChallengeFollowsTheRfc7636TestVector()
    {
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM",
            Pkce.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
    }

    [Fact]
    public void AVerifierHasTheAllowedCharactersAndTheChallengeDependsOnIt()
    {
        var verifier = Pkce.NewVerifier();
        Assert.Matches("^[A-Za-z0-9\\-_]{43,128}$", verifier);
        Assert.NotEqual(verifier, Pkce.NewVerifier());
        Assert.Equal(Pkce.Challenge(verifier), Pkce.Challenge(verifier));
        Assert.NotEqual(Pkce.Challenge(verifier), Pkce.Challenge(verifier + "x"));
        Assert.Matches("^[A-Za-z0-9\\-_]{16,}$", Pkce.NewState());
    }

    [Fact]
    public void TheAuthUrlCarriesTheCallbackTheChallengeAndTheState()
    {
        var url = Pkce.AuthUrl("http://127.0.0.1:4952/callback", "challenge-x_y", "state-7");

        Assert.StartsWith("https://openrouter.ai/auth?", url);
        Assert.Contains("callback_url=" + Uri.EscapeDataString("http://127.0.0.1:4952/callback"), url);
        Assert.Contains("code_challenge=challenge-x_y", url);
        Assert.Contains("code_challenge_method=S256", url);
        Assert.Contains("state=state-7", url);
    }

    [Fact]
    public void EachSignInTakesItsOwnFreeLoopbackPort()
    {
        using var first = OpenRouterAuth.Start(Plain(), _ => { });
        using var second = OpenRouterAuth.Start(Plain(), _ => { });

        Assert.Equal(IPAddress.Loopback, first.EndPoint.Address);
        Assert.Equal(IPAddress.Loopback, second.EndPoint.Address);
        Assert.NotEqual(0, first.EndPoint.Port);
        Assert.NotEqual(first.EndPoint.Port, second.EndPoint.Port);
    }

    [Fact]
    public async Task TheBrowserCallbackExchangesTheCodeForAKeyAndClosesTheListener()
    {
        using var server = new FakeHttp(Port);
        server.Respond(200, """{"key":"sk-or-v1-test-key"}""");
        string? opened = null;
        using var auth = OpenRouterAuth.Start(Plain(), url => opened = url, server.Url + "api/v1/auth/keys", TimeSpan.FromMinutes(1));

        Assert.Equal(IPAddress.Loopback, auth.EndPoint.Address);
        Assert.NotEqual(0, auth.EndPoint.Port);
        Assert.Equal(auth.AuthUrl, opened);                            // the browser was opened at the sign-in address
        Assert.StartsWith("https://openrouter.ai/auth?callback_url=http%3a%2f%2f127.0.0.1%3a" + auth.EndPoint.Port + "%2fcallback",
            auth.AuthUrl, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(auth.State, QueryOf(auth.AuthUrl)["state"]);

        var browser = BrowserAsync($"http://127.0.0.1:{auth.EndPoint.Port}/callback?code=the-code&state={Uri.EscapeDataString(auth.State)}");
        var key = await auth.WaitAsync();

        Assert.Equal("sk-or-v1-test-key", key);
        var page = await browser;
        Assert.Contains("You can close this tab and go back to the wallet", page, StringComparison.Ordinal);

        var call = Assert.Single(server.Requests);                     // the one exchange, with the PKCE pieces
        Assert.Equal("POST", call.Method);
        Assert.Equal("/api/v1/auth/keys", call.Path);
        var body = JsonNode.Parse(call.Body)!.AsObject();
        Assert.Equal("the-code", body["code"]!.GetValue<string>());
        Assert.Equal(auth.Verifier, body["code_verifier"]!.GetValue<string>());
        Assert.Equal("S256", body["code_challenge_method"]!.GetValue<string>());
        Assert.Equal(Pkce.Challenge(auth.Verifier), QueryOf(auth.AuthUrl)["code_challenge"]);

        using var refused = new TcpClient();                           // one callback is all the listener takes
        await Assert.ThrowsAnyAsync<SocketException>(
            () => refused.ConnectAsync(IPAddress.Loopback, auth.EndPoint.Port));
    }

    [Fact]
    public async Task AStrayTabGetsAPlainNotFoundAndTheCallbackStillWorks()
    {
        using var server = new FakeHttp(Port);
        server.Respond(200, """{"key":"sk-or-v1-second"}""");
        using var auth = OpenRouterAuth.Start(Plain(), _ => { }, server.Url + "api/v1/auth/keys", TimeSpan.FromMinutes(1));

        var stray = BrowserAsync($"http://127.0.0.1:{auth.EndPoint.Port}/favicon.ico");
        var browser = BrowserAsync($"http://127.0.0.1:{auth.EndPoint.Port}/callback?code=c2&state={Uri.EscapeDataString(auth.State)}");

        Assert.Equal("sk-or-v1-second", await auth.WaitAsync());
        Assert.Contains("Not found", await stray, StringComparison.Ordinal);
        await browser;
        Assert.Equal("/api/v1/auth/keys", Assert.Single(server.Requests).Path);
    }

    [Fact]
    public async Task AMismatchedStateFailsBeforeAnythingIsSent()
    {
        using var server = new FakeHttp(Port);
        using var auth = OpenRouterAuth.Start(Plain(), _ => { }, server.Url + "api/v1/auth/keys", TimeSpan.FromMinutes(1));

        var browser = BrowserAsync($"http://127.0.0.1:{auth.EndPoint.Port}/callback?code=x&state=made-up");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => auth.WaitAsync());

        Assert.Contains("state", error.Message, StringComparison.Ordinal);
        Assert.Contains("go back to the wallet", await browser, StringComparison.Ordinal);
        Assert.Empty(server.Requests);                                 // the exchange never happened
    }

    [Fact]
    public async Task ACallbackThatReportsACancellationExchangesNothing()
    {
        using var server = new FakeHttp(Port);
        using var auth = OpenRouterAuth.Start(Plain(), _ => { }, server.Url + "api/v1/auth/keys", TimeSpan.FromMinutes(1));

        var browser = BrowserAsync($"http://127.0.0.1:{auth.EndPoint.Port}/callback?error=access_denied&state={Uri.EscapeDataString(auth.State)}");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => auth.WaitAsync());

        Assert.Contains("cancelled", error.Message, StringComparison.OrdinalIgnoreCase);
        await browser;
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task ACallbackWithoutACodeFailsToo()
    {
        using var server = new FakeHttp(Port);
        using var auth = OpenRouterAuth.Start(Plain(), _ => { }, server.Url + "api/v1/auth/keys", TimeSpan.FromMinutes(1));

        var browser = BrowserAsync($"http://127.0.0.1:{auth.EndPoint.Port}/callback?state={Uri.EscapeDataString(auth.State)}");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => auth.WaitAsync());

        Assert.Contains("without a code", error.Message, StringComparison.Ordinal);
        await browser;
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task WaitingTimesOutAndClosesTheListener()
    {
        using var server = new FakeHttp(Port);
        using var auth = OpenRouterAuth.Start(Plain(), _ => { }, server.Url + "api/v1/auth/keys", TimeSpan.FromMilliseconds(400));

        var error = await Assert.ThrowsAsync<TimeoutException>(() => auth.WaitAsync());

        Assert.Contains("timed out", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(server.Requests);                                 // nothing was received and nothing was sent
        using var probe = new TcpClient();
        await Assert.ThrowsAnyAsync<SocketException>(
            () => probe.ConnectAsync(IPAddress.Loopback, auth.EndPoint.Port));
    }
}
