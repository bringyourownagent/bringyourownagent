using System.Text.Json.Nodes;
using Byoa.Wallet.Activity;
using Byoa.Wallet.Audio;
using Byoa.Wallet.Engine;
using Byoa.Wallet.Protocol;
using Byoa.Wallet.Settings;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Tests;

/// <summary>A clock the tests move by hand.</summary>
sealed class ManualTime : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>A source with fixed properties that answers a fixed text, or fails.</summary>
class FakeSource : ISpeechSource
{
    public FakeSource(string id, string runs = Sources.Runs.ThisDevice, IReadOnlyList<string>? languages = null, decimal pricePerSecond = 0)
    {
        Id = id;
        Runs = runs;
        ProviderKind = runs == Sources.Runs.ApiKey ? "api-key" : "local-model";
        Languages = languages;
        PricePerSecond = pricePerSecond;
    }

    public string Id { get; }
    public string Name => "Fake " + Id;
    public string Runs { get; }
    public string ProviderKind { get; set; } = "local-model";
    public IReadOnlyList<string>? Languages { get; }
    public int LanguageCount => Languages?.Count ?? 99;
    public decimal PricePerSecond { get; }
    public bool Paid => PricePerSecond > 0;
    public double ApproxSeconds => 0.1;
    public bool HasMeasuredSeconds { get; set; }
    public SourceState State { get; set; } = SourceState.Ready;
    public string StateText => State.ToString();
    public string Answer { get; set; } = "francia";
    public bool Fail { get; set; }
    int _calls;
    public int Calls => Volatile.Read(ref _calls);
    public string? LastLanguage { get; private set; }

    /// <summary>When set, calls wait for it before they answer: calls held in flight.</summary>
    public TaskCompletionSource? Hold { get; set; }

    public decimal EstimateCost(double clipSeconds) => (decimal)Math.Ceiling(clipSeconds) * PricePerSecond;

    public async Task<Transcript> TranscribeAsync(float[] samples, string? language, CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        LastLanguage = language;
        if (Hold is { } hold) await hold.Task.WaitAsync(ct);
        if (Fail) throw new InvalidOperationException("fake failure");
        return new Transcript(Answer, EstimateCost(samples.Length / 16000.0));
    }
}

/// <summary>The test stand-in for what <c>SourceSet</c> does at start: every speech source gets a stop in the route.</summary>
static class SpeechRoutes
{
    /// <summary>Appends a stop for each source that has none, in the order given (a paid one goes last, as in a real wallet).</summary>
    public static void Ensure(WalletSettings settings, IEnumerable<ISpeechSource> sources)
    {
        var chain = settings.Chain(Vocabulary.Transcribe);
        foreach (var source in sources)
        {
            if (chain.Stops.Any(s => s.SourceId == source.Id)) continue;
            var paid = sources.FirstOrDefault(x => chain.Stops.Any(s => s.SourceId == x.Id) && x.Paid);
            var index = paid is null ? chain.Stops.Count : chain.Stops.FindIndex(s => s.SourceId == paid.Id);
            chain.Stops.Insert(index, new ChainStop { SourceId = source.Id });
        }
    }

    /// <summary>An engine over these speech sources, with a route that lists them in the order given.</summary>
    public static WalletEngine Engine(SettingsStore store, IReadOnlyList<ISpeechSource> sources, ActivityLog log, IWalletUi ui,
        TimeProvider? time = null)
    {
        store.Update(s => Ensure(s, sources));
        return new WalletEngine(store, sources, log, ui, time);
    }
}

/// <summary>A fake source with a model to load: warming a language takes <see cref="WarmTime"/> the first time.</summary>
sealed class WarmingSource(string id, IReadOnlyList<string>? languages = null) : FakeSource(id, languages: languages), IWarmable
{
    public TimeSpan WarmTime { get; set; } = TimeSpan.FromMilliseconds(500);
    public List<string?> Warmed { get; } = [];

    public async Task WarmAsync(string? language)
    {
        lock (Warmed)
        {
            if (Warmed.Contains(language)) return;
        }

        await Task.Delay(WarmTime);
        lock (Warmed) Warmed.Add(language);
    }
}

/// <summary>A text source with fixed answers (one per call, the last repeats) or failures.</summary>
class FakeTextSource : ITextSource, IStreamingTextSource, ILiveness
{
    public FakeTextSource(string id, string runs = Sources.Runs.ThisDevice, decimal costPerCall = 0)
    {
        Id = id;
        Runs = runs;
        CostPerCall = costPerCall;
        ProviderKind = runs == Sources.Runs.ApiKey ? "api-key" : "local-model";
    }

    public string Id { get; }
    public string Name => "FakeText " + Id;
    public string Runs { get; }
    public string ProviderKind { get; set; } = "local-model";
    public IReadOnlyList<string>? Languages { get; set; }
    public bool Paid => CostPerCall > 0;
    public SourceState State { get; set; } = SourceState.Ready;
    public string StateText => State.ToString();
    public decimal CostPerCall { get; }
    public bool? Up { get; set; } = true;

    public Queue<string> Answers { get; set; } = new();
    /// <summary>Optional scripted provider outcomes, including typed paid failures.</summary>
    public Queue<Func<TextRequest, TextAnswer>>? Outcomes { get; set; }
    public bool Fail { get; set; }
    int _calls;
    public int Calls => Volatile.Read(ref _calls);
    public TextRequest? LastRequest { get; private set; }

    /// <summary>When set, calls wait for it before they answer: calls held in flight.</summary>
    public TaskCompletionSource? Hold { get; set; }

    public decimal EstimateCost(TextRequest request) => CostPerCall;

    public Task<TextAnswer> GenerateAsync(TextRequest request, CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        LastRequest = request;
        if (Hold is { } hold) return HeldAsync(hold, request, ct);
        return Task.FromResult(Answer(request));
    }

    /// <summary>Streams the answer a word at a time, as a model would.</summary>
    public async Task<TextAnswer> GenerateStreamAsync(TextRequest request, Action<string> onText, CancellationToken ct)
    {
        var answer = await GenerateAsync(request, ct);
        foreach (var piece in answer.Text.Split(' ').Select((w, i) => (i == 0 ? "" : " ") + w)) onText(piece);
        return answer;
    }

    async Task<TextAnswer> HeldAsync(TaskCompletionSource hold, TextRequest request, CancellationToken ct)
    {
        await hold.Task.WaitAsync(ct);
        return Answer(request);
    }

    TextAnswer Answer(TextRequest request)
    {
        if (Outcomes is { Count: > 0 })
        {
            var outcome = Outcomes.Count > 1 ? Outcomes.Dequeue() : Outcomes.Peek();
            return outcome(request);
        }
        if (Fail) throw new InvalidOperationException("fake text failure");
        var text = Answers.Count > 1 ? Answers.Dequeue() : Answers.Peek();
        return new TextAnswer(text, CostPerCall, 1, 1, "fake");
    }
}

/// <summary>A visitor who answers the wallet's windows with preset answers, and counts what was shown.</summary>
sealed class ScriptedUi : IWalletUi
{
    public NoticeAnswer Notice { get; set; } = NoticeAnswer.Show;
    public ConsentAnswer? Consent { get; set; }
    public PaidAnswer Paid { get; set; } = PaidAnswer.No;
    public int Notices { get; private set; }
    public List<NoticeModel> NoticeModels { get; } = [];
    public List<ConsentModel> Consents { get; } = [];
    public int PaidQuestions { get; private set; }
    public List<PaidModel> PaidModels { get; } = [];
    public int Quiet { get; private set; }
    public List<InfoModel> Infos { get; } = [];

    public Task<NoticeAnswer> ShowNoticeAsync(NoticeModel model, CancellationToken ct)
    {
        Notices++;
        NoticeModels.Add(model);
        return Task.FromResult(Notice);
    }

    /// <summary>When set, the consent window stays open until it is completed or the wallet closes it.</summary>
    public TaskCompletionSource<ConsentAnswer>? ConsentGate { get; set; }

    /// <summary>The token the wallet gave the last consent window: cancelled means the wallet closed it.</summary>
    public CancellationToken ConsentToken { get; private set; }

    public async Task<ConsentAnswer> ShowConsentAsync(ConsentModel model, CancellationToken ct)
    {
        // A link has no separate notice any more (it goes straight to the consent window, whose Decline is "not now"). The tests count a link's ask here and
        // read a preset Notice other than Show as "the visitor said not now", as the notice used to say.
        if (model.FromLink)
        {
            Notices++;
            NoticeModels.Add(new NoticeModel(model.Origin, model.Host, FromLink: true));
            if (Notice != NoticeAnswer.Show) return ConsentAnswer.Declined;
        }

        lock (Consents) Consents.Add(model);
        ConsentToken = ct;
        if (ConsentGate is { } gate)
        {
            try
            {
                return await gate.Task.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return ConsentAnswer.Declined;                  // the window was closed by the wallet
            }
        }

        return Consent ?? new ConsentAnswer(true, model.Budget, false, []);
    }

    public Task<PaidAnswer> AskPaidAsync(PaidModel model, CancellationToken ct)
    {
        PaidQuestions++;
        PaidModels.Add(model);
        return Task.FromResult(Paid);
    }

    public void ShowInfo(InfoModel model) => Infos.Add(model);

    /// <summary>The limit questions shown, and the raise the visitor picks: its index among the offered raises, or null for «No».</summary>
    public List<RaiseModel> RaiseModels { get; } = [];
    public int? RaisePick { get; set; }

    public Task<decimal?> AskRaiseAsync(RaiseModel model, CancellationToken ct)
    {
        lock (RaiseModels) RaiseModels.Add(model);
        return Task.FromResult(RaisePick is { } n && n < model.Raises.Count ? model.Raises[n].Value : (decimal?)null);
    }

    /// <summary>What the guard showed the visitor, and what he answers (one answer per question, then "refuse").</summary>
    public List<GuardPrompt> GuardPrompts { get; } = [];
    public Queue<GuardAnswer> GuardAnswers { get; } = new();

    public Task<GuardAnswer> AskGuardAsync(GuardPrompt model, CancellationToken ct)
    {
        lock (GuardPrompts) GuardPrompts.Add(model);
        return Task.FromResult(GuardAnswers.Count > 0 ? GuardAnswers.Dequeue() : GuardAnswer.Refuse);
    }

    /// <summary>Every "Preparing your model…" shown, and whether it was closed again.</summary>
    public List<PreparingModel> Preparing { get; } = [];
    public int PreparingClosed;

    /// <summary>When set, the window that shows "Preparing your model…" fails.</summary>
    public bool FailPreparing { get; set; }

    public IDisposable ShowPreparing(PreparingModel model)
    {
        lock (Preparing) Preparing.Add(model);
        if (FailPreparing) throw new InvalidOperationException("the window failed");
        return new Closer(this);
    }

    sealed class Closer(ScriptedUi ui) : IDisposable
    {
        public void Dispose() => Interlocked.Increment(ref ui.PreparingClosed);
    }

    public void QuietRequest(string origin) => Quiet++;
    public void OpenSettings(string? page = null) { }
}

static class Clips
{
    /// <summary>A 16 kHz mono 16-bit WAV of <paramref name="seconds"/> of a quiet tone, as base64.</summary>
    public static string Base64(double seconds)
    {
        var samples = new float[(int)(seconds * 16000)];
        for (var i = 0; i < samples.Length; i++) samples[i] = 0.1f * MathF.Sin(i * 0.05f);
        return Convert.ToBase64String(Wav.Encode(samples, 16000));
    }

    public const string GeoManifest = """
        {
          "byoa": "0.1-draft",
          "purpose": { "summary": "Family geography game: recognise country names spoken aloud", "limits": { "maxClipSeconds": 15, "maxCallsPerMinute": 40 } },
          "skills": { "audio.transcribe": { "languages": ["ru", "sk"], "required": true } },
          "privacy": "personal-voice",
          "expectedCost": { "perSession": "0.05 USD" }
        }
        """;

    public static WireMessage Request(string origin, bool click, string manifest = GeoManifest) =>
        new(1, Wire.Request, origin, click, new JsonObject { ["manifest"] = JsonNode.Parse(manifest) });

    public static WireMessage Call(string origin, string grantId, double seconds = 2, string? language = "ru", string? base64 = null) =>
        new(2, Wire.Call, origin, false, new JsonObject
        {
            ["grantId"] = grantId,
            ["skill"] = "audio.transcribe",
            ["input"] = new JsonObject { ["audio"] = new JsonObject { ["mime"] = "audio/wav", ["base64"] = base64 ?? Base64(seconds) } },
            ["params"] = language is null ? new JsonObject() : new JsonObject { ["language"] = language },
        });
}
