using System.Text.Json;
using Byoa.Wallet.Activity;
using Byoa.Wallet.Engine;
using Byoa.Wallet.Everywhere;
using Byoa.Wallet.Settings;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Tests;

/// <summary>The wallet's own calls (dictation and the check): through the same routes, limits and log as a site's call, with no site in it.</summary>
public class EverywhereEngineTests
{
    readonly ManualTime _time = new();
    readonly ScriptedUi _ui = new();
    readonly SettingsStore _store = new(null);
    readonly ActivityLog _log;
    readonly FakeSource _local = new("s-local") { Answer = "hello there" };
    readonly FakeSource _paid = new("s-paid", Runs.ApiKey, pricePerSecond: 0.00004m) { Answer = "paid words" };
    readonly FakeTextSource _model = new("t-local") { Answers = new Queue<string>(["The Earth is round."]) };
    readonly FakeTextSource _paidModel = new("t-paid", Runs.ApiKey, costPerCall: 0.004m) { Answers = new Queue<string>(["From the cloud."]) };

    public EverywhereEngineTests() => _log = new ActivityLog(null, _time.Now);

    WalletEngine Engine(IReadOnlyList<ISpeechSource>? speech = null, IReadOnlyList<ITextSource>? text = null)
    {
        speech ??= [_local, _paid];
        text ??= [_model, _paidModel];
        _store.Update(s =>
        {
            SpeechRoutes.Ensure(s, speech);
            Router.EnsureTextChain(s, text);
        });
        return new WalletEngine(_store, speech, _log, _ui, _time, text);
    }

    static float[] Seconds(double seconds) => new float[(int)(seconds * 16000)];

    static string Json(ActivityEntry entry) => JsonSerializer.Serialize(entry);

    // ------------------------------------------------------------------ dictation: the speech route

    [Fact]
    public async Task ARecordingIsRecognisedByTheVisitorsLocalSpeechModel()
    {
        var engine = Engine();
        var result = await engine.TranscribeOwnAsync(Seconds(2), null);
        Assert.True(result.Ok);
        Assert.Equal("hello there", result.Text);
        Assert.Equal(1, _local.Calls);
        Assert.Equal(0, _paid.Calls);
    }

    [Fact]
    public async Task TheCallIsInTheActivityLogUnderEverywhereWithNoTextInIt()
    {
        var engine = Engine();
        await engine.TranscribeOwnAsync(Seconds(2), "ru");

        var entry = Assert.Single(_log.Recent());
        Assert.Equal("Everywhere", entry.Origin);
        Assert.Equal("call", entry.Kind);
        Assert.Equal("audio.transcribe", entry.Skill);
        Assert.Equal("ok", entry.Outcome);
        Assert.Equal("Fake s-local", entry.Source);
        Assert.Equal(2, entry.ClipSeconds);
        Assert.Equal("ru", entry.Language);
        Assert.DoesNotContain("hello there", Json(entry));  // the recognised text is never logged
    }

    [Fact]
    public async Task NoSiteRecordIsMadeForTheWalletsOwnCalls()
    {
        var engine = Engine();
        await engine.TranscribeOwnAsync(Seconds(1), null);
        await engine.GenerateOwnAsync([new ChatMessage("user", "hi")]);
        Assert.Empty(_store.Read(s => s.Sites.ToList()));
        Assert.Null(engine.FindSite("Everywhere"));
    }

    [Fact]
    public async Task TheLanguageGoesToTheSourceAsAPrimaryTag()
    {
        var engine = Engine();
        await engine.TranscribeOwnAsync(Seconds(1), "RU-ru");
        Assert.Equal("ru", _local.LastLanguage);
        await engine.TranscribeOwnAsync(Seconds(1), null);
        Assert.Null(_local.LastLanguage);
    }

    [Fact]
    public async Task APaidSourceIsNeverReachedUnlessTheVisitorAllowedIt()
    {
        _local.Fail = true;
        var engine = Engine();
        var result = await engine.TranscribeOwnAsync(Seconds(2), null);
        Assert.False(result.Ok);
        Assert.Contains("No speech model answered", result.Error);
        Assert.Contains("Fake s-local", result.Error);      // the route's own reason is kept
        Assert.Equal(0, _paid.Calls);
        Assert.Equal(0, _ui.PaidQuestions);                 // no question either: it is not even in the route
    }

    [Fact]
    public async Task WithNoLocalSpeechModelAtAllThePlainMessageSaysToAddOne()
    {
        var engine = Engine(speech: [_paid]);
        var result = await engine.TranscribeOwnAsync(Seconds(2), null);
        Assert.False(result.Ok);
        Assert.Contains("My models", result.Error);
        Assert.Equal(0, _paid.Calls);
    }

    [Fact]
    public async Task AllowedPaidFallbackAsksBeforeTheCallAndNamesEverywhere()
    {
        _local.Fail = true;
        _ui.Paid = PaidAnswer.Once;
        _store.Update(s => s.Everywhere.AllowPaid = true);
        var engine = Engine();

        var result = await engine.TranscribeOwnAsync(Seconds(2), null);

        Assert.True(result.Ok);
        Assert.Equal("paid words", result.Text);
        Assert.Equal(1, _ui.PaidQuestions);
        Assert.Equal("Everywhere", _ui.PaidModels.Single().Host);
        var entry = _log.Recent()[0];
        Assert.Equal("Everywhere", entry.Origin);
        Assert.True(entry.CostValue > 0);
        Assert.Contains("Fake s-local", entry.Fallback);
    }

    [Fact]
    public async Task SayingNoToThePaidQuestionEndsTheCallWithAReason()
    {
        _local.Fail = true;
        _ui.Paid = PaidAnswer.No;
        _store.Update(s => s.Everywhere.AllowPaid = true);
        var engine = Engine();

        var result = await engine.TranscribeOwnAsync(Seconds(2), null);

        Assert.False(result.Ok);
        Assert.Contains("you said no", result.Error);
        Assert.Equal(0, _paid.Calls);
    }

    [Fact]
    public async Task AlwaysIsRememberedForEverywhereAndNotAsASite()
    {
        _local.Fail = true;
        _ui.Paid = PaidAnswer.Always;
        _store.Update(s => s.Everywhere.AllowPaid = true);
        var engine = Engine();

        Assert.True((await engine.TranscribeOwnAsync(Seconds(2), null)).Ok);
        Assert.True(_store.Read(s => s.Everywhere.PaidWithoutAsking));
        Assert.Empty(_store.Read(s => s.Sites.ToList()));   // no "Everywhere" in Sites

        Assert.True((await engine.TranscribeOwnAsync(Seconds(2), null)).Ok);
        Assert.Equal(1, _ui.PaidQuestions);                 // not asked again
    }

    [Fact]
    public async Task AnEmptyRecordingIsRefusedWithoutCallingAnything()
    {
        var engine = Engine();
        var result = await engine.TranscribeOwnAsync([], null);
        Assert.False(result.Ok);
        Assert.Equal("Nothing was recorded.", result.Error);
        Assert.Equal(0, _local.Calls);
    }

    // ------------------------------------------------------------------ the limits

    [Fact]
    public async Task TheDailyCallLimitStopsTheCallsAndSaysSo()
    {
        _store.Update(s => s.Limits.SiteCallsPerDay = 1);
        var engine = Engine();
        Assert.True((await engine.TranscribeOwnAsync(Seconds(1), null)).Ok);

        var second = await engine.TranscribeOwnAsync(Seconds(1), null);
        Assert.False(second.Ok);
        Assert.Contains("limit", second.Error);
        Assert.Equal(1, _local.Calls);
        Assert.Equal("limit", _log.Recent()[0].Outcome);
    }

    [Fact]
    public async Task OwnCallsCountInTheWalletsDayAndInTheirOwnDay()
    {
        var engine = Engine();
        await engine.TranscribeOwnAsync(Seconds(1), null);
        await engine.GenerateOwnAsync([new ChatMessage("user", "hi")]);
        Assert.Equal(2, _log.Today(_time.Now, "Everywhere").Calls);
        Assert.Equal(2, _log.Today(_time.Now).Calls);
    }

    [Fact]
    public async Task TheWalletsMoneyLimitForTheDayHoldsBackAPaidCall()
    {
        _local.Fail = true;
        _ui.Paid = PaidAnswer.Once;
        _store.Update(s =>
        {
            s.Everywhere.AllowPaid = true;
            s.Limits.WalletPerDay = "0.00001 USD";
        });
        var engine = Engine();

        var result = await engine.TranscribeOwnAsync(Seconds(10), null);

        Assert.False(result.Ok);
        Assert.Equal(0, _paid.Calls);
        Assert.Equal(0, _ui.PaidQuestions);                 // it never got as far as asking
    }

    // ------------------------------------------------------------------ the text route

    [Fact]
    public async Task TheCheckGoesToTheFirstTextModelAndIsLoggedWithoutText()
    {
        var engine = Engine();
        var result = await engine.GenerateOwnAsync([new ChatMessage("system", "Check it."), new ChatMessage("user", "The Earth is flat.")]);

        Assert.True(result.Ok);
        Assert.Equal("The Earth is round.", result.Text);
        Assert.Equal(["Check it.", "The Earth is flat."], _model.LastRequest!.Messages.Select(m => m.Content).ToArray());
        var entry = Assert.Single(_log.Recent());
        Assert.Equal("Everywhere", entry.Origin);
        Assert.Equal("text.generate", entry.Skill);
        Assert.Equal("ok", entry.Outcome);
        var json = Json(entry);
        Assert.DoesNotContain("Earth", json);
        Assert.DoesNotContain("Check it", json);
    }

    [Fact]
    public async Task TheCheckAsksForAStraightAnswerWithLittleCreativity()
    {
        var engine = Engine();
        await engine.GenerateOwnAsync([new ChatMessage("user", "hi")]);
        Assert.Equal(0.3, _model.LastRequest!.Temperature);
        Assert.False(_model.LastRequest.Thinking);
        Assert.True(_model.LastRequest.MaxTokens >= 1000);
    }

    [Fact]
    public async Task TheCheckNeverReachesAnAgentEvenWhenItIsFirstInTheRoute()
    {
        var agent = new FakeAgentSource("agent:claude");
        var engine = Engine(text: [agent, _model]);
        _store.Update(s =>
        {
            var chain = s.Chain(Vocabulary.Generate);
            chain.Stops.RemoveAll(x => x.SourceId == agent.Id);
            chain.Stops.Insert(0, new ChainStop { SourceId = agent.Id });      // the visitor put his agent first
        });

        var result = await engine.GenerateOwnAsync([new ChatMessage("user", "hi")]);

        Assert.True(result.Ok);
        Assert.Equal("The Earth is round.", result.Text);
        Assert.Equal(0, agent.Calls);
        Assert.Equal(1, _model.Calls);
    }

    [Fact]
    public async Task OnlyAnAgentMeansNoModelAndTheAgentIsNotAsked()
    {
        var agent = new FakeAgentSource("agent:claude");
        var engine = Engine(text: [agent]);
        var result = await engine.GenerateOwnAsync([new ChatMessage("user", "hi")]);
        Assert.False(result.Ok);
        Assert.Contains("No text model is set up", result.Error);
        Assert.Equal(0, agent.Calls);
    }

    [Fact]
    public async Task ASecondModelAnswersWhenTheFirstFails()
    {
        _model.Fail = true;
        var second = new FakeTextSource("t-second") { Answers = new Queue<string>(["From the second."]) };
        var engine = Engine(text: [_model, second]);
        var result = await engine.GenerateOwnAsync([new ChatMessage("user", "hi")]);
        Assert.True(result.Ok);
        Assert.Equal("From the second.", result.Text);
        Assert.Contains("FakeText t-local", _log.Recent()[0].Fallback);
    }

    [Fact]
    public async Task APaidTextModelIsNotUsedUnlessAllowedAndThenItAsks()
    {
        _model.Fail = true;
        var engine = Engine();
        var refused = await engine.GenerateOwnAsync([new ChatMessage("user", "hi")]);
        Assert.False(refused.Ok);
        Assert.Equal(0, _paidModel.Calls);

        _store.Update(s => s.Everywhere.AllowPaid = true);
        _ui.Paid = PaidAnswer.Once;
        var allowed = await engine.GenerateOwnAsync([new ChatMessage("user", "hi")]);
        Assert.True(allowed.Ok);
        Assert.Equal("From the cloud.", allowed.Text);
        Assert.Equal(1, _ui.PaidQuestions);
        Assert.Equal("Everywhere", _ui.PaidModels.Single().Host);
    }

    [Fact]
    public async Task AFreeDemoModelAnswersTheCheckWhenNoModelOfTheVisitorsDoesAndNothingIsAsked()
    {
        _model.Fail = true;
        var demo = new FakeTextSource("t-demo", Runs.ApiKey) { Answers = new Queue<string>(["From the demo."]) };
        var engine = Engine(text: [_model, demo, _paidModel]);

        var result = await engine.GenerateOwnAsync([new ChatMessage("user", "hi")]);

        Assert.True(result.Ok);
        Assert.Equal("From the demo.", result.Text);
        Assert.Equal(1, demo.Calls);
        Assert.Equal(0, _paidModel.Calls);                  // a free demo is not a paid source
        Assert.Equal(0, _ui.PaidQuestions);
    }

    [Fact]
    public async Task WithFreeCloudSwitchedOffAFreeDemoModelIsNotReached()
    {
        _model.Fail = true;
        var demo = new FakeTextSource("t-demo", Runs.ApiKey) { Answers = new Queue<string>(["From the demo."]) };
        _store.Update(s => s.Everywhere.AllowFreeCloud = false);
        var engine = Engine(text: [_model, demo]);

        var result = await engine.GenerateOwnAsync([new ChatMessage("user", "hi")]);

        Assert.False(result.Ok);
        Assert.Equal(0, demo.Calls);
    }

    [Fact]
    public async Task NoTextModelAtAllIsAPlainMessage()
    {
        var engine = Engine(text: []);
        var result = await engine.GenerateOwnAsync([new ChatMessage("user", "hi")]);
        Assert.False(result.Ok);
        Assert.Equal("No text model is set up. Add one in My models.", result.Error);
    }

    [Fact]
    public async Task EmptyMessagesAreRefused()
    {
        var engine = Engine();
        Assert.False((await engine.GenerateOwnAsync([])).Ok);
        Assert.False((await engine.GenerateOwnAsync([new ChatMessage("user", "")])).Ok);
        Assert.Equal(0, _model.Calls);
    }

    [Fact]
    public async Task ATextBeyondTheWalletsInputLimitIsRefused()
    {
        _store.Update(s => s.Limits.TextInputChars = 1_000);
        var engine = Engine();
        var result = await engine.GenerateOwnAsync([new ChatMessage("user", new string('x', 1_500))]);
        Assert.False(result.Ok);
        Assert.Contains("too long", result.Error);
        Assert.Equal(0, _model.Calls);
    }

    // ------------------------------------------------------------------ cancel and the adapter

    [Fact]
    public async Task ACancelledCallIsLoggedAndThrows()
    {
        var gate = new TaskCompletionSource();
        _model.Hold = gate;
        var engine = Engine();
        using var cts = new CancellationTokenSource();
        var call = engine.GenerateOwnAsync([new ChatMessage("user", "hi")], cts.Token);
        await Wait.Until(() => _model.Calls == 1);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        var entry = Assert.Single(_log.Recent());            // the call that ran is in the log once
        Assert.Equal("Everywhere", entry.Origin);
        Assert.NotEqual("ok", entry.Outcome);
    }

    [Fact]
    public async Task ACancelledSpeechCallIsLoggedOnceAndThrows()
    {
        var gate = new TaskCompletionSource();
        _local.Hold = gate;
        var engine = Engine();
        using var cts = new CancellationTokenSource();
        var call = engine.TranscribeOwnAsync(Seconds(1), null, cts.Token);
        await Wait.Until(() => _local.Calls == 1);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        var entry = Assert.Single(_log.Recent());
        Assert.Equal("Everywhere", entry.Origin);
        Assert.Equal("cancelled", entry.Note);
    }

    [Fact]
    public async Task TheOwnCallsInterfaceIsTheSamePath()
    {
        var engine = Engine();
        IOwnCalls own = engine.Own;
        Assert.Same(own, engine.Own);
        Assert.True((await own.TranscribeAsync(Seconds(1), null, CancellationToken.None)).Ok);
        Assert.True((await own.GenerateAsync([new ChatMessage("user", "hi")], CancellationToken.None)).Ok);
        Assert.Equal(2, _log.Recent().Count);
    }

    // ------------------------------------------------------------------ the words for the visitor

    [Theory]
    [InlineData("no source can do text.generate", "No text model is set up. Add one in My models.")]
    [InlineData("paid fallback not allowed", "A paid source was the only one left and you said no.")]
    public void KnownFailuresHavePlainSentences(string note, string sentence)
    {
        var entry = new ActivityEntry { Outcome = "failed", Note = note };
        Assert.Equal(sentence, WalletEngine.PlainOwnFailure(Vocabulary.Generate, entry, null));
    }

    [Fact]
    public void ALimitIsSaidAsALimitAndNotAsAGateCode()
    {
        foreach (var note in new[] { "money per day", "calls per day", "budget per session" })
        {
            var text = WalletEngine.PlainOwnFailure(Vocabulary.Generate, new ActivityEntry { Outcome = "limit", Note = note }, null);
            Assert.Contains("today's limit", text);
            Assert.DoesNotContain(note, text);
        }
    }

    [Fact]
    public void ALanguageNoModelHandlesIsNamed()
    {
        var text = WalletEngine.PlainOwnFailure(Vocabulary.Transcribe, new ActivityEntry { Outcome = "failed", Note = "no source for this language" }, "sk");
        Assert.Contains("sk", text);
        Assert.Contains("automatic", text);
    }
}
