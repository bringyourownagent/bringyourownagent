using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BringYourOwnAgent.Relay.Tests;

/// <summary>
/// The library in a real site, in this process: a Kestrel on a loopback port, the site built with <c>AddByoaRelay</c> and
/// <c>MapByoaRelay</c> and nothing else, and the pretend wallet dialling in over a real WebSocket — the whole flow the way a
/// visitor uses it: the page asks for a code, the wallet opens the link and comes in, the page asks all four participants at once,
/// and the answers arrive one by one in the order of their speed while the slow agent is still thinking.
/// </summary>
public sealed class SiteTests : IAsyncLifetime
{
    WebApplication _app = null!;
    HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        // exactly what a site writes: the relay, its manifest, and the WebSockets middleware
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            ApplicationName = "BringYourOwnAgent.Relay.Tests",
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddByoaRelay(options =>
        {
            options.ManifestJson = TestManifest.Json;
            options.ConnectCheck = true;                       // this site shows the check code on its page
        });
        _app = builder.Build();
        _app.UseWebSockets();
        _app.MapByoaRelay();
        await _app.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    /// <summary>Four participants with the demo names but short waits, so the suite stays quick: m1 fast, m4 an agent that "checks the web".</summary>
    static WalletOptions Wallet() => new()
    {
        Models =
        [
            new Persona("m1", "Local Qwen 4B", "qwen/qwen3.5-4b", "local", 200),
            new Persona("m2", "GLM Flash", "z-ai/glm-5.3-flash", "api-key", 500),
            new Persona("m3", "DeepSeek", "deepseek/deepseek-v3.2", "api-key", 900),
            new Persona("m4", "Claude Code (Opus)", "agent/claude-code-opus", "subscription", 2500),
        ],
        Log = new StringWriter(),
    };

    static string NewRoom() => Guid.NewGuid().ToString();                 // 36 characters of the shape the page makes

    static async Task<JsonElement> Post(HttpClient http, string path, object? body = null)
    {
        var answer = body is null ? await http.PostAsync(path, null) : await http.PostAsJsonAsync(path, body);
        var json = await answer.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(answer.IsSuccessStatusCode, $"{path} answered {(int)answer.StatusCode}: {json}");
        return json;
    }

    /// <summary>The wallet's side of the link the page shows: parse it exactly as the real wallet does.</summary>
    static async Task<WalletClient> Connect(string link, WalletOptions options, CancellationToken ct)
    {
        var (endpoint, session, manifest) = WalletClient.ParseLink(link);
        Assert.True(WalletClient.EndpointAllowed(endpoint, allowLocalWs: true), endpoint);
        var wallet = new WalletClient(options);
        var (pairing, echoed, error) = await wallet.ConnectAsync(endpoint, session, null, ct: ct);
        Assert.Null(error);
        Assert.NotNull(pairing);
        Assert.Equal(manifest, echoed);                                  // §7: the site echoes the manifest of the link, byte for byte
        return wallet;
    }

    [Fact]
    public async Task TheCodeAnswerCarriesTheCheckCodeOfTheSessionAndTheFlaggedManifest()
    {
        var room = NewRoom();
        var code = await Post(_http, $"/api/r/{room}/byoa/code");
        var session = code.GetProperty("session").GetString()!;
        var link = code.GetProperty("link").GetString()!;

        Assert.Equal(ConnectCheck.Compute(session), code.GetProperty("check").GetString());        // §3a: what the page shows
        Assert.Matches("^[ABCDEFGHJKMNPQRSTUVWXYZ23456789]{4}$", code.GetProperty("check").GetString());
        Assert.Equal(120, code.GetProperty("expiresIn").GetInt32());

        var (_, _, carried) = WalletClient.ParseLink(link);
        var manifest = JsonDocument.Parse(Encoding.UTF8.GetString(Base64Url.DecodeFromChars(carried))).RootElement;
        Assert.True(manifest.GetProperty("connect").GetProperty("check").GetBoolean());            // the site tells the wallet it shows the code

        // and the wallet that comes in hears exactly that string back
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var wallet = await Connect(link, Wallet(), stop.Token);
        var waited = await Post(_http, $"/api/r/{room}/byoa/wait", new { session });
        Assert.Equal("connected", waited.GetProperty("state").GetString());
        Assert.Equal(carried, wallet.WelcomeManifest);
    }

    [Fact]
    public async Task ACodeIsSpentOnceAndAPageOfTheWrongShapeIsTurnedAway()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var room = NewRoom();
        var code = await Post(_http, $"/api/r/{room}/byoa/code");
        var session = code.GetProperty("session").GetString()!;
        var link = code.GetProperty("link").GetString()!;
        await using var first = await Connect(link, Wallet(), stop.Token);

        var (endpoint, _, _) = WalletClient.ParseLink(link);
        var second = new WalletClient(Wallet());
        var (pairing, _, error) = await second.ConnectAsync(endpoint, session, null, ct: stop.Token);
        Assert.Equal("bad-session", error);                                        // §5: a code the server did not issue (or spent) is a MUST refuse
        Assert.Null(pairing);
        await second.DisposeAsync();

        foreach (var path in new[] { "short", "not%20a%20room%20id%20at%20all%20really%20no", new string('x', 65) })
        {
            var answer = await _http.PostAsync($"/api/r/{path}/byoa/code", null);
            Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode);
        }
    }

    [Fact]
    public async Task AVisitorPairsAWalletAsksAllFourParticipantsAndTheAnswersArriveInOrderOfSpeed()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var room = NewRoom();

        var code = await Post(_http, $"/api/r/{room}/byoa/code");
        Assert.StartsWith("bringyourownagent://connect?v=0.1&endpoint=", code.GetProperty("link").GetString());
        Assert.Equal("waiting", (await Post(_http, $"/api/r/{room}/byoa/wait", new { session = code.GetProperty("session").GetString() })).GetProperty("state").GetString());

        await using var wallet = await Connect(code.GetProperty("link").GetString()!, Wallet(), stop.Token);
        await wallet.SendAsync("""{"type":"ping"}""");                  // a frame the site ignores, from a wallet that is already in

        var connected = await Post(_http, $"/api/r/{room}/byoa/wait", new { session = code.GetProperty("session").GetString() });
        Assert.Equal("connected", connected.GetProperty("state").GetString());
        var pairing = connected.GetProperty("pairing").GetString()!;
        var models = connected.GetProperty("models").EnumerateArray().ToList();
        Assert.Equal(["m1", "m2", "m3", "m4"], models.Select(m => m.GetProperty("ref").GetString()));
        Assert.Equal(["model", "model", "model", "agent"], models.Select(m => m.GetProperty("kind").GetString()));
        Assert.Equal("Claude Code (Opus)", models[3].GetProperty("name").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, connected.GetProperty("capabilities").ValueKind);   // the fake wallet sends none (an older wallet): null, which is not "an empty list"
        Assert.Equal(60000, connected.GetProperty("limits").GetProperty("maxTextChars").GetInt32());

        // exactly what the page sends: a system message naming the participant, and one user message with the transcript
        async Task<string> Ask(string reference, string? transcript = null)
        {
            var started = await Post(_http, $"/api/r/{room}/byoa/job", new
            {
                pairing,
                model = reference,
                messages = new object[]
                {
                    new { role = "system", content = $"You are {reference}, one of 4 AI participants in a group chat with a human, the Visitor." },
                    new { role = "user", content = transcript ?? "Conversation so far:\n\n[Visitor (@visitor)]: What should we tell the visitor about group chats?"
                        + "\n\nThe latest message is for: everybody.\nWrite your next message now, or reply NO_REPLY." },
                },
                maxTokens = 3000,
                temperature = 0.7,
            });
            return started.GetProperty("id").GetString()!;
        }

        var run = Task.Run(() => wallet.RunAsync(stop.Token), stop.Token);
        var names = models.ToDictionary(m => m.GetProperty("ref").GetString()!, m => m.GetProperty("name").GetString()!);
        var order = new List<string>();
        var partial = "";

        // every participant of the round is asked at once and answers at its own speed; the page learns of them in that order
        async Task<Dictionary<string, string>> Round(IEnumerable<string> references, string? transcript)
        {
            var done = new Dictionary<string, string>();
            var jobs = new List<(string Ref, string Id)>();
            foreach (var reference in references) jobs.Add((reference, await Ask(reference, transcript)));
            var deadline = DateTime.UtcNow.AddSeconds(45);
            while (done.Count < jobs.Count && DateTime.UtcNow < deadline)
            {
                foreach (var (reference, id) in jobs.Where(j => !done.ContainsKey(j.Ref)))
                {
                    var polled = await Post(_http, $"/api/r/{room}/byoa/job/poll", new { id });
                    if (polled.GetProperty("state").GetString() == "pending")
                    {
                        var piece = polled.GetProperty("partial").GetString();
                        if (reference == "m4" && !string.IsNullOrEmpty(piece)) partial = piece;       // seen while it is still writing
                        continue;
                    }
                    Assert.True(polled.GetProperty("ok").GetBoolean(), $"{reference}: {polled.GetProperty("reason").GetString()}");
                    done[reference] = polled.GetProperty("text").GetString()!;
                    order.Add(reference);
                }
                await Task.Delay(40);
            }
            Assert.Equal(jobs.Count, done.Count);
            return done;
        }

        var texts = await Round(["m1", "m2", "m3", "m4"], null);
        Assert.Equal(["m1", "m2", "m3", "m4"], order);                   // each at its own speed, the agent last and never blocking the others
        Assert.Contains("What should we tell the visitor about group chats?", texts["m1"]);      // it quotes what the visitor asked
        Assert.DoesNotContain("https://", texts["m1"]);                  // a model answers from its own knowledge
        Assert.Contains("cannot browse", texts["m1"]);
        Assert.Contains("https://example.org/byoa", texts["m4"]);        // the agent says which pages it checked
        Assert.StartsWith("Local Qwen 4B:", texts["m1"]);                // the wallet's answer may echo its own name: the page strips that
        Assert.NotEmpty(partial);                                        // the slow one showed its text while it was still writing

        // the same participant answers again — asked by a participant this time — and now it reads what the others said
        var line = (string r) => $"[{names[r]} (@{WalletClient.HandleOf(names[r])})]: {texts[r]}";
        var second = await Round(["m4"], "Conversation so far:\n\n[Visitor (@visitor)]: What should we tell the visitor about group chats?\n\n"
            + $"{line("m1")}\n\n{line("m2")}\n\n{line("m3")}\n\n"
            + $"[{names["m1"]} (@local-qwen-4b)]: @claude-code-opus what do you say to that?"    // the newest message mentions the agent
            + "\n\nThe latest message is for: you.\nWrite " + names["m4"] + "'s next message now, or reply NO_REPLY.");
        Assert.Contains("Local Qwen 4B spoke before me", second["m4"]);  // it read the earlier answer and names who spoke before it

        // a message that names somebody else is not for this participant: it answers NO_REPLY (and quickly), and the page shows nothing
        var quiet = await Round(["m2"], "Conversation so far:\n\n[Visitor (@visitor)]: everybody have a look\n\n"
            + $"[Local Qwen 4B (@local-qwen-4b)]: @deepseek can you check this one?\n\nThe latest message is for: everybody.\n"
            + "Write GLM Flash's next message now, or reply NO_REPLY.");
        Assert.Equal("NO_REPLY", quiet["m2"]);

        // the page can stop waiting: the job is forgotten and the late answer dropped
        var cancelled = await Ask("m4");
        await Task.Delay(150);
        Assert.True((await Post(_http, $"/api/r/{room}/byoa/job/cancel", new { id = cancelled })).GetProperty("cancelled").GetBoolean());
        Assert.Equal("unknown", (await Post(_http, $"/api/r/{room}/byoa/job/poll", new { id = cancelled })).GetProperty("state").GetString());

        // a page that reloads finds its wallet again with the pairing it kept
        var again = await Post(_http, $"/api/r/{room}/byoa/status", new { pairing });
        Assert.Equal("live", again.GetProperty("state").GetString());
        Assert.Equal(4, again.GetProperty("models").GetArrayLength());
        Assert.Equal(4, again.GetProperty("modelsBySkill").GetProperty("text.generate").GetArrayLength());       // the per-skill shape is beside `models`, not instead of it

        stop.Cancel();
        await Task.WhenAny(run, Task.Delay(3000));
    }

    [Fact]
    public async Task TheVisitorChangingTheModelsInTheWalletUpdatesThePageWithoutTouchingTheChat()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var room = NewRoom();
        var code = await Post(_http, $"/api/r/{room}/byoa/code");
        await using var wallet = await Connect(code.GetProperty("link").GetString()!, Wallet(), stop.Token);
        var pairing = (await Post(_http, $"/api/r/{room}/byoa/wait", new { session = code.GetProperty("session").GetString() })).GetProperty("pairing").GetString()!;

        await wallet.SendAsync(wallet.GrantFrame([new Persona("m7", "A new model", "deepseek/deepseek-v3.2", "api-key", 100)]));
        var until = DateTime.UtcNow.AddSeconds(5);
        JsonElement status = default;
        while (DateTime.UtcNow < until)
        {
            status = await Post(_http, $"/api/r/{room}/byoa/status", new { pairing });
            if (status.GetProperty("models").GetArrayLength() == 1) break;
            await Task.Delay(50);
        }
        Assert.Equal("m7", status.GetProperty("models")[0].GetProperty("ref").GetString());
        Assert.Equal("A new model", status.GetProperty("models")[0].GetProperty("name").GetString());
        Assert.Contains("text.generate", status.GetProperty("skills").EnumerateArray().Select(s => s.GetString()));   // a grant frame says nothing about skills

        // and the page lets the wallet go: the pairing is forgotten and the wallet is told bye
        Assert.True((await Post(_http, $"/api/r/{room}/byoa/release", new { pairing })).GetProperty("released").GetBoolean());
        Assert.Equal("unknown", (await Post(_http, $"/api/r/{room}/byoa/status", new { pairing })).GetProperty("state").GetString());
        Assert.False((await Post(_http, $"/api/r/{room}/byoa/release", new { pairing })).GetProperty("released").GetBoolean());
    }

    [Fact]
    public async Task OneRoomsPageCanNeverUseAnotherRoomsWallet()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var roomA = NewRoom();
        var roomB = NewRoom();
        var code = await Post(_http, $"/api/r/{roomA}/byoa/code");
        var session = code.GetProperty("session").GetString()!;
        await using var wallet = await Connect(code.GetProperty("link").GetString()!, Wallet(), stop.Token);
        var pairing = (await Post(_http, $"/api/r/{roomA}/byoa/wait", new { session })).GetProperty("pairing").GetString()!;

        Assert.Equal("unknown", (await Post(_http, $"/api/r/{roomB}/byoa/status", new { pairing })).GetProperty("state").GetString());
        Assert.Equal("expired", (await Post(_http, $"/api/r/{roomB}/byoa/wait", new { session })).GetProperty("state").GetString());
        Assert.False((await Post(_http, $"/api/r/{roomB}/byoa/release", new { pairing })).GetProperty("released").GetBoolean());
        Assert.False((await Post(_http, $"/api/r/{roomB}/byoa/cancel", new { session })).GetProperty("cancelled").GetBoolean());

        var answer = await _http.PostAsJsonAsync($"/api/r/{roomB}/byoa/job", new
        {
            pairing, model = "m1",
            messages = new object[] { new { role = "user", content = "let me in" } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode);
        Assert.Equal("offline", (await answer.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());

        // and the jobs of room A cannot be fetched from room B
        var started = await Post(_http, $"/api/r/{roomA}/byoa/job", new { pairing, model = "m2", messages = new object[] { new { role = "user", content = "hello" } } });
        var id = started.GetProperty("id").GetString()!;
        Assert.Equal("unknown", (await Post(_http, $"/api/r/{roomB}/byoa/job/poll", new { id })).GetProperty("state").GetString());
        Assert.False((await Post(_http, $"/api/r/{roomB}/byoa/job/cancel", new { id })).GetProperty("cancelled").GetBoolean());
        Assert.True((await Post(_http, $"/api/r/{roomA}/byoa/job/cancel", new { id })).GetProperty("cancelled").GetBoolean());
    }

    [Fact]
    public async Task ThePageIsToldInPlainWordsWhenTheWalletGoesAway()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var room = NewRoom();
        var code = await Post(_http, $"/api/r/{room}/byoa/code");
        var wallet = await Connect(code.GetProperty("link").GetString()!, Wallet(), stop.Token);
        var pairing = (await Post(_http, $"/api/r/{room}/byoa/wait", new { session = code.GetProperty("session").GetString() })).GetProperty("pairing").GetString()!;
        var started = await Post(_http, $"/api/r/{room}/byoa/job", new { pairing, model = "m4", messages = new object[] { new { role = "user", content = "a long question" } } });
        var id = started.GetProperty("id").GetString()!;
        await Task.Delay(200);

        await wallet.CloseAsync();                                  // the visitor closed the wallet
        await wallet.DisposeAsync();

        var until = DateTime.UtcNow.AddSeconds(10);
        JsonElement polled = default;
        while (DateTime.UtcNow < until)
        {
            polled = await Post(_http, $"/api/r/{room}/byoa/job/poll", new { id });
            if (polled.GetProperty("state").GetString() == "done") break;
            await Task.Delay(50);
        }
        Assert.Equal("done", polled.GetProperty("state").GetString());
        Assert.False(polled.GetProperty("ok").GetBoolean());
        Assert.Equal("gone", polled.GetProperty("reason").GetString());
        Assert.Equal("offline", (await Post(_http, $"/api/r/{room}/byoa/status", new { pairing })).GetProperty("state").GetString());
    }

    [Fact]
    public async Task AWalletThatIsNotThereIsNeverToldApartFromOneThatSaidNo()
    {
        var room = NewRoom();
        Assert.Equal("expired", (await Post(_http, $"/api/r/{room}/byoa/wait", new { session = "no-such-code-no-such-code-0000" })).GetProperty("state").GetString());
        Assert.Equal("unknown", (await Post(_http, $"/api/r/{room}/byoa/status", new { pairing = "no-such-pairing" })).GetProperty("state").GetString());
        Assert.False((await Post(_http, $"/api/r/{room}/byoa/cancel", new { session = "no-such-code-no-such-code-0000" })).GetProperty("cancelled").GetBoolean());
    }

    [Fact]
    public async Task ARequestThePageWouldNotSendIsRefusedBeforeItReachesTheWallet()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var room = NewRoom();
        var code = await Post(_http, $"/api/r/{room}/byoa/code");
        await using var wallet = await Connect(code.GetProperty("link").GetString()!, Wallet(), stop.Token);
        var pairing = (await Post(_http, $"/api/r/{room}/byoa/wait", new { session = code.GetProperty("session").GetString() })).GetProperty("pairing").GetString()!;

        async Task<HttpStatusCode> Ask(object body)
        {
            var answer = await _http.PostAsJsonAsync($"/api/r/{room}/byoa/job", body);
            await answer.Content.ReadFromJsonAsync<JsonElement>();
            return answer.StatusCode;
        }
        Assert.Equal(HttpStatusCode.BadRequest, await Ask(new { pairing, model = "m9", messages = new object[] { new { role = "user", content = "hi" } } }));
        Assert.Equal(HttpStatusCode.BadRequest, await Ask(new { pairing, model = "m1", messages = new object[] { new { role = "tool", content = "hi" } } }));
        Assert.Equal(HttpStatusCode.BadRequest, await Ask(new { pairing, model = "m1", messages = new object[] { new { role = "user", content = new string('x', 60_001) } } }));
        Assert.Equal(HttpStatusCode.BadRequest, await Ask(new { pairing = "made-up", model = "m1", messages = new object[] { new { role = "user", content = "hi" } } }));
        Assert.Equal(HttpStatusCode.BadRequest, await Ask(new { pairing, messages = new object[] { new { role = "user", content = "hi" } } }));
        // a body far bigger than any chat call is refused before it is read
        var huge = await _http.PostAsJsonAsync($"/api/r/{room}/byoa/job", new { pairing, model = "m1", messages = new object[] { new { role = "user", content = new string('x', 1_600_000) } } });
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, huge.StatusCode);

        // and a body that is not a call at all (no JSON, no type) never reaches the wallet either
        var notJson = await _http.PostAsync($"/api/r/{room}/byoa/job", new StringContent("model=m1&message=hi", Encoding.UTF8, "text/plain"));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, notJson.StatusCode);
        var noType = await _http.PostAsync($"/api/r/{room}/byoa/job", new ByteArrayContent(Encoding.UTF8.GetBytes("""{"pairing":"x"}""")));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, noType.StatusCode);
    }

    [Fact]
    public async Task TheSitesOwnPathsAreWhereTheRelayAnswers()
    {
        // the same relay, mapped where a site of its own wants it: nothing of the protocol changes with the paths
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            ApplicationName = "BringYourOwnAgent.Relay.Tests.Paths",
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddByoaRelay(options =>
        {
            options.ManifestJson = TestManifest.Json;
            options.RoomPathPrefix = "/relay";
            options.RelayPath = "wallet";
            options.WalletPath = "/agents/channel";
        });
        await using var app = builder.Build();
        app.UseWebSockets();
        app.MapByoaRelay();
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };

        var room = NewRoom();
        var code = await Post(http, $"/relay/{room}/wallet/code");
        var (endpoint, session, _) = WalletClient.ParseLink(code.GetProperty("link").GetString()!);
        Assert.Equal($"{http.BaseAddress!.ToString().Replace("http://", "ws://")}agents/channel", endpoint);
        await using var wallet = new WalletClient(Wallet());
        var (pairing, _, error) = await wallet.ConnectAsync(endpoint, session, null);
        Assert.Null(error);
        Assert.Equal("connected", (await Post(http, $"/relay/{room}/wallet/wait", new { session })).GetProperty("state").GetString());
        Assert.Equal("live", (await Post(http, $"/relay/{room}/wallet/status", new { pairing })).GetProperty("state").GetString());
        await app.StopAsync();
    }
}