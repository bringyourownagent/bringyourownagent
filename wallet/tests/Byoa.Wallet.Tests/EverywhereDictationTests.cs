using Byoa.Wallet.Everywhere;
using Byoa.Wallet.Settings;

namespace Byoa.Wallet.Tests;

/// <summary>The state machine of dictation: hold to talk, press to toggle, cancel, a recording that is too long, nothing heard, a route that fails.</summary>
public class EverywhereDictationTests
{
    readonly FakeMicrophone _mic = new() { Recording = new float[16000] };
    readonly FakeOwn _own = new();
    readonly RecordingInserter _inserter = new();
    readonly RecordingIndicator _indicator = new();
    readonly EverywhereSettings _settings = new() { DictationOn = true };
    readonly List<Exception> _errors = [];

    DictationController Make(DictationOptions? options = null) =>
        new(_mic, _own, _inserter, _indicator, () => _settings, options, _errors.Add);

    static async Task Idle(DictationController c) => await Wait.Until(() => c.Current == DictationController.Phase.Idle);

    // ------------------------------------------------------------------ hold to talk

    [Fact]
    public async Task HoldToTalkRecordsWhileHeldAndWritesOnRelease()
    {
        var c = Make();
        await c.PressAsync();
        Assert.Equal(DictationController.Phase.Listening, c.Current);
        Assert.Equal((IndicatorKind.Listening, "Listening…"), _indicator.Last);
        Assert.Equal(1, _mic.Starts);
        Assert.Empty(_own.SpeechCalls);                     // nothing is sent while the visitor is still talking

        await c.ReleaseAsync();
        await Idle(c);
        Assert.Equal(["hello world"], _inserter.Inserted);
        Assert.Equal(16000, _own.SpeechCalls.Single().Samples);
        Assert.Contains(_indicator.Shown, s => s is (IndicatorKind.Writing, "Writing…"));
        Assert.True(_indicator.Hidden > 0);                 // the indicator goes away once the text is typed
        Assert.Empty(_errors);
    }

    [Fact]
    public async Task HoldToTalkIgnoresASecondPressWhileListening()
    {
        var c = Make();
        await c.PressAsync();
        await c.PressAsync();                               // key repeat that reached the machine
        Assert.Equal(DictationController.Phase.Listening, c.Current);
        Assert.Equal(1, _mic.Starts);
        Assert.Equal(0, _mic.Stops);
    }

    [Fact]
    public async Task AReleaseWithoutAPressDoesNothing()
    {
        var c = Make();
        await c.ReleaseAsync();
        Assert.Equal(DictationController.Phase.Idle, c.Current);
        Assert.Equal(0, _mic.Stops);
        Assert.Empty(_indicator.Shown);
    }

    [Fact]
    public async Task ARecordingShorterThanAFractionOfASecondIsASlipAndSendsNothing()
    {
        _mic.Recording = new float[1600];                   // 0.1 s
        var c = Make();
        await c.PressAsync();
        await c.ReleaseAsync();
        Assert.Equal(DictationController.Phase.Idle, c.Current);
        Assert.Empty(_own.SpeechCalls);
        Assert.Equal(IndicatorKind.Notice, _indicator.Last.Kind);
        Assert.Contains("Keep the keys down", _indicator.Last.Text);
    }

    // ------------------------------------------------------------------ press to start, press to stop

    [Fact]
    public async Task ToggleStartsOnOnePressAndStopsOnTheNext()
    {
        _settings.DictationMode = EverywhereSettings.PressToggle;
        var c = Make();
        await c.PressAsync();
        await c.ReleaseAsync();                             // letting go does not stop a toggle
        Assert.Equal(DictationController.Phase.Listening, c.Current);
        Assert.Equal(0, _mic.Stops);

        await c.PressAsync();
        await Idle(c);
        Assert.Equal(["hello world"], _inserter.Inserted);
    }

    [Fact]
    public async Task TheModeIsTakenWhenARecordingStartsSoAChangeMidRecordingDoesNotConfuseIt()
    {
        var c = Make();
        await c.PressAsync();                               // hold
        _settings.DictationMode = EverywhereSettings.PressToggle;
        await c.ReleaseAsync();
        await Idle(c);
        Assert.Single(_inserter.Inserted);
    }

    [Fact]
    public async Task AnUnknownModeBehavesAsHoldToTalk()
    {
        _settings.DictationMode = "something odd";
        var c = Make();
        await c.PressAsync();
        await c.ReleaseAsync();
        await Idle(c);
        Assert.Single(_inserter.Inserted);
    }

    // ------------------------------------------------------------------ cancel

    [Fact]
    public async Task EscWhileListeningDropsTheRecording()
    {
        var c = Make();
        await c.PressAsync();
        await c.CancelAsync();
        Assert.Equal(DictationController.Phase.Idle, c.Current);
        Assert.Equal(1, _mic.Cancels);
        Assert.Equal(0, _mic.Stops);
        Assert.Empty(_own.SpeechCalls);
        Assert.Empty(_inserter.Inserted);
        Assert.True(_indicator.Hidden > 0);

        await c.ReleaseAsync();                             // the release after the cancel finds nothing to stop
        Assert.Empty(_own.SpeechCalls);
    }

    [Fact]
    public async Task EscWhileWaitingForTheWordsWritesNothing()
    {
        var gate = new TaskCompletionSource<OwnResult>();
        _own.Speech = async (_, _, ct) => await gate.Task.WaitAsync(ct);
        var c = Make();
        await c.PressAsync();
        await c.ReleaseAsync();
        await Wait.Until(() => _own.SpeechCalls.Count == 1);
        Assert.Equal(DictationController.Phase.Writing, c.Current);

        await c.CancelAsync();
        await Idle(c);
        Assert.Empty(_inserter.Inserted);
        Assert.True(_indicator.Hidden > 0);
    }

    [Fact]
    public async Task AnAnswerThatArrivesAfterTheCancelIsNotTyped()
    {
        var gate = new TaskCompletionSource<OwnResult>();
        _own.Speech = (_, _, _) => gate.Task;               // a source that ignores the cancel and answers anyway
        var c = Make();
        await c.PressAsync();
        await c.ReleaseAsync();
        await Wait.Until(() => _own.SpeechCalls.Count == 1);
        await c.CancelAsync();
        gate.SetResult(OwnResult.Success("late words"));
        await Idle(c);
        Assert.Empty(_inserter.Inserted);
    }

    [Fact]
    public async Task TurningTheFeatureOffMidRecordingIsACancel()
    {
        var c = Make();
        await c.PressAsync();
        _settings.DictationOn = false;
        await c.CancelAsync();
        Assert.Equal(DictationController.Phase.Idle, c.Current);
        await c.PressAsync();                               // and a press while it is off starts nothing
        Assert.Equal(1, _mic.Starts);
    }

    // ------------------------------------------------------------------ a recording that is too long

    [Fact]
    public async Task ARecordingStopsByItselfAtTheLimitAndWhatWasSaidIsWritten()
    {
        var c = Make(new DictationOptions { MaxRecording = TimeSpan.FromMilliseconds(120) });
        await c.PressAsync();
        Assert.Equal(1, _mic.MaxSeconds);                   // the microphone is told the cap too (whole seconds, rounded up)
        await Wait.Until(() => _inserter.Inserted.Count == 1);
        Assert.Equal(1, _mic.Stops);
        Assert.Contains(_indicator.Shown, s => s.Kind == IndicatorKind.Writing && s.Text.Contains("limit"));
        await Idle(c);
    }

    [Fact]
    public async Task TheDefaultLimitIsSixtySeconds()
    {
        var c = Make();
        await c.PressAsync();
        Assert.Equal(60, _mic.MaxSeconds);
        await c.CancelAsync();
    }

    [Fact]
    public async Task TheReleaseAfterAnAutomaticStopDoesNotStartOrStopAnything()
    {
        var c = Make(new DictationOptions { MaxRecording = TimeSpan.FromMilliseconds(80) });
        await c.PressAsync();
        await Wait.Until(() => _inserter.Inserted.Count == 1);
        await Idle(c);
        await c.ReleaseAsync();
        Assert.Equal(1, _mic.Stops);
        Assert.Single(_own.SpeechCalls);
    }

    [Fact]
    public async Task TheLimitOfAnEarlierRecordingNeverStopsALaterOne()
    {
        var c = Make(new DictationOptions { MaxRecording = TimeSpan.FromMilliseconds(200) });
        await c.PressAsync();
        await c.ReleaseAsync();                             // stopped by hand, well before the limit
        await Idle(c);
        await Task.Delay(300);                              // the first recording's limit time has passed
        Assert.Equal(1, _mic.Stops);

        await c.PressAsync();                               // a second recording ends by its own limit
        await Wait.Until(() => _inserter.Inserted.Count == 2);
        Assert.Equal(2, _mic.Stops);
    }

    // ------------------------------------------------------------------ what comes back

    [Fact]
    public async Task NothingHeardIsToldAndNothingIsTyped()
    {
        _own.Speech = (_, _, _) => Task.FromResult(OwnResult.Success("   "));
        var c = Make();
        await c.PressAsync();
        await c.ReleaseAsync();
        await Idle(c);
        Assert.Empty(_inserter.Inserted);
        Assert.Equal(IndicatorKind.Notice, _indicator.Last.Kind);
        Assert.Contains("Heard nothing", _indicator.Last.Text);
    }

    [Fact]
    public async Task AnErrorFromTheRouteIsShownInPlainWords()
    {
        _own.Speech = (_, _, _) => Task.FromResult(OwnResult.Failure("No speech model answered. Llama · Qwen3-ASR: not running."));
        var c = Make();
        await c.PressAsync();
        await c.ReleaseAsync();
        await Idle(c);
        Assert.Empty(_inserter.Inserted);
        Assert.Equal((IndicatorKind.Error, "No speech model answered. Llama · Qwen3-ASR: not running."), _indicator.Last);
    }

    [Fact]
    public async Task AnExceptionInTheCallIsReportedWithItsTypeOnlyAndTheMachineGoesOn()
    {
        _own.Speech = (_, _, _) => throw new InvalidOperationException("secret words inside the message");
        var c = Make();
        await c.PressAsync();
        await c.ReleaseAsync();
        await Idle(c);
        Assert.Equal(IndicatorKind.Error, _indicator.Last.Kind);
        Assert.Contains("InvalidOperationException", _indicator.Last.Text);
        Assert.DoesNotContain("secret", _indicator.Last.Text);
        Assert.Single(_errors);

        _own.Speech = (_, _, _) => Task.FromResult(OwnResult.Success("again"));
        await c.PressAsync();
        await c.ReleaseAsync();
        await Idle(c);
        Assert.Equal(["again"], _inserter.Inserted);        // not stuck
    }

    [Fact]
    public async Task ATextThatCouldNotBeTypedIsAnErrorWithTheReason()
    {
        _inserter.Fail = new InsertException("the clipboard is busy");
        var c = Make();
        await c.PressAsync();
        await c.ReleaseAsync();
        await Idle(c);
        Assert.Equal(IndicatorKind.Error, _indicator.Last.Kind);
        Assert.Contains("could not be typed", _indicator.Last.Text);
        Assert.Contains("clipboard is busy", _indicator.Last.Text);
    }

    [Fact]
    public async Task TheTextIsTrimmedBeforeItIsTyped()
    {
        _own.Speech = (_, _, _) => Task.FromResult(OwnResult.Success("  hello there \n"));
        var c = Make();
        await c.PressAsync();
        await c.ReleaseAsync();
        await Idle(c);
        Assert.Equal(["hello there"], _inserter.Inserted);
    }

    // ------------------------------------------------------------------ the microphone

    [Fact]
    public async Task NoMicrophoneIsAPlainErrorAndNoRecordingStarts()
    {
        _mic.FailStart = true;
        var c = Make();
        await c.PressAsync();
        Assert.Equal(DictationController.Phase.Idle, c.Current);
        Assert.Equal((IndicatorKind.Error, "No microphone was found."), _indicator.Last);
        Assert.Empty(_own.SpeechCalls);
    }

    [Fact]
    public async Task AMicrophoneThatDiesMidRecordingEndsItWithAMessage()
    {
        var c = Make();
        await c.PressAsync();
        _mic.RaiseFailed("the device was unplugged");
        await c.SettledAsync();
        Assert.Equal(DictationController.Phase.Idle, c.Current);
        Assert.Equal(IndicatorKind.Error, _indicator.Last.Kind);
        Assert.Contains("unplugged", _indicator.Last.Text);
        Assert.Equal(1, _mic.Cancels);

        await c.ReleaseAsync();
        Assert.Empty(_own.SpeechCalls);
    }

    // ------------------------------------------------------------------ settings, one thing at a time

    [Fact]
    public async Task WithDictationOffAPressDoesNothingAtAll()
    {
        _settings.DictationOn = false;
        var c = Make();
        await c.PressAsync();
        Assert.Equal(0, _mic.Starts);
        Assert.Empty(_indicator.Shown);
    }

    [Fact]
    public async Task TheLanguageIsPassedOnAndEmptyMeansAutomatic()
    {
        var c = Make();
        await c.PressAsync();
        await c.ReleaseAsync();
        await Idle(c);
        Assert.Null(_own.SpeechCalls[0].Language);          // automatic by default

        _settings.DictationLanguage = " ru ";
        await c.PressAsync();
        await c.ReleaseAsync();
        await Idle(c);
        Assert.Equal("ru", _own.SpeechCalls[1].Language);

        _settings.DictationLanguage = "  ";
        await c.PressAsync();
        await c.ReleaseAsync();
        await Idle(c);
        Assert.Null(_own.SpeechCalls[2].Language);
    }

    [Fact]
    public async Task APressWhileTheTextIsBeingWrittenIsIgnored()
    {
        var gate = new TaskCompletionSource<OwnResult>();
        _own.Speech = (_, _, _) => gate.Task;
        var c = Make();
        await c.PressAsync();
        await c.ReleaseAsync();
        await Wait.Until(() => c.Current == DictationController.Phase.Writing && _own.SpeechCalls.Count == 1);

        await c.PressAsync();
        await c.ReleaseAsync();
        Assert.Equal(1, _mic.Starts);                       // no second recording on top of the first
        gate.SetResult(OwnResult.Success("one"));
        await Idle(c);
        Assert.Equal(["one"], _inserter.Inserted);
    }

    [Fact]
    public async Task TheWalletIsBusyFromThePressUntilTheTextIsWritten()
    {
        var c = Make();
        Assert.False(c.Busy);
        await c.PressAsync();
        Assert.True(c.Busy);                                // Esc belongs to the wallet now
        await c.ReleaseAsync();
        await Idle(c);
        Assert.False(c.Busy);
    }

    [Fact]
    public async Task TheRecognisedTextIsNeverPutInAnErrorOrTheIndicatorBeyondTheTypedText()
    {
        _own.Speech = (_, _, _) => Task.FromResult(OwnResult.Success("my password is hunter2"));
        _inserter.Fail = new InsertException("the clipboard is busy");
        var c = Make();
        await c.PressAsync();
        await c.ReleaseAsync();
        await Idle(c);
        lock (_indicator.Shown) Assert.All(_indicator.Shown, s => Assert.DoesNotContain("hunter2", s.Text));
        Assert.Empty(_errors);
    }
}
