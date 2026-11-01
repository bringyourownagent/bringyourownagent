using System.Text.Json.Nodes;
using Byoa.Wallet.Activity;
using Byoa.Wallet.Engine;
using Byoa.Wallet.Protocol;
using Byoa.Wallet.Providers;
using Byoa.Wallet.Secrets;
using Byoa.Wallet.Settings;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Tests;

/// <summary>
/// A model behind OpenRouter that draws a picture on a text request (Nano Banana 2.1 does on every one): OpenRouter bills the picture, and with
/// a small answer limit the text is empty. The source tells it from the answer, the wallet learns it once, keeps it, and asks that model for no
/// text again, while its pictures are untouched.
/// </summary>
public class DrawsOnTextTests
{
    const int Port = 5397;

    static OpenRouterTextSource Source(FakeHttp server) =>
        new("google/gemini-nano-banana-2.1", null, null, new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }), () => "sk-test", () => true, new Uri(server.Url));

    static TextRequest Ask() => new([new ChatMessage("user", "Write one short sentence about a fox.")], 200, 0.7, null, null);

    static string Sse(params string[] events) => string.Concat(events.Select(e => "data: " + e + "\n\n")) + "data: [DONE]\n\n";

    [Fact]
    public async Task AnEmptyAnswerWithAPictureFailsAsDrawnAndKeepsWhatItCost()
    {
        using var server = new FakeHttp(Port);
        server.Respond(200, """{"choices":[{"message":{"content":"","images":[{"type":"image_url","image_url":{"url":"data:image/jpeg;base64,AAAA"}}]}}],"usage":{"completion_tokens":1120,"cost":0.034881,"completion_tokens_details":{"image_tokens":1120}}}""");
        var failure = await Assert.ThrowsAsync<TextSourceException>(() => Source(server).GenerateAsync(Ask(), default));
        Assert.True(failure.Drew);
        Assert.Contains("drew a picture instead of writing", failure.Message);
        Assert.Equal(0.034881m, failure.Cost);
    }

    [Fact]
    public async Task AnAnswerWithTextAndPictureTokensIsKeptButSaysItDrew()
    {
        using var server = new FakeHttp(Port);
        server.Respond(200, """{"choices":[{"message":{"content":"A fox naps."}}],"usage":{"cost":0.035,"completion_tokens_details":{"image_tokens":1120}}}""");
        var answer = await Source(server).GenerateAsync(Ask(), default);
        Assert.Equal("A fox naps.", answer.Text);
        Assert.True(answer.Drew);
    }

    [Fact]
    public async Task AStreamTellsItDrewFromThePictureTokensInItsUsage()
    {
        using var server = new FakeHttp(Port);
        server.Respond(200, Sse(
            """{"choices":[{"delta":{"content":""},"finish_reason":"length"}]}""",
            """{"choices":[],"usage":{"completion_tokens":200,"cost":0.001176,"completion_tokens_details":{"image_tokens":200}}}"""));
        var failure = await Assert.ThrowsAsync<TextSourceException>(() => Source(server).GenerateStreamAsync(Ask(), _ => { }, default));
        Assert.True(failure.Drew);
        Assert.Equal(0.001176m, failure.Cost);
    }

    [Fact]
    public async Task AStreamTellsItDrewFromAPictureInAPieceWhenTheUsageSaysNothing()
    {
        using var server = new FakeHttp(Port);
        // as Nano Banana 2.1 answered a request for an icon's SVG: a picture in the stream, no text; this usage reports nothing
        server.Respond(200, Sse(
            """{"choices":[{"delta":{"content":"","images":[{"type":"image_url","image_url":{"url":"data:image/jpeg;base64,AAAA"}}]}}]}""",
            """{"choices":[{"delta":{},"finish_reason":"stop"}]}""",
            """{"choices":[],"usage":{"completion_tokens":0,"cost":0}}"""));
        var failure = await Assert.ThrowsAsync<TextSourceException>(() => Source(server).GenerateStreamAsync(Ask(), _ => { }, default));
        Assert.True(failure.Drew);
    }

    [Fact]
    public async Task APlainAnswerDidNotDraw()
    {
        using var server = new FakeHttp(Port);
        server.Respond(200, """{"choices":[{"message":{"content":"A fox naps."}}],"usage":{"cost":0.00003,"completion_tokens_details":{"image_tokens":0}}}""");
        Assert.False((await Source(server).GenerateAsync(Ask(), default)).Drew);
    }

    [Fact]
    public async Task OnceItRefusesTextNothingIsSentAndNothingIsPaid()
    {
        using var server = new FakeHttp(Port);
        var source = Source(server);
        source.RefuseText(TextRefusals.DrawsPictures);
        var failure = await Assert.ThrowsAsync<TextSourceException>(() => source.GenerateAsync(Ask(), default));
        Assert.False(failure.ProviderCalled);
        Assert.Equal(0m, failure.Cost);
        await Assert.ThrowsAsync<TextSourceException>(() => source.GenerateStreamAsync(Ask(), _ => { }, default));
        Assert.Empty(server.Requests);
    }

    [Fact]
    public void TheRouterLeavesItOutOfTextButNotOutOfPictures()
    {
        var drawing = new DrawingTextSource("openrouter:google/gemini-nano-banana-2.1");
        drawing.RefuseText(TextRefusals.DrawsPictures);
        var chain = new SkillChain { Skill = Vocabulary.Generate, Stops = [new ChainStop { SourceId = drawing.Id }] };
        var text = Router.PlanText(chain, [drawing], _ => true);
        Assert.Empty(text.Steps);
        Assert.Equal(TextRefusals.DrawsPictures, Assert.Single(text.Skipped).Why);
        Assert.Single(Router.PlanText(chain, [drawing], _ => true, pictures: true).Steps);
    }

    [Fact]
    public void ALearnedSettingMakesTheSourceRefuseTextAfterARestart()
    {
        var home = TestFolder.New();
        var oldHome = Environment.GetEnvironmentVariable(WalletPaths.HomeVariable);
        Environment.SetEnvironmentVariable(WalletPaths.HomeVariable, home);
        try
        {
            var store = new SettingsStore(null);
            using var sources = new SourceSet(store, new SecretStore(Path.Combine(home, "secrets.dat")), new HttpClient());
            var setting = new SourceSetting
            {
                Id = "openrouter:google/gemini-nano-banana-2.1", Kind = SourceKinds.OpenRouter, Model = "google/gemini-nano-banana-2.1",
                Skills = [Vocabulary.Generate], DrawsOnText = true,
            };
            var learned = Assert.IsAssignableFrom<ITextRefusal>(sources.CreateTextSource(setting));
            Assert.Equal(TextRefusals.DrawsPictures, learned.RefusesText);
            setting.DrawsOnText = false;
            Assert.Null(Assert.IsAssignableFrom<ITextRefusal>(sources.CreateTextSource(setting)).RefusesText);
        }
        finally
        {
            Environment.SetEnvironmentVariable(WalletPaths.HomeVariable, oldHome);
        }
    }

    // ------------------------------------------------------------------ in the engine

    const string Site = "https://icons.example.org";

    const string Manifest = """
        {"byoa":"0.1-draft","purpose":{"summary":"Icon designer","limits":{"maxTextChars":8000,"maxCallsPerMinute":40}},
        "skills":{"text.generate":{"required":true}},"privacy":"personal","expectedCost":{"perSession":"0.50 USD"}}
        """;

    static WireMessage TextCall(string grantId) =>
        new(2, Wire.Call, Site, false, new JsonObject
        {
            ["grantId"] = grantId,
            ["skill"] = "text.generate",
            ["input"] = new JsonObject { ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "A fox?" }) },
            ["params"] = new JsonObject(),
        });

    static bool Ok(JsonNode? result) => result is JsonObject o && o["ok"]?.GetValue<bool>() == true;

    [Theory]
    [InlineData(true)]          // nothing but a picture: the call goes on to the next model
    [InlineData(false)]         // a text and a picture: the text is the answer
    public async Task TheWalletLearnsItOnceKeepsItAndAsksTheNextModelFromThenOn(bool empty)
    {
        var time = new ManualTime();
        var ui = new ScriptedUi();
        var log = new ActivityLog(null, time.Now);
        var drawing = new DrawingTextSource("openrouter:google/gemini-nano-banana-2.1");
        drawing.Outcomes = new Queue<Func<TextRequest, TextAnswer>>([_ => empty
            ? throw new TextSourceException("the model drew a picture instead of writing", 0.035m, providerCalled: true, responseReceived: true, drew: true)
            : new TextAnswer("A fox naps.", 0.035m, 9, 1300, "nano", Drew: true)]);
        var plain = new FakeTextSource("t-plain") { Answers = new Queue<string>(["A fox runs.", "A fox sleeps.", "A fox hides."]) };
        var store = new SettingsStore(null, s => s.Sources.Add(new SourceSetting
        {
            Id = drawing.Id, Kind = SourceKinds.OpenRouter, Model = "google/gemini-nano-banana-2.1", Skills = [Vocabulary.Generate],
        }));
        var engine = new WalletEngine(store, [], log, ui, time, [drawing, plain]);
        store.Update(s => s.Chain(Vocabulary.Generate).Stops =
            [new ChainStop { SourceId = drawing.Id, AskBeforePaid = false }, new ChainStop { SourceId = plain.Id }]);
        var grant = Assert.IsType<JsonObject>(await engine.HandleAsync(Clips.Request(Site, click: true, Manifest)))["grantId"]!.GetValue<string>();

        var first = Assert.IsType<JsonObject>(await engine.HandleAsync(TextCall(grant)));
        Assert.True(Ok(first));
        Assert.Equal(empty ? "A fox runs." : "A fox naps.", first["output"]!["text"]!.GetValue<string>());
        Assert.Equal(0.035m, engine.FindGrant(grant)!.Spent);                                 // the picture was paid, once
        Assert.True(store.Read(s => s.Sources.Single(x => x.Id == drawing.Id).DrawsOnText));  // kept for after a restart
        Assert.Equal(TextRefusals.DrawsPictures, drawing.RefusesText);

        var second = Assert.IsType<JsonObject>(await engine.HandleAsync(TextCall(grant)));
        Assert.True(Ok(second));
        Assert.Equal(1, drawing.Calls);                                                         // not asked again
        Assert.Equal(0.035m, engine.FindGrant(grant)!.Spent);
    }
}

/// <summary>A fake model behind a key that can be kept from text, as the OpenRouter source is.</summary>
sealed class DrawingTextSource(string id) : FakeTextSource(id, Sources.Runs.ApiKey, costPerCall: 0.035m), ITextRefusal
{
    public string? RefusesText { get; private set; }

    public void RefuseText(string why) => RefusesText = why;
}
