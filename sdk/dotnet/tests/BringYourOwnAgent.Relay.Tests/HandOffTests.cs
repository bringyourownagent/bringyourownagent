using System;
using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BringYourOwnAgent.Relay.Tests;

/// <summary>
/// The hand-off address of CONTRACT-PAIR.md §2, rule by rule, on the hub with a clock the test moves: the token and the short code, one
/// use between them, 120 seconds, death with the code, wrong / used / expired short codes told alike, and the limits on wrong tries.
/// </summary>
public class HandOffHubTests
{
    const string Endpoint = "wss://geo.example.org/byoa/ws";
    const string Base = "https://geo.example.org/byoa/hand";
    const string Letters = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    static readonly DateTimeOffset Start = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    static DirectHub Hub(ManualClock clock, Action<RelayOptions>? change = null) => Rooms.Hub(clock, o => { o.HandOff = true; change?.Invoke(o); });

    static IssuedCode Issue(DirectHub hub, string room = Rooms.A, string? ip = "203.0.113.5") =>
        hub.Issue(room, ip, Endpoint, Base) ?? throw new Xunit.Sdk.XunitException("no code was issued");

    static string Token(IssuedCode issued) => issued.HandOff![(Base.Length + 1)..];

    [Fact]
    public void ACodeComesWithAnHttpsAddressAndATypedForm()
    {
        var hub = Hub(new ManualClock(Start));
        var issued = Issue(hub);

        Assert.StartsWith(Base + "/", issued.HandOff);
        var token = Token(issued);
        Assert.Matches("^[A-Za-z0-9_-]+$", token);                                            // URL-safe
        Assert.True(Base64Url.DecodeFromChars(token).Length >= 16, "at least 128 bits");
        Assert.True(issued.HandOff!.Length < 100, "short enough for a small QR code");
        Assert.Matches($"^[{Letters}]{{4}}-[{Letters}]{{4}}$", issued.Short);                 // 8 characters of the check alphabet, written K7MQ-3XPZ
        Assert.Equal(ConnectCheck.Compute(issued.Code), issued.Check);
        Assert.StartsWith("bringyourownagent://connect?", issued.Link);
    }

    [Fact]
    public void WithoutAHandOffBaseThereIsNoHandOffAndNewCodeIsAsItWas()
    {
        var hub = Hub(new ManualClock(Start));
        var plain = hub.Issue(Rooms.A, "203.0.113.5", Endpoint);
        Assert.Null(plain!.HandOff);
        Assert.Null(plain.Short);
        var old = hub.NewCode(Rooms.B, "203.0.113.6", Endpoint)!.Value;
        Assert.Equal(ConnectCheck.Compute(old.Code), old.Check);
    }

    [Fact]
    public void TheTokenGivesTheLinkOnceAndASecondTryIsToldItWasUsed()
    {
        var hub = Hub(new ManualClock(Start));
        var issued = Issue(hub);
        var token = Token(issued);

        var first = hub.HandOffOpen(token, null, "198.51.100.1");
        Assert.Equal(HandOffOutcome.Released, first.Outcome);
        Assert.Equal(issued.Link, first.Link);

        var second = hub.HandOffOpen(token, null, "198.51.100.1");
        Assert.Equal(HandOffOutcome.Used, second.Outcome);
        Assert.Null(second.Link);
        Assert.Equal(HandOffOutcome.Used, hub.HandOffOpen(token, null, "198.51.100.2").Outcome);
    }

    [Fact]
    public void AnUnknownTokenIsGone()
    {
        var hub = Hub(new ManualClock(Start));
        Issue(hub);
        var result = hub.HandOffOpen(DirectHub.NewSecret(), null, "198.51.100.1");
        Assert.Equal(HandOffOutcome.Gone, result.Outcome);
        Assert.Null(result.Link);
    }

    [Theory]
    [InlineData("upper")]
    [InlineData("lower")]
    [InlineData("nohyphen")]
    [InlineData("spaces")]
    public void AShortCodeIsTypedWithoutCareForCaseHyphenOrSpaces(string style)
    {
        var hub = Hub(new ManualClock(Start));
        var issued = Issue(hub);
        var typed = style switch
        {
            "lower" => issued.Short!.ToLowerInvariant(),
            "nohyphen" => issued.Short!.Replace("-", ""),
            "spaces" => "  " + issued.Short!.Replace("-", " ") + " ",
            _ => issued.Short!,
        };
        var result = hub.HandOffOpen(null, typed, "198.51.100.1");
        Assert.Equal(HandOffOutcome.Released, result.Outcome);
        Assert.Equal(issued.Link, result.Link);
    }

    [Fact]
    public void TheTokenAndTheShortCodeAreOneUseBetweenThem()
    {
        var hub = Hub(new ManualClock(Start));
        var a = Issue(hub);
        Assert.Equal(HandOffOutcome.Released, hub.HandOffOpen(Token(a), null, "198.51.100.1").Outcome);
        Assert.Equal(HandOffOutcome.Wrong, hub.HandOffOpen(null, a.Short, "198.51.100.1").Outcome);       // a short code that was used

        var b = Issue(hub, Rooms.B, "203.0.113.6");
        Assert.Equal(HandOffOutcome.Released, hub.HandOffOpen(null, b.Short, "198.51.100.1").Outcome);
        Assert.Equal(HandOffOutcome.Used, hub.HandOffOpen(Token(b), null, "198.51.100.1").Outcome);
    }

    [Fact]
    public void TheHandOffDiesAfter120SecondsButTheCodeLivesOn()
    {
        var clock = new ManualClock(Start);
        var hub = Hub(clock);
        var live = Issue(hub);
        var late = Issue(hub, Rooms.B, "203.0.113.6");

        clock.Advance(TimeSpan.FromSeconds(119));
        Assert.Equal(HandOffOutcome.Released, hub.HandOffOpen(Token(live), null, "198.51.100.1").Outcome);

        clock.Advance(TimeSpan.FromSeconds(2));                                                          // 121 seconds since both were issued
        Assert.Equal(HandOffOutcome.Gone, hub.HandOffOpen(Token(late), null, "198.51.100.1").Outcome);
        Assert.Equal(HandOffOutcome.Wrong, hub.HandOffOpen(null, late.Short, "198.51.100.1").Outcome);

        // the code itself is good for its ten minutes: the wallet can still connect
        Assert.Equal(("waiting", (string?)null), hub.CodeState(Rooms.B, late.Code));
        Assert.NotNull(hub.Accept(Rooms.Channel(), late.Code, null));
    }

    [Fact]
    public void ReleasingTheLinkDoesNotSpendTheCode()
    {
        var hub = Hub(new ManualClock(Start));
        var issued = Issue(hub);
        Assert.Equal(HandOffOutcome.Released, hub.HandOffOpen(Token(issued), null, "198.51.100.1").Outcome);
        Assert.Equal(("waiting", (string?)null), hub.CodeState(Rooms.A, issued.Code));
        var welcome = hub.Accept(Rooms.Channel(), issued.Code, null);
        Assert.NotNull(welcome);
        Assert.Equal("connected", hub.CodeState(Rooms.A, issued.Code).State);
    }

    [Fact]
    public void TheHandOffDiesWithItsCode()
    {
        var clock = new ManualClock(Start);
        var hub = Hub(clock);

        var cancelled = Issue(hub);
        Assert.True(hub.Cancel(Rooms.A, cancelled.Code));
        Assert.Equal(HandOffOutcome.Gone, hub.HandOffOpen(Token(cancelled), null, "198.51.100.1").Outcome);
        Assert.Equal(HandOffOutcome.Wrong, hub.HandOffOpen(null, cancelled.Short, "198.51.100.1").Outcome);

        var connected = Issue(hub, Rooms.B, "203.0.113.6");                                              // the wallet came by the raw link meanwhile
        Assert.NotNull(hub.Accept(Rooms.Channel(), connected.Code, null));
        Assert.Equal(HandOffOutcome.Gone, hub.HandOffOpen(Token(connected), null, "198.51.100.1").Outcome);
        Assert.Equal(HandOffOutcome.Wrong, hub.HandOffOpen(null, connected.Short, "198.51.100.1").Outcome);
    }

    [Fact]
    public void ALongerHandOffLifeNeverOutlivesTheCode()
    {
        var clock = new ManualClock(Start);
        var hub = Hub(clock, o => { o.HandOffLifeSeconds = 3600; o.CodeLifeSeconds = 300; });
        var issued = Issue(hub);
        clock.Advance(TimeSpan.FromSeconds(299));
        Assert.Equal(HandOffOutcome.Released, hub.HandOffOpen(Token(issued), null, "198.51.100.1").Outcome);

        var other = Issue(hub, Rooms.B, "203.0.113.6");
        clock.Advance(TimeSpan.FromSeconds(301));
        Assert.Equal(HandOffOutcome.Gone, hub.HandOffOpen(Token(other), null, "198.51.100.1").Outcome);
    }

    [Fact]
    public void AWrongAUsedAndAnExpiredShortCodeAreAnsweredAlike()
    {
        var clock = new ManualClock(Start);
        var hub = Hub(clock);
        var spent = Issue(hub);
        var expiring = Issue(hub, Rooms.B, "203.0.113.6");
        Assert.Equal(HandOffOutcome.Released, hub.HandOffOpen(null, spent.Short, "198.51.100.1").Outcome);
        clock.Advance(TimeSpan.FromSeconds(121));

        var wrong = hub.HandOffOpen(null, "ZZZZ-ZZZZ", "198.51.100.1");
        var used = hub.HandOffOpen(null, spent.Short, "198.51.100.2");
        var expired = hub.HandOffOpen(null, expiring.Short, "198.51.100.3");
        Assert.Equal(new HandOffResult(HandOffOutcome.Wrong, null), wrong);
        Assert.Equal(wrong, used);
        Assert.Equal(wrong, expired);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("K7MQ")]
    [InlineData("K7MQ-3XPZ-1111")]
    [InlineData("0000-1111")]                                                                           // not in the alphabet
    [InlineData("K7MQ-3XP\n")]
    public void ThingsThatCannotBeAShortCodeAreWrongAndCountAsATry(string? typed)
    {
        var hub = Hub(new ManualClock(Start));
        var issued = Issue(hub);
        Assert.Equal(HandOffOutcome.Wrong, hub.HandOffOpen(null, typed, "198.51.100.1").Outcome);
        for (var i = 0; i < 4; i++) hub.HandOffOpen(null, "ZZZZ-ZZZZ", "198.51.100.1");                  // five wrong tries in all
        Assert.Equal(HandOffOutcome.Throttled, hub.HandOffOpen(null, issued.Short, "198.51.100.1").Outcome);
    }

    [Fact]
    public void MoreThanFiveWrongTriesFromOneAddressInTenMinutesAreRefusedWhateverTheCode()
    {
        var clock = new ManualClock(Start);
        var hub = Hub(clock);
        var issued = Issue(hub);
        for (var i = 0; i < 5; i++) Assert.Equal(HandOffOutcome.Wrong, hub.HandOffOpen(null, "ZZZZ-ZZZ" + Letters[i], "198.51.100.1").Outcome);

        var refused = hub.HandOffOpen(null, issued.Short, "198.51.100.1");                               // the right code, the sixth try
        Assert.Equal(HandOffOutcome.Throttled, refused.Outcome);
        Assert.Null(refused.Link);
        Assert.Equal(HandOffOutcome.Throttled, hub.HandOffOpen(null, "ZZZZ-ZZZZ", "198.51.100.1").Outcome);

        // another address is not held back by it; the token is not a short code and is not limited by it
        Assert.Equal(HandOffOutcome.Released, hub.HandOffOpen(null, issued.Short, "198.51.100.2").Outcome);
        var second = Issue(hub, Rooms.B, "203.0.113.6");
        Assert.Equal(HandOffOutcome.Released, hub.HandOffOpen(Token(second), null, "198.51.100.1").Outcome);

        clock.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));
        var third = Issue(hub, Rooms.A, "203.0.113.7");
        Assert.Equal(HandOffOutcome.Released, hub.HandOffOpen(null, third.Short, "198.51.100.1").Outcome);
    }

    [Fact]
    public void RightTriesAreNotCountedAgainstTheAddress()
    {
        var hub = Hub(new ManualClock(Start));
        for (var i = 0; i < 12; i++)
        {
            var issued = Issue(hub, Rooms.A, "203.0.113." + (10 + i));
            Assert.Equal(HandOffOutcome.Released, hub.HandOffOpen(null, issued.Short, "198.51.100.1").Outcome);
        }
    }

    [Fact]
    public void ACeilingOnWrongTriesFromAllAddressesStopsBulkLookups()
    {
        var clock = new ManualClock(Start);
        var hub = Hub(clock, o => o.HandOffWrongTriesPerMinuteAll = 10);
        var issued = Issue(hub);
        for (var i = 0; i < 10; i++) Assert.Equal(HandOffOutcome.Wrong, hub.HandOffOpen(null, "ZZZZ-ZZZZ", "198.51.100." + (i + 1)).Outcome);   // ten addresses, one try each

        Assert.Equal(HandOffOutcome.Throttled, hub.HandOffOpen(null, issued.Short, "198.51.100.200").Outcome);
        Assert.Equal(HandOffOutcome.Throttled, hub.HandOffOpen(null, "ZZZZ-ZZZZ", "198.51.100.201").Outcome);

        clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(HandOffOutcome.Released, hub.HandOffOpen(null, issued.Short, "198.51.100.200").Outcome);
    }

    [Fact]
    public void ShortCodesOfLiveCodesAreAllDifferent()
    {
        var hub = Hub(new ManualClock(Start), o => { o.CodesPerMinute = 100_000; o.MaxCodes = 100_000; });
        var shorts = new HashSet<string>();
        var tokens = new HashSet<string>();
        for (var i = 0; i < 3000; i++)
        {
            var issued = Issue(hub, Rooms.A, "203.0.113.5");
            Assert.True(shorts.Add(issued.Short!));
            Assert.True(tokens.Add(Token(issued)));
        }
    }

    [Fact]
    public void ADeadCodeTakesItsHandOffOutOfTheTables()
    {
        var clock = new ManualClock(Start);
        var hub = Hub(clock);
        var issued = Issue(hub);
        clock.Advance(TimeSpan.FromMinutes(11));                                                          // the code is swept by the next call
        Assert.Equal(HandOffOutcome.Gone, hub.HandOffOpen(Token(issued), null, "198.51.100.1").Outcome);
        Assert.Equal(HandOffOutcome.Wrong, hub.HandOffOpen(null, issued.Short, "198.51.100.1").Outcome);
    }
}

/// <summary>The hand-off in a real site in this process: the code answer, the page, the endpoint, the headers, the logs.</summary>
public sealed class HandOffSiteTests : IAsyncLifetime
{
    sealed class Capture : ILoggerProvider
    {
        public ConcurrentQueue<(string Category, string Message)> Lines { get; } = new();
        public ILogger CreateLogger(string categoryName) => new CaptureLogger(categoryName, Lines);
        public void Dispose() { }

        sealed class CaptureLogger(string category, ConcurrentQueue<(string, string)> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                lines.Enqueue((category, formatter(state, exception) + (exception is null ? "" : " " + exception)));
        }
    }

    readonly Capture _logs = new();
    readonly List<WebApplication> _apps = [];
    readonly List<HttpClient> _clients = [];
    WebApplication _app = null!;
    HttpClient _http = null!;

    public async Task InitializeAsync() => (_app, _http) = await Start(o => { o.HandOff = true; o.ConnectCheck = true; });

    public async Task DisposeAsync()
    {
        foreach (var c in _clients) c.Dispose();
        foreach (var a in _apps) { await a.StopAsync(); await a.DisposeAsync(); }
    }

    async Task<(WebApplication App, HttpClient Http)> Start(Action<RelayOptions> configure)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory, ApplicationName = "BringYourOwnAgent.Relay.Tests" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Trace);
        builder.Logging.AddProvider(_logs);
        builder.Services.AddByoaRelay(options => { options.ManifestJson = TestManifest.Json; configure(options); });
        var app = builder.Build();
        app.UseWebSockets();
        app.MapByoaRelay();
        await app.StartAsync();
        var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        _apps.Add(app);
        _clients.Add(http);
        return (app, http);
    }

    static string NewRoom() => Guid.NewGuid().ToString();

    static async Task<JsonElement> Code(HttpClient http, string? room = null)
    {
        var answer = await http.PostAsync($"/api/r/{room ?? NewRoom()}/byoa/code", null);
        Assert.True(answer.IsSuccessStatusCode, $"code answered {(int)answer.StatusCode}");
        return await answer.Content.ReadFromJsonAsync<JsonElement>();
    }

    static string TokenOf(JsonElement code) => code.GetProperty("handoff").GetString()!.Split('/').Last();

    static Task<HttpResponseMessage> PostHand(HttpClient http, object body) => http.PostAsJsonAsync("/byoa/hand", body);

    static async Task<(HttpStatusCode Status, string Body)> Answer(HttpResponseMessage response) => (response.StatusCode, await response.Content.ReadAsStringAsync());

    [Fact]
    public async Task TheCodeAnswerCarriesTheHandOffAddressAndTheShortCode()
    {
        var code = await Code(_http);
        var handoff = code.GetProperty("handoff").GetString()!;
        Assert.StartsWith(_http.BaseAddress!.ToString().TrimEnd('/') + "/byoa/hand/", handoff);        // the site's own host; http only because this test site is not https
        Assert.Matches($"^[ABCDEFGHJKMNPQRSTUVWXYZ23456789]{{4}}-[ABCDEFGHJKMNPQRSTUVWXYZ23456789]{{4}}$", code.GetProperty("short").GetString());
        Assert.Equal(ConnectCheck.Compute(code.GetProperty("session").GetString()!), code.GetProperty("check").GetString());
        Assert.Equal(600, code.GetProperty("expiresIn").GetInt32());
        Assert.StartsWith("bringyourownagent://connect?", code.GetProperty("link").GetString());
    }

    [Fact]
    public async Task ASiteWithoutTheOptionAnswersAsItDidAndHasNoHandOffPage()
    {
        var (_, http) = await Start(o => o.ConnectCheck = true);
        var code = await Code(http);
        Assert.Equal(["session", "link", "check", "expiresIn"], code.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/byoa/hand")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/byoa/hand/" + new string('a', 32))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostHand(http, new { token = "x" })).StatusCode);
    }

    [Fact]
    public async Task AGetRevealsNothingAndSpendsNothing()
    {
        var code = await Code(_http);
        var token = TokenOf(code);
        var page = await _http.GetAsync("/byoa/hand/" + token);

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.StartsWith("text/html", page.Content.Headers.ContentType!.MediaType + "");
        var html = await page.Content.ReadAsStringAsync();
        // no link, no code, no token, no short code, no check code in it
        foreach (var secret in new[] { code.GetProperty("session").GetString()!, token, code.GetProperty("short").GetString()!, code.GetProperty("short").GetString()!.Replace("-", ""), code.GetProperty("link").GetString()! })
            Assert.DoesNotContain(secret, html);
        Assert.DoesNotContain("manifest=", html);
        Assert.DoesNotContain("session=", html);
        Assert.DoesNotContain("data-token", html);

        // the same page for a token the server never issued: a GET tells nothing about a token
        var other = await _http.GetStringAsync("/byoa/hand/" + DirectHub.NewSecret());
        Assert.Equal(html, other);

        // and it spent nothing: the click still gets the link
        var link = (await (await PostHand(_http, new { token })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("link").GetString();
        Assert.Equal(code.GetProperty("link").GetString(), link);
    }

    [Fact]
    public async Task ThePageIsNeverCachedNeverSentOnInAReferrerAndNeverIndexed()
    {
        var token = TokenOf(await Code(_http));
        foreach (var url in new[] { "/byoa/hand/" + token, "/byoa/hand" })
        {
            var page = await _http.GetAsync(url);
            Assert.Equal("no-store", page.Headers.CacheControl!.ToString());
            Assert.Equal("no-referrer", page.Headers.GetValues("Referrer-Policy").Single());
            Assert.Contains("noindex", page.Headers.GetValues("X-Robots-Tag").Single());
            Assert.Equal("nosniff", page.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.Equal("DENY", page.Headers.GetValues("X-Frame-Options").Single());
            var html = await page.Content.ReadAsStringAsync();
            Assert.Contains("<meta name=\"robots\" content=\"noindex, nofollow\">", html);
            Assert.Contains("<meta name=\"referrer\" content=\"no-referrer\">", html);
        }
        var post = await PostHand(_http, new { token = "nothing" });
        Assert.Equal("no-store", post.Headers.CacheControl!.ToString());
        Assert.Equal("no-referrer", post.Headers.GetValues("Referrer-Policy").Single());
        Assert.Contains("noindex", post.Headers.GetValues("X-Robots-Tag").Single());
    }

    [Fact]
    public async Task ThePageHasAStrictPolicyThatMatchesItsOwnScriptAndStyleAndNothingExternal()
    {
        var page = await _http.GetAsync("/byoa/hand");
        var html = await page.Content.ReadAsStringAsync();
        var csp = page.Headers.GetValues("Content-Security-Policy").Single();

        static string Hash(string text) => "'sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(text))) + "'";
        var script = Regex.Match(html, "<script>(.*?)</script>", RegexOptions.Singleline).Groups[1].Value;
        var style = Regex.Match(html, "<style>(.*?)</style>", RegexOptions.Singleline).Groups[1].Value;
        Assert.False(string.IsNullOrWhiteSpace(script));
        Assert.False(string.IsNullOrWhiteSpace(style));
        Assert.Contains("default-src 'none'", csp);
        Assert.Contains("script-src " + Hash(script), csp);
        Assert.Contains("style-src " + Hash(style), csp);
        Assert.Contains("connect-src 'self'", csp);
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.DoesNotContain("unsafe", csp);
        Assert.Equal(HandOffPage.ContentSecurityPolicy, csp);

        // exactly one script, no external file of any kind, no inline event handler
        Assert.Single(Regex.Matches(html, "<script"));
        Assert.DoesNotMatch("<(script|link|img|iframe|object|embed|audio|video|source)\\b[^>]*\\b(src|href|data)=", html);
        Assert.DoesNotMatch("\\son[a-z]+=", html);
    }

    [Fact]
    public async Task OneClickGivesTheLinkOnceAndASecondClickIsToldItWasUsed()
    {
        var code = await Code(_http);
        var token = TokenOf(code);

        var first = await PostHand(_http, new { token });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(code.GetProperty("link").GetString(), (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("link").GetString());

        var second = await Answer(await PostHand(_http, new { token }));
        Assert.Equal(HttpStatusCode.Gone, second.Status);
        Assert.Equal("""{"error":"used"}""", second.Body);
        Assert.DoesNotContain("bringyourownagent://", second.Body);

        var unknown = await Answer(await PostHand(_http, new { token = DirectHub.NewSecret() }));
        Assert.Equal(HttpStatusCode.Gone, unknown.Status);
        Assert.Equal("""{"error":"gone"}""", unknown.Body);
    }

    [Fact]
    public async Task AfterTheLinkIsReleasedTheWalletCanStillConnectWithTheCode()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var room = NewRoom();
        var code = await Code(_http, room);
        var session = code.GetProperty("session").GetString()!;
        var link = (await (await PostHand(_http, new { token = TokenOf(code) })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("link").GetString()!;

        var (endpoint, linkSession, _) = WalletClient.ParseLink(link);
        Assert.Equal(session, linkSession);
        await using var wallet = new WalletClient(new WalletOptions { Models = [new Persona("m1", "Local Qwen 4B", "qwen/qwen3.5-4b", "local", 50)], Log = new StringWriter() });
        var (pairing, _, error) = await wallet.ConnectAsync(endpoint, session, null, ct: stop.Token);
        Assert.Null(error);
        Assert.NotNull(pairing);

        var waited = await (await _http.PostAsJsonAsync($"/api/r/{room}/byoa/wait", new { session })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("connected", waited.GetProperty("state").GetString());
        Assert.Equal(HttpStatusCode.Gone, (await PostHand(_http, new { token = TokenOf(code) })).StatusCode);       // used, whichever way
    }

    [Fact]
    public async Task TheShortCodeIsTypedOnThePageWithoutATokenAndGivesTheSameLink()
    {
        var code = await Code(_http);
        var page = await _http.GetStringAsync("/byoa/hand");
        Assert.Contains("id=\"short\"", page);                       // the field the code is typed in
        Assert.Contains("data-mode=\"short\"", page);
        Assert.DoesNotContain("data-mode=\"token\"", page);

        var typed = code.GetProperty("short").GetString()!.ToLowerInvariant().Replace("-", " ");
        var answer = await PostHand(_http, new { @short = typed });
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Equal(code.GetProperty("link").GetString(), (await answer.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("link").GetString());
        Assert.Equal(HttpStatusCode.Gone, (await PostHand(_http, new { token = TokenOf(code) })).StatusCode);       // one use between the two
    }

    [Fact]
    public async Task AWrongAndAUsedShortCodeGetTheSameAnswer()
    {
        var code = await Code(_http);
        var shortCode = code.GetProperty("short").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await PostHand(_http, new { @short = shortCode })).StatusCode);

        var used = await Answer(await PostHand(_http, new { @short = shortCode }));
        var wrong = await Answer(await PostHand(_http, new { @short = "ZZZZ-ZZZZ" }));
        Assert.Equal(HttpStatusCode.NotFound, wrong.Status);
        Assert.Equal("""{"error":"wrong"}""", wrong.Body);
        Assert.Equal(wrong, used);
    }

    [Fact]
    public async Task SixWrongShortCodesFromOneAddressAreRefusedEvenWithTheRightOne()
    {
        var (_, http) = await Start(o => o.HandOff = true);
        var code = await Code(http);
        for (var i = 0; i < 5; i++) Assert.Equal(HttpStatusCode.NotFound, (await PostHand(http, new { @short = "ZZZZ-ZZZ" + "ABCDE"[i] })).StatusCode);

        var refused = await Answer(await PostHand(http, new { @short = code.GetProperty("short").GetString() }));
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.Status);
        Assert.Equal("""{"error":"slow"}""", refused.Body);
        // the token is not a short code: it works, and the limit leaves the code alone
        Assert.Equal(HttpStatusCode.OK, (await PostHand(http, new { token = TokenOf(code) })).StatusCode);
    }

    [Fact]
    public async Task OnlyAJsonBodyOfTheRightShapeAndSizeIsTaken()
    {
        var wrongType = await _http.PostAsync("/byoa/hand", new StringContent("""{"token":"abc"}""", Encoding.UTF8, "text/plain"));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, wrongType.StatusCode);
        var form = await _http.PostAsync("/byoa/hand", new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = "abc" }));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, form.StatusCode);                          // a cross-site form cannot send JSON

        var notJson = await _http.PostAsync("/byoa/hand", new StringContent("not json", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, notJson.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostHand(_http, new { other = 1 })).StatusCode);

        var big = await _http.PostAsync("/byoa/hand", new StringContent("{\"token\":\"" + new string('a', 5000) + "\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, big.StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("/byoa/hand/abc")).StatusCode);          // not the shape of a token: nothing is rendered for it
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("/byoa/hand/" + new string('a', 200))).StatusCode);
    }

    [Fact]
    public async Task NeitherTheTokenNorTheShortCodeNorTheLinkIsLoggedByTheLibrary()
    {
        var code = await Code(_http);
        var token = TokenOf(code);
        var shortCode = code.GetProperty("short").GetString()!;
        await _http.GetAsync("/byoa/hand/" + token);
        await PostHand(_http, new { token });
        await PostHand(_http, new { token });
        var second = await Code(_http);
        await PostHand(_http, new { @short = second.GetProperty("short").GetString() });
        await PostHand(_http, new { @short = "ZZZZ-ZZZZ" });

        var lines = _logs.Lines.ToArray();
        Assert.NotEmpty(lines);
        foreach (var (category, message) in lines)
        {
            if (category.StartsWith("BringYourOwnAgent", StringComparison.Ordinal))
                Assert.DoesNotContain(token, message);
            // nothing at all (the host's own request log included) carries the short codes, the session codes or a link: they only travel in bodies
            foreach (var secret in new[] { shortCode, shortCode.Replace("-", ""), second.GetProperty("short").GetString()!, code.GetProperty("session").GetString()!, "bringyourownagent://connect" })
                Assert.DoesNotContain(secret, message);
        }
    }

    [Fact]
    public async Task ASitePutsItsOwnWordsOnThePageAndTheyAreSetAsText()
    {
        var (_, http) = await Start(o =>
        {
            o.HandOff = true;
            o.HandOffLabels = new HandOffLabels { Lang = "de", Title = "\u00D6ffne dein Agent-Wallet <b>jetzt</b>", Open = "Mein Wallet \u00F6ffnen" };
            o.HandOffLabelsFor = ctx => ctx.Request.Query["lang"] == "en" ? new HandOffLabels { Title = "Open <script>alert(1)</script>" } : null;
        });
        var german = await http.GetStringAsync("/byoa/hand");
        Assert.Contains("<html lang=\"de\">", german);
        Assert.Contains("Mein Wallet \u00F6ffnen", WebUtility.HtmlDecode(german));
        Assert.Contains("&lt;b&gt;jetzt&lt;/b&gt;", german);                                               // markup in a word is not markup on the page
        Assert.DoesNotContain("<b>", german);

        var english = await http.GetStringAsync("/byoa/hand?lang=en");
        Assert.Contains("<html lang=\"en\">", english);
        Assert.Contains("Open &lt;script&gt;alert(1)&lt;/script&gt;", english);
        Assert.Single(Regex.Matches(english, "<script"));                                                  // only the page's own
    }

    [Fact]
    public async Task ThePageKnowsTheWebWalletAndThePathItPostsTo()
    {
        var (_, plain) = await Start(o => o.HandOff = true);
        var html = await plain.GetStringAsync("/byoa/hand");
        Assert.Contains("data-web=\"https://wallet.bringyourownagent.org/\"", html);
        Assert.Contains("data-post=\"/byoa/hand\"", html);

        var (_, own) = await Start(o => { o.HandOff = true; o.HandOffPath = "go/hand/"; o.HandOffWebWallet = "https://wallet.example.org/"; });
        var code = await Code(own);
        Assert.Contains("/go/hand/", code.GetProperty("handoff").GetString());
        var page = await own.GetStringAsync("/go/hand");
        Assert.Contains("data-post=\"/go/hand\"", page);
        Assert.Contains("data-web=\"https://wallet.example.org/\"", page);

        var (_, none) = await Start(o => { o.HandOff = true; o.HandOffWebWallet = null; });
        Assert.Contains("data-web=\"\"", await none.GetStringAsync("/byoa/hand"));
    }

    [Fact]
    public async Task ASiteBehindAProxyNamesItsOwnHostForTheAddress()
    {
        var (_, http) = await Start(o => { o.HandOff = true; o.HandOffBaseUrl = "https://icons.example.org/"; });
        var code = await Code(http);
        Assert.StartsWith("https://icons.example.org/byoa/hand/", code.GetProperty("handoff").GetString());
    }

    [Theory]
    [InlineData("HandOffWebWallet", "http://wallet.example.org/")]
    [InlineData("HandOffWebWallet", "javascript:alert(1)")]
    [InlineData("HandOffBaseUrl", "http://icons.example.org")]
    [InlineData("HandOffBaseUrl", "not an address")]
    public void AMistakenAddressInTheSettingsIsRefusedWhenTheSiteStarts(string option, string value)
    {
        var services = new ServiceCollection();
        Assert.Throws<ArgumentException>(() => services.AddByoaRelay(o =>
        {
            o.ManifestJson = TestManifest.Json;
            o.HandOff = true;
            if (option == "HandOffWebWallet") o.HandOffWebWallet = value; else o.HandOffBaseUrl = value;
        }));
    }
}
