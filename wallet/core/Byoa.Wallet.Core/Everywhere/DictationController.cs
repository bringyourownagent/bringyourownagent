using Byoa.Wallet.Audio;
using Byoa.Wallet.Settings;

namespace Byoa.Wallet.Everywhere;

public sealed record DictationOptions
{
    /// <summary>One recording stops by itself after this long, and what was said until then is written.</summary>
    public TimeSpan MaxRecording { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>A recording shorter than this is a slip of the finger: nothing is sent.</summary>
    public double MinSeconds { get; init; } = 0.3;
}

/// <summary>
/// Dictation: the hotkey starts a recording, the recording is recognised by the wallet's own speech route, and the text is typed into the field that
/// has the focus. Two ways to drive it (the visitor's choice): <i>hold to talk</i> records while the hotkey is held and writes on release, <i>press
/// to start, press to stop</i> toggles. Esc cancels a recording, or a recognition that is still running. One thing at a time: a hotkey while the text
/// is being written does nothing.
/// Every command goes through one queue, so the machine needs no locks; the slow parts (the call, the typing) run beside it and report back through it.
/// Nothing is recorded unless <see cref="EverywhereSettings.DictationOn"/> is on and the hotkey is pressed, the audio goes only to the visitor's own
/// sources by <see cref="IOwnCalls"/>, and the recognised text is never logged.
/// </summary>
public sealed class DictationController
{
    public enum Phase
    {
        Idle,
        Listening,
        Writing,
    }

    readonly IMicrophone _microphone;
    readonly IOwnCalls _own;
    readonly ITextInserter _inserter;
    readonly IIndicator _indicator;
    readonly Func<EverywhereSettings> _settings;
    readonly DictationOptions _options;
    readonly Action<Exception>? _onError;
    readonly SerialQueue _queue;

    volatile Phase _phase;
    string _mode = EverywhereSettings.HoldToTalk;
    string? _language;
    int _recording;                       // which recording the cap timer belongs to
    CancellationTokenSource? _cap;
    CancellationTokenSource? _write;

    public DictationController(IMicrophone microphone, IOwnCalls own, ITextInserter inserter, IIndicator indicator,
        Func<EverywhereSettings> settings, DictationOptions? options = null, Action<Exception>? onError = null)
    {
        _microphone = microphone;
        _own = own;
        _inserter = inserter;
        _indicator = indicator;
        _settings = settings;
        _options = options ?? new DictationOptions();
        _onError = onError;
        _queue = new SerialQueue(onError);
        _microphone.Failed += message => _ = _queue.Post(() => MicrophoneFailed(message));
    }

    public Phase Current => _phase;

    /// <summary>True while a recording or a recognition is going on: the time when Esc belongs to the wallet.</summary>
    public bool Busy => _phase != Phase.Idle;

    /// <summary>The hotkey went down.</summary>
    public Task PressAsync() => _queue.Post(PressedAsync);

    /// <summary>The hotkey was let go (only <i>hold to talk</i> cares).</summary>
    public Task ReleaseAsync() => _queue.Post(ReleasedAsync);

    /// <summary>Esc, or the feature was turned off: drop the recording, or stop waiting for the recognition.</summary>
    public Task CancelAsync() => _queue.Post(Cancelled);

    /// <summary>For tests and for a shutdown: ends when everything queued so far has run.</summary>
    public Task SettledAsync() => _queue.Post(() => { });

    async Task PressedAsync()
    {
        switch (_phase)
        {
            case Phase.Idle:
                var settings = _settings();
                if (!settings.DictationOn) return;
                try
                {
                    _microphone.Start((int)Math.Ceiling(_options.MaxRecording.TotalSeconds));
                }
                catch (MicrophoneException e)
                {
                    _indicator.Show(IndicatorKind.Error, e.Message);
                    return;
                }

                _mode = settings.DictationMode == EverywhereSettings.PressToggle ? EverywhereSettings.PressToggle : EverywhereSettings.HoldToTalk;
                _language = string.IsNullOrWhiteSpace(settings.DictationLanguage) ? null : settings.DictationLanguage.Trim();
                _phase = Phase.Listening;
                _indicator.Show(IndicatorKind.Listening, "Listening…");
                _cap = new CancellationTokenSource();
                _ = CapAsync(++_recording, _cap.Token);
                break;
            case Phase.Listening when _mode == EverywhereSettings.PressToggle:
                await StopAsync(capped: false).ConfigureAwait(false);
                break;
        }
    }

    Task ReleasedAsync() => _phase == Phase.Listening && _mode == EverywhereSettings.HoldToTalk ? StopAsync(capped: false) : Task.CompletedTask;

    void Cancelled()
    {
        switch (_phase)
        {
            case Phase.Listening:
                _cap?.Cancel();
                _microphone.Cancel();
                _phase = Phase.Idle;
                _indicator.Hide();
                break;
            case Phase.Writing:
                _write?.Cancel();
                break;
        }
    }

    void MicrophoneFailed(string message)
    {
        if (_phase != Phase.Listening) return;
        _cap?.Cancel();
        _microphone.Cancel();
        _phase = Phase.Idle;
        _indicator.Show(IndicatorKind.Error, "The microphone stopped working: " + message);
    }

    async Task CapAsync(int recording, CancellationToken ct)
    {
        try
        {
            await Task.Delay(_options.MaxRecording, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await _queue.Post(async () =>
        {
            if (_phase == Phase.Listening && _recording == recording) await StopAsync(capped: true).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    Task StopAsync(bool capped)
    {
        _cap?.Cancel();
        _cap = null;
        float[] samples;
        try
        {
            samples = _microphone.Stop();
        }
        catch (MicrophoneException e)
        {
            _phase = Phase.Idle;
            _indicator.Show(IndicatorKind.Error, e.Message);
            return Task.CompletedTask;
        }

        if (samples.Length < _options.MinSeconds * Wav.TargetRate)
        {
            _phase = Phase.Idle;
            _indicator.Show(IndicatorKind.Notice, _mode == EverywhereSettings.HoldToTalk
                ? "Too short. Keep the keys down while you speak."
                : "Too short. Speak, then press the keys again.");
            return Task.CompletedTask;
        }

        _phase = Phase.Writing;
        _indicator.Show(IndicatorKind.Writing, capped
            ? "Writing… (stopped at the " + (int)_options.MaxRecording.TotalSeconds + " second limit)"
            : "Writing…");
        var write = new CancellationTokenSource();
        _write = write;
        _ = WriteAsync(samples, _language, write);
        return Task.CompletedTask;
    }

    async Task WriteAsync(float[] samples, string? language, CancellationTokenSource write)
    {
        try
        {
            var result = await _own.TranscribeAsync(samples, language, write.Token).ConfigureAwait(false);
            write.Token.ThrowIfCancellationRequested();
            if (!result.Ok)
            {
                _indicator.Show(IndicatorKind.Error, result.Error ?? "The speech model did not answer.");
                return;
            }

            var text = result.Text.Trim();
            if (text.Length == 0)
            {
                _indicator.Show(IndicatorKind.Notice, "Heard nothing. Speak closer to the microphone and try again.");
                return;
            }

            await _inserter.InsertAsync(text, write.Token).ConfigureAwait(false);
            _indicator.Hide();
        }
        catch (OperationCanceledException)
        {
            _indicator.Hide();
        }
        catch (InsertException e)
        {
            _indicator.Show(IndicatorKind.Error, "The text could not be typed: " + e.Message);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _onError?.Invoke(e);
            _indicator.Show(IndicatorKind.Error, "Dictation failed (" + e.GetType().Name + "). The wallet's error log has the details.");
        }
        finally
        {
            await _queue.Post(() =>
            {
                if (ReferenceEquals(_write, write)) _write = null;
                _phase = Phase.Idle;
                write.Dispose();
            }).ConfigureAwait(false);
        }
    }
}
