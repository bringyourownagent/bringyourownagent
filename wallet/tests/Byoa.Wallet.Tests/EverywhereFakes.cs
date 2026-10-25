using System.Text;
using Byoa.Wallet.Everywhere;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Tests;

/// <summary>A keyboard hook the test drives by hand: <see cref="Send"/> is what the system would do for a key.</summary>
sealed class FakeKeyboardHook : IKeyboardHook
{
    Func<KeyEvent, bool>? _handler;
    public int Starts { get; private set; }
    public int Stops { get; private set; }
    public bool Running => _handler is not null;

    public void Start(Func<KeyEvent, bool> handler)
    {
        Starts++;
        _handler = handler;
    }

    public void Stop()
    {
        Stops++;
        _handler = null;
    }

    /// <summary>One key event; true when the hook would keep it from the program with the focus.</summary>
    public bool Send(int virtualKey, bool down, bool injected = false) =>
        _handler is { } handler && handler(new KeyEvent(virtualKey, down, injected));

    public void Dispose() => _handler = null;

    // the keys of the default chord
    public const int LCtrl = 0xA2, LAlt = 0xA4, LShift = 0xA0, Space = 0x20, K = 0x4B;
}

/// <summary>A keyboard that records what the wallet sent; a test can make a program react to a key (a copy).</summary>
sealed class FakeKeyboard : IKeyboard
{
    public List<string> Sent { get; } = [];
    public KeySet Down { get; } = new();
    public Action<HotkeyModifiers, int>? OnChord { get; set; }

    public void Chord(HotkeyModifiers modifiers, int virtualKey)
    {
        Sent.Add(new Hotkey(modifiers, virtualKey).ToString());
        OnChord?.Invoke(modifiers, virtualKey);
    }

    public void TypeText(string text) => Sent.Add("type:" + text);

    public bool IsDown(int virtualKey) => Down.Contains(virtualKey);

    public void KeyUp(int virtualKey)
    {
        Down.Remove(virtualKey);
        Sent.Add("up:" + virtualKey.ToString("X2"));
    }

    public void MaskMenuKey() => Sent.Add("mask");
}

/// <summary>The keys held down; a test lets go of them from another thread while the wallet waits.</summary>
sealed class KeySet
{
    readonly HashSet<int> _keys = [];

    public void Add(int key)
    {
        lock (_keys) _keys.Add(key);
    }

    public void Remove(int key)
    {
        lock (_keys) _keys.Remove(key);
    }

    public void Clear()
    {
        lock (_keys) _keys.Clear();
    }

    public bool Contains(int key)
    {
        lock (_keys) return _keys.Contains(key);
    }

    public int Count
    {
        get { lock (_keys) return _keys.Count; }
    }
}

/// <summary>A clipboard held in memory, with a sequence number like the real one; it can be made busy.</summary>
sealed class FakeClipboard : IClipboard
{
    const uint UnicodeText = 13;
    public long Seq { get; set; } = 100;
    public List<ClipboardItem> Items { get; set; } = [];
    public bool Busy { get; set; }
    public bool? LastKeepOut { get; private set; }
    public List<string> Log { get; } = [];

    public long SequenceNumber => Seq;

    public string? Text
    {
        get => Items.FirstOrDefault(i => i.Format == UnicodeText) is { } item ? Encoding.Unicode.GetString(item.Data) : null;
        set => Items = value is null ? [] : [new ClipboardItem(UnicodeText, Encoding.Unicode.GetBytes(value))];
    }

    public ClipboardSnapshot Save()
    {
        if (Busy) throw new ClipboardException("another program holds the clipboard");
        Log.Add("save");
        return new ClipboardSnapshot(Items.ToList());
    }

    public void Restore(ClipboardSnapshot snapshot)
    {
        if (Busy) throw new ClipboardException("another program holds the clipboard");
        Log.Add("restore");
        Items = snapshot.Items.ToList();
        Seq++;
    }

    public string? GetText()
    {
        if (Busy) throw new ClipboardException("another program holds the clipboard");
        return Text;
    }

    public void SetText(string text, bool keepOut)
    {
        if (Busy) throw new ClipboardException("another program holds the clipboard");
        Log.Add("set");
        Text = text;
        LastKeepOut = keepOut;
        Seq++;
    }
}

sealed class FakeMicrophone : IMicrophone
{
    public float[] Recording { get; set; } = new float[16000];
    public bool FailStart { get; set; }
    public int Starts { get; private set; }
    public int Stops { get; private set; }
    public int Cancels { get; private set; }
    public int? MaxSeconds { get; private set; }

    public event Action<string>? Failed;

    public void Start(int maxSeconds)
    {
        if (FailStart) throw new MicrophoneException("No microphone was found.");
        Starts++;
        MaxSeconds = maxSeconds;
    }

    public float[] Stop()
    {
        Stops++;
        return Recording;
    }

    public void Cancel() => Cancels++;

    public void RaiseFailed(string message) => Failed?.Invoke(message);
}

/// <summary>The wallet's own calls with answers a test chooses.</summary>
sealed class FakeOwn : IOwnCalls
{
    public Func<float[], string?, CancellationToken, Task<OwnResult>> Speech { get; set; } = (_, _, _) => Task.FromResult(OwnResult.Success("hello world"));
    public Func<IReadOnlyList<ChatMessage>, CancellationToken, Task<OwnResult>> Text { get; set; } = (_, _) => Task.FromResult(OwnResult.Success("checked"));
    public List<(int Samples, string? Language)> SpeechCalls { get; } = [];
    public List<IReadOnlyList<ChatMessage>> TextCalls { get; } = [];

    public Task<OwnResult> TranscribeAsync(float[] samples, string? language, CancellationToken ct)
    {
        lock (SpeechCalls) SpeechCalls.Add((samples.Length, language));
        return Speech(samples, language, ct);
    }

    public Task<OwnResult> GenerateAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct)
    {
        lock (TextCalls) TextCalls.Add(messages);
        return Text(messages, ct);
    }
}

sealed class RecordingIndicator : IIndicator
{
    public List<(IndicatorKind Kind, string Text)> Shown { get; } = [];
    public int Hidden { get; private set; }

    public void Show(IndicatorKind kind, string text)
    {
        lock (Shown) Shown.Add((kind, text));
    }

    public void Hide() => Hidden++;

    public (IndicatorKind Kind, string Text) Last
    {
        get { lock (Shown) return Shown[^1]; }
    }
}

sealed class RecordingInserter : ITextInserter
{
    public List<string> Inserted { get; } = [];
    public Exception? Fail { get; set; }
    public TaskCompletionSource? Hold { get; set; }

    public async Task InsertAsync(string text, CancellationToken ct)
    {
        if (Hold is { } hold) await hold.Task.WaitAsync(ct);
        if (Fail is { } fail) throw fail;
        lock (Inserted) Inserted.Add(text);
    }
}

sealed class FakeTargetWindow : ITargetWindow
{
    public TargetWindow? Focused { get; set; } = new(1001, false);
    public bool CanRestore { get; set; } = true;
    public List<TargetWindow> Restored { get; } = [];

    public TargetWindow? Capture() => Focused;

    public Task<bool> RestoreAsync(TargetWindow window, CancellationToken ct)
    {
        Restored.Add(window);
        return Task.FromResult(CanRestore);
    }
}

sealed class FakePopup : ICheckPopup
{
    public List<CheckView> Views { get; } = [];
    public int Closed { get; private set; }

    public void Show(CheckView view)
    {
        lock (Views) Views.Add(view);
    }

    public void Close() => Closed++;

    public CheckView Last
    {
        get { lock (Views) return Views[^1]; }
    }
}

sealed class FakeSelectionReader : ISelectionReader
{
    public string? Text { get; set; } = "The Earth is flat.";
    public Exception? Fail { get; set; }
    public List<TargetWindow?> Targets { get; } = [];

    public Task<string?> ReadAsync(TargetWindow? target, CancellationToken ct)
    {
        Targets.Add(target);
        if (Fail is { } fail) throw fail;
        return Task.FromResult(Text);
    }
}

sealed class FakeHotkeyProbe : IHotkeyProbe
{
    public HashSet<string> Taken { get; } = [];
    public List<string> Probed { get; } = [];

    public HotkeyProbeResult Probe(Hotkey hotkey)
    {
        Probed.Add(hotkey.ToString());
        return Taken.Contains(hotkey.ToString()) ? new HotkeyProbeResult(false, "the hotkey is already registered") : new HotkeyProbeResult(true);
    }
}

static class Wait
{
    /// <summary>Polls until the condition holds (the state machines run their slow parts beside the test).</summary>
    public static async Task Until(Func<bool> condition, int milliseconds = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("the condition did not hold in " + milliseconds + " ms");
            await Task.Delay(5);
        }
    }
}
