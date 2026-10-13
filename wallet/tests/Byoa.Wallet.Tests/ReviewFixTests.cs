using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Byoa.Wallet.Activity;
using Byoa.Wallet.Audio;
using Byoa.Wallet.Engine;
using Byoa.Wallet.Host;
using Byoa.Wallet.Protocol;
using Byoa.Wallet.Providers;
using Byoa.Wallet.Settings;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Tests;

/// <summary>
/// The findings of the external review of wallet 0.1, one or more tests each (numbers as in the review), and the
/// wallet side of the cold start: a grant resolves only when the routed models are loaded.
/// </summary>
public class ReviewFixTests
{
    const string Site = "https://geo.example.org";

    readonly ManualTime _time = new();
    readonly ScriptedUi _ui = new();
    readonly FakeSource _giga = new("gigaam-v3", languages: ["ru"]) { Answer = "francia-giga" };
    readonly FakeSource _whisper = new("whisper") { Answer = "francia-whisper" };
    readonly FakeSource _paid = new("openrouter-scribe", Runs.ApiKey, pricePerSecond: 0.00004m) { Answer = "francia-paid" };
    readonly SettingsStore _store = new(null);
    readonly ActivityLog _log;
    readonly WalletEngine _engine;

    public ReviewFixTests()
    {
        _log = new ActivityLog(null, _time.Now);
        _engine = new WalletEngine(_store, [_giga, _whisper, _paid], _log, _ui, _time);
    }

    static string Manifest(string cost = "0.05 USD", string extraSkill = "") =>
        "{\"byoa\":\"0.1-draft\",\"purpose\":{\"summary\":\"Family geography game\",\"limits\":{\"maxClipSeconds\":15,\"maxCallsPerMinute\":40}},"
        + "\"skills\":{\"audio.transcribe\":{\"languages\":[\"ru\",\"sk\"],\"required\":true}" + extraSkill + "},"
        + "\"privacy\":\"personal-voice\",\"expectedCost\":{\"perSession\":\"" + cost + "\"}}";

    static string Only(string language) =>
        "{\"byoa\":\"0.1-draft\",\"purpose\":{\"summary\":\"Family geography game\",\"limits\":{\"maxClipSeconds\":15,\"maxCallsPerMinute\":40}},"
        + "\"skills\":{\"audio.transcribe\":{\"languages\":[\"" + language + "\"],\"required\":true}},"
        + "\"privacy\":\"personal-voice\",\"expectedCost\":{\"perSession\":\"0.05 USD\"}}";

    async Task<string> GrantAsync(WalletEngine? engine = null, string manifest = Clips.GeoManifest)
    {
        var result = await (engine ?? _engine).HandleAsync(Clips.Request(Site, click: true, manifest));
        return Assert.IsType<JsonObject>(result)["grantId"]!.GetValue<string>();
    }

    static bool Ok(JsonNode? result) => result is JsonObject o && o["ok"]?.GetValue<bool>() == true;

    static async Task WaitUntil(Func<bool> condition, int seconds = 10)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(seconds)) throw new TimeoutException("the condition did not become true");
            await Task.Delay(10);
        }
    }

    // ------------------------------------------------------------------ #3 daily limits count calls in flight

    [Fact]
    public async Task R3_CallsInFlightCountAgainstTheDailyCallLimit()
    {
        _store.Update(s => s.Limits.SiteCallsPerDay = 2);
        var grant = await GrantAsync();
        _giga.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Five calls at once while none has finished: two run, three are refused at once (not five that run).
        var calls = Enumerable.Range(0, 5).Select(_ => _engine.HandleAsync(Clips.Call(Site, grant))).ToList();
        await WaitUntil(() => calls.Count(c => c.IsCompleted) == 3);
        Assert.All(calls.Where(c => c.IsCompleted), c => Assert.False(Ok(c.Result)));
        Assert.Equal("limit", _log.Recent()[0].Outcome);

        _giga.Hold.SetResult();
        var results = await Task.WhenAll(calls).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, results.Count(Ok));
        Assert.Equal(2, _giga.Calls);
        Assert.False(Ok(await _engine.HandleAsync(Clips.Call(Site, grant))));     // and still 2 a day after they finished
    }

    [Fact]
    public async Task R3_PaidCallsInFlightCountAgainstTheDailyMoneyLimit()
    {
        _giga.Fail = true;
        _whisper.Fail = true;
        _ui.Paid = PaidAnswer.Always;
        _store.Update(s => s.Limits.SitePerDay = "0.0001 USD");                   // one 2-second paid clip (0.00008), not two
        var grant = await GrantAsync();
        _paid.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = _engine.HandleAsync(Clips.Call(Site, grant));
        await WaitUntil(() => _paid.Calls == 1);
        var second = await _engine.HandleAsync(Clips.Call(Site, grant)).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(Ok(second));                                                  // refused while the first is still running
        Assert.Equal("money per day", _log.Recent()[0].Note);
        Assert.False(first.IsCompleted);

        _paid.Hold.SetResult();
        Assert.True(Ok(await first.WaitAsync(TimeSpan.FromSeconds(10))));
        Assert.Equal(1, _paid.Calls);
    }

    // ------------------------------------------------------------------ #4 an unserved required skill is never silent

    [Fact]
    public async Task R4_ANewRequiredSkillNoSourceCanServeIsNeverGrantedSilently()
    {
        _ui.Consent = new ConsentAnswer(true, 0.05m, AutoDecide: true, []);
        await GrantAsync();
        var notices = _ui.Notices;
        var more = Manifest(extraSkill: ",\"text.generate\":{\"required\":true}");

        // "Decide on my own" is on, but the grant does not cover a required skill: no silent grant, only the badge.
        Assert.Null(await _engine.HandleAsync(Clips.Request(Site, click: false, more)));
        Assert.Equal("held", _log.Recent()[0].Outcome);
        Assert.Equal(notices, _ui.Notices);

        // After a click the visitor sees the missing skill, Allow is not offered, and the page gets null.
        Assert.Null(await _engine.HandleAsync(Clips.Request(Site, click: true, more)));
        var consent = _ui.Consents[^1];
        Assert.True(consent.IsDifference);
        Assert.True(consent.MissingRequired);
        Assert.Equal(["text.generate"], consent.Rows.Select(r => r.Skill));

        // The site's old grant is unchanged: the same manifest as before is still granted on its own.
        Assert.NotNull(await _engine.HandleAsync(Clips.Request(Site, click: false)));
        Assert.Equal("auto", _log.Recent()[0].Outcome);
    }

    // ------------------------------------------------------------------ #5 WAV chunk sizes cannot overflow

    static byte[] WithJunkChunk(uint size)
    {
        var wav = Wav.Encode(new float[1600], 16000);
        var junk = new byte[12];
        Encoding.ASCII.GetBytes("JUNK").CopyTo(junk, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(junk.AsSpan(4), size);
        return [.. wav.AsSpan(0, 12), .. junk, .. wav.AsSpan(12)];
    }

    [Theory]
    [InlineData(0x7FFFFFF0u)]          // body + size wrapped to a negative offset before the fix
    [InlineData(0x7FFFFFFFu)]
    [InlineData(0xFFFFFFF0u)]
    [InlineData(5000u)]
    public void R5_AChunkThatRunsPastTheEndIsBadAudioNotAnOverflow(uint size)
    {
        var e = Assert.Throws<InvalidDataException>(() => Wav.Parse(WithJunkChunk(size)));
        Assert.Contains("JUNK", e.Message);
        Assert.Equal(0.1, Wav.Parse(WithJunkChunk(4)).Seconds, 3);                 // a real 4-byte JUNK chunk is skipped
    }

    [Fact]
    public async Task R5_TheEngineAnswersAnOverflowingClipAsBadAudio()
    {
        var grant = await GrantAsync();
        var result = await _engine.HandleAsync(Clips.Call(Site, grant, base64: Convert.ToBase64String(WithJunkChunk(0x7FFFFFF0u))));
        Assert.Equal("{\"ok\":false}", result!.ToJsonString());
        var entry = _log.Recent()[0];
        Assert.Equal("invalid", entry.Outcome);
        Assert.StartsWith("not a WAV clip: the JUNK chunk", entry.Note);
        Assert.Equal(0, _giga.Calls);
    }

    // ------------------------------------------------------------------ #7 a shared question outlives the first tab

    [Fact]
    public async Task R7_ClosingTheFirstTabDoesNotCloseTheQuestionTheSecondTabWaitsFor()
    {
        _ui.ConsentGate = new TaskCompletionSource<ConsentAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var tabA = new CancellationTokenSource();
        var a = _engine.HandleAsync(Clips.Request(Site, click: true), tabA.Token);
        var b = _engine.HandleAsync(Clips.Request(Site, click: true));
        await WaitUntil(() => _ui.Consents.Count == 1);

        tabA.Cancel();
        Assert.Null(await a.WaitAsync(TimeSpan.FromSeconds(10)));
        await Task.Delay(100);
        Assert.False(_ui.ConsentToken.IsCancellationRequested);                   // the window stays open for tab B
        Assert.False(b.IsCompleted);

        _ui.ConsentGate.SetResult(new ConsentAnswer(true, 0.05m, false, []));
        var grant = Assert.IsType<JsonObject>(await b.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.NotNull(grant["grantId"]);
        Assert.Equal(1, _ui.Notices);                                             // one question for both tabs
    }

    [Fact]
    public async Task R7_ARequestRightAfterAnAnsweredOneGetsItsOwnQuestion()
    {
        // The answered question leaves the shared table a moment later; a request in between must not reuse its answer.
        for (var i = 0; i < 200; i++)
        {
            _ui.Notice = i % 2 == 0 ? NoticeAnswer.Decline : NoticeAnswer.Closed;
            Assert.Null(await _engine.HandleAsync(Clips.Request(Site, click: true)));
            Assert.Equal(i + 1, _ui.Notices);
            _engine.Revoke(Site);
        }
    }

    [Fact]
    public async Task R7_WhenEveryTabWaitingHasGoneTheQuestionCloses()
    {
        _ui.ConsentGate = new TaskCompletionSource<ConsentAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var tabA = new CancellationTokenSource();
        using var tabB = new CancellationTokenSource();
        var a = _engine.HandleAsync(Clips.Request(Site, click: true), tabA.Token);
        var b = _engine.HandleAsync(Clips.Request(Site, click: true), tabB.Token);
        await WaitUntil(() => _ui.Consents.Count == 1);
        tabA.Cancel();
        tabB.Cancel();
        Assert.Null(await a.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Null(await b.WaitAsync(TimeSpan.FromSeconds(10)));
        await WaitUntil(() => _ui.ConsentToken.IsCancellationRequested);
        await WaitUntil(() => _log.Recent().Any(e => e.Outcome == "closed"));
        Assert.NotEqual(SiteStatus.Allowed, _store.Read(s => s.Site(Site)!.Status));
    }

    // ------------------------------------------------------------------ #8 the budget difference

    [Fact]
    public async Task R8_ACostRiseWithinTheBudgetIsCoveredAndAboveItShowsBudgetToCost()
    {
        _ui.Consent = new ConsentAnswer(true, 0.50m, AutoDecide: true, []);       // the visitor raised the budget to $0.50
        await GrantAsync(manifest: Manifest(cost: "0.10 USD"));
        _ui.Consent = null;
        var consents = _ui.Consents.Count;

        // The site now expects $0.25: still within the visitor's $0.50, so no question at all ("$0.50 -> $0.50" before).
        Assert.NotNull(await _engine.HandleAsync(Clips.Request(Site, click: false, Manifest(cost: "0.25 USD"))));
        Assert.Equal("auto", _log.Recent()[0].Outcome);
        Assert.Equal(consents, _ui.Consents.Count);

        // $0.60 is above it: the difference shows "budget $0.50 -> $0.60".
        Assert.NotNull(await _engine.HandleAsync(Clips.Request(Site, click: true, Manifest(cost: "0.60 USD"))));
        var diff = _ui.Consents[^1];
        Assert.True(diff.IsDifference);
        Assert.Empty(diff.Rows);
        Assert.Equal(0.50m, diff.BudgetBefore);
        Assert.Equal(0.60m, diff.Budget);
    }

    // ------------------------------------------------------------------ #9 a GigaAM model that does not load

    [Fact]
    public async Task R9_AGigaAmModelThatFailsToLoadIsBrokenAndCallsFallBack()
    {
        var folder = TestFolder.New();                                            // a folder without the model files
        using var giga = new GigaAmSource(() => folder);
        Assert.Equal(SourceState.Ready, giga.State);                              // not loaded yet: it looks usable
        await Assert.ThrowsAsync<InvalidOperationException>(() => giga.TranscribeAsync(new float[1600], "ru", default));
        Assert.Equal(SourceState.Broken, giga.State);                             // was Ready forever before the fix
        Assert.StartsWith("failed to load", giga.StateText);

        // In the engine: a fresh broken model is skipped, Russian falls back to the next source.
        using var cold = new GigaAmSource(() => folder);
        var whisper = new FakeSource("whisper") { Answer = "francia-whisper" };
        var engine = new WalletEngine(_store, [cold, whisper], _log, _ui, _time);
        var grant = await GrantAsync(engine);
        for (var i = 0; i < 2; i++)
        {
            var result = await engine.HandleAsync(Clips.Call(Site, grant, language: "ru"));
            Assert.Equal("francia-whisper", result!["output"]!["text"]!.GetValue<string>());
        }

        Assert.Equal(SourceState.Broken, cold.State);
        Assert.Equal(2, whisper.Calls);
    }

    // ------------------------------------------------------------------ #10 the uninstall header

    [Fact]
    public void R10_TheUninstallHeaderNamesTheOneFileItDeletes()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "uninstall.ps1"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var script = File.ReadAllText(Path.Combine(dir.FullName, "uninstall.ps1"));
        var header = script[..script.IndexOf("#>", StringComparison.Ordinal)];
        Assert.DoesNotContain("does not delete files", header);
        Assert.Contains("Startup shortcut", header);
        Assert.Contains("deletes no files", header);
        Assert.Single(Regex.Matches(script, @"Remove-Item\b"));                    // the only file it removes: the shortcut
        Assert.Matches(@"Remove-Item -LiteralPath \$link\b", script);
    }

    [Fact]
    public void R9_TheInstallerKeepsThePackageAndSourceLicenseFiles()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "install.ps1"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var script = File.ReadAllText(Path.Combine(dir.FullName, "install.ps1"));

        Assert.Contains("Copy-FileIfPresent (Join-Path $root 'LICENSE')", script, StringComparison.Ordinal);
        Assert.Contains("Copy-FileIfPresent (Join-Path $root 'THIRD-PARTY-NOTICES.txt')", script, StringComparison.Ordinal);
        Assert.Contains("Copy-Folder $packageLicenses", script, StringComparison.Ordinal);
        Assert.Contains("$root 'packaging\\THIRD-PARTY-NOTICES.txt'", script, StringComparison.Ordinal);
        Assert.Contains("Copied $copied files.", script, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ #2 cold start, wallet side

    [Fact]
    public async Task R2_AllowResolvesOnlyWhenTheRoutedModelsAreLoaded()
    {
        var giga = new WarmingSource("gigaam-v3", ["ru"]) { WarmTime = TimeSpan.FromMilliseconds(400), Answer = "francia-giga" };
        var whisper = new WarmingSource("whisper") { WarmTime = TimeSpan.FromMilliseconds(900) };
        var engine = new WalletEngine(_store, [giga, whisper, _paid], _log, _ui, _time);

        var watch = Stopwatch.StartNew();
        await GrantAsync(engine);
        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(850), watch.Elapsed.ToString());
        Assert.Equal(["ru"], giga.Warmed);                                          // GigaAM for Russian, Whisper for Slovak
        Assert.Equal(["sk"], whisper.Warmed);
        var preparing = Assert.Single(_ui.Preparing);                             // "Preparing your model…" meanwhile
        Assert.Equal(["Fake gigaam-v3", "Fake whisper"], preparing.Sources);
        Assert.Equal(1, _ui.PreparingClosed);
        Assert.Matches(@"models ready after \d\.\d s", _log.Recent()[0].Note);

        // Loaded once: the next Allow is not held and shows no "Preparing".
        watch.Restart();
        await GrantAsync(engine);
        Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(400), watch.Elapsed.ToString());
        Assert.Single(_ui.Preparing);
    }

    [Fact]
    public async Task R2_A_SlowDownloadKeepsAllowWaitingAndReportsProgress()
    {
        var slow = new SlowDownloadSource("slow-whisper", ["ru"], TimeSpan.FromMilliseconds(180));
        var store = new SettingsStore(null);
        var ui = new ScriptedUi();
        var log = new ActivityLog(null, _time.Now);
        var engine = new WalletEngine(store, [slow], log, ui, _time);
        store.Update(s => s.Chain(Vocabulary.Transcribe).Stops.Insert(0, new ChainStop { SourceId = slow.Id }));

        var watch = Stopwatch.StartNew();
        var result = await engine.HandleAsync(Clips.Request(Site, click: true, Only("ru")));

        Assert.NotNull(result);
        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(300), watch.Elapsed.ToString());
        Assert.Equal(SourceState.Ready, slow.State);
        Assert.Contains(ui.PreparingUpdates, update => update.Progress is >= 0.40 and <= 0.42);
        Assert.Contains(ui.Preparing, update => update.Progress is not null);
        Assert.Equal(1, ui.PreparingClosed);
    }

    [Fact]
    public async Task R2_A_SlowModelIsNotReleasedByTheOldWarmTimeout()
    {
        var saved = WalletEngine.WarmWait;
        WalletEngine.WarmWait = TimeSpan.FromSeconds(1);
        try
        {
            var giga = new WarmingSource("gigaam-v3", ["ru"]) { WarmTime = TimeSpan.FromMilliseconds(1400) };
            var engine = new WalletEngine(_store, [giga, _whisper], _log, _ui, _time);
            var watch = Stopwatch.StartNew();
            await GrantAsync(engine, Only("ru")).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.InRange(watch.Elapsed.TotalSeconds, 1.2, 5);
            Assert.Single(_ui.Preparing);
            Assert.Equal(1, _ui.PreparingClosed);
        }
        finally
        {
            WalletEngine.WarmWait = saved;
        }
    }

    [Fact]
    public async Task R2_ASilentGrantIsAlsoLoadedFirstWithoutAToast()
    {
        _ui.Consent = new ConsentAnswer(true, 0.05m, AutoDecide: true, []);
        await GrantAsync(new WalletEngine(_store, [new WarmingSource("gigaam-v3", ["ru"]), _whisper], _log, _ui, _time), Only("ru"));
        var shown = _ui.Preparing.Count;

        // The wallet restarted: the model is cold again. The silent grant waits for it, and shows nothing.
        var giga = new WarmingSource("gigaam-v3", ["ru"]) { WarmTime = TimeSpan.FromMilliseconds(500) };
        var engine = new WalletEngine(_store, [giga, _whisper], _log, _ui, _time);
        var watch = Stopwatch.StartNew();
        Assert.NotNull(await engine.HandleAsync(Clips.Request(Site, click: false, Only("ru"))));
        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(450), watch.Elapsed.ToString());
        Assert.Equal(["ru"], giga.Warmed);
        Assert.Equal(shown, _ui.Preparing.Count);
        Assert.Equal("auto", _log.Recent()[0].Outcome);
        Assert.Contains("models ready after", _log.Recent()[0].Note);
    }
}

/// <summary>#1: a wallet pipe that is already closed must not take the native host down.</summary>
public class HostDisposedPipeTests
{
    /// <summary>A wallet connection whose pipe was closed under the relay: every write throws ObjectDisposedException.</summary>
    sealed class ClosedPipe : Stream
    {
        readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new ObjectDisposedException("pipe");
        public override Task FlushAsync(CancellationToken ct) => throw new ObjectDisposedException("pipe");
        public override int Read(byte[] buffer, int offset, int count) => throw new ObjectDisposedException("pipe");

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await _closed.Task.WaitAsync(ct);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override void Write(byte[] buffer, int offset, int count) => throw new ObjectDisposedException("pipe");
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => throw new ObjectDisposedException("pipe");
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => throw new ObjectDisposedException("pipe");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            _closed.TrySetResult();
            base.Dispose(disposing);
        }
    }

    [Fact]
    public async Task R1_AWalletPipeAlreadyClosedAnswersTheFallbackAndTheHostKeepsRunning()
    {
        var (browser, host) = HostRelayTests.Pair();
        var connector = new HostRelayTests.Connector(_ => new ClosedPipe());
        var relay = Task.Run(() => new HostRelay(host, host, connector).RunAsync());

        await Framing.WriteAsync(browser, HostRelayTests.Message(5, "call"));
        Assert.Equal("{\"id\":5,\"result\":{\"ok\":false}}", (await HostRelayTests.ReadAsync(browser)).ToJsonString());
        await Framing.WriteAsync(browser, HostRelayTests.Message(6, "request"));
        Assert.Equal("{\"id\":6,\"result\":null}", (await HostRelayTests.ReadAsync(browser)).ToJsonString());

        browser.Dispose();
        await relay.WaitAsync(TimeSpan.FromSeconds(10));                          // ended normally, not with an exception
        Assert.True(relay.IsCompletedSuccessfully);
    }
}

/// <summary>#6: the wallet serves one connection with a bound and backpressure.</summary>
public class FrameServerTests
{
    /// <summary>Counts the bytes read from the inner stream.</summary>
    sealed class Counting(Stream inner) : Stream
    {
        long _read;
        public long BytesRead => Interlocked.Read(ref _read);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var n = await inner.ReadAsync(buffer, ct);
            Interlocked.Add(ref _read, n);
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => inner.WriteAsync(buffer, ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    [Fact]
    public async Task R6_AtMostNMessagesRunAndTheNextFrameIsNotReadUntilOneEnds()
    {
        var (client, server) = HostRelayTests.Pair();
        var counting = new Counting(server);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int running = 0, most = 0, started = 0;
        var frames = new FrameServer(async (message, ct) =>
        {
            var now = Interlocked.Increment(ref running);
            int seen;
            while ((seen = Volatile.Read(ref most)) < now && Interlocked.CompareExchange(ref most, now, seen) != seen)
            {
            }

            Interlocked.Increment(ref started);
            await gate.Task.WaitAsync(ct);
            Interlocked.Decrement(ref running);
            return new JsonObject { ["ok"] = true };
        }, maxParallel: 3);
        var serving = Task.Run(() => frames.ServeAsync(counting, CancellationToken.None));

        var size = 4 + HostRelayTests.Message(11, "call").Length;
        for (var id = 11; id <= 20; id++) await Framing.WriteAsync(client, HostRelayTests.Message(id, "call"));
        await WaitUntil(() => Volatile.Read(ref started) == 3);
        await Task.Delay(300);
        Assert.Equal(3, Volatile.Read(ref started));
        Assert.Equal(3 * size, counting.BytesRead);                              // the 4th frame is still in the pipe

        gate.SetResult();
        var ids = new List<int>();
        for (var i = 0; i < 10; i++) ids.Add((await HostRelayTests.ReadAsync(client))["id"]!.GetValue<int>());
        Assert.Equal(Enumerable.Range(11, 10), ids.Order());
        Assert.True(Volatile.Read(ref most) <= 3, "at most 3 at once, was " + most);

        client.Dispose();
        await serving.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task R6_ClosingTheConnectionCancelsWhatIsStillRunning()
    {
        var (client, server) = HostRelayTests.Pair();
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var frames = new FrameServer(async (message, ct) =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                cancelled.TrySetResult();
                throw;
            }

            return null;
        });
        var serving = Task.Run(() => frames.ServeAsync(server, CancellationToken.None));
        await Framing.WriteAsync(client, HostRelayTests.Message(1, "request"));
        await Task.Delay(100);
        client.Dispose();
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await serving.WaitAsync(TimeSpan.FromSeconds(10));
    }

    static async Task WaitUntil(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException();
            await Task.Delay(10);
        }
    }
}
