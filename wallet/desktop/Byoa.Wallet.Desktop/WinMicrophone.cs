using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Byoa.Wallet.Everywhere;

namespace Byoa.Wallet.Desktop;

/// <summary>
/// The default recording device, through the old Windows <c>waveIn</c> calls, asked for 16 kHz mono 16-bit: the format the speech route takes, so that
/// Windows does the conversion from whatever the microphone gives and the wallet resamples nothing. Plain <c>waveIn</c> was chosen over a package
/// (NAudio would do the same through the same calls): it is about two hundred lines, it adds no dependency and no licence notice to the package, and
/// the wallet needs nothing beyond "record to memory until told to stop". Nothing is written to disk: the recording lives in memory and is gone
/// when the wallet has sent it to the visitor's own speech model. A buffer of 100 ms is handed back to the program as it fills; a recording stops
/// taking samples at the limit it was started with.
/// </summary>
[SupportedOSPlatform("windows")]
sealed class WinMicrophone : IMicrophone
{
    const int Rate = 16000;
    const int BufferSamples = 1600;          // 100 ms
    const int BufferCount = 4;
    const uint WaveMapper = 0xFFFFFFFF;
    const uint CallbackEvent = 0x00050000;
    const uint HeaderDone = 0x00000001;

    readonly object _gate = new();
    IntPtr _device;
    IntPtr[] _headers = [];
    IntPtr[] _data = [];
    ManualResetEventSlim? _signal;
    Thread? _thread;
    volatile bool _stopping;
    float[] _samples = [];
    int _count;

    public event Action<string>? Failed;

    public void Start(int maxSeconds)
    {
        lock (_gate)
        {
            if (_device != IntPtr.Zero) throw new MicrophoneException("A recording is already running.");
            _samples = new float[Math.Max(1, maxSeconds) * Rate];
            _count = 0;
            _stopping = false;
            _signal = new ManualResetEventSlim(false);
            var format = new WaveFormat { Tag = 1, Channels = 1, SamplesPerSec = Rate, AvgBytesPerSec = Rate * 2, BlockAlign = 2, BitsPerSample = 16, Size = 0 };
            var result = waveInOpen(out _device, WaveMapper, ref format, _signal.WaitHandle.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, CallbackEvent);
            if (result != 0)
            {
                _device = IntPtr.Zero;
                throw new MicrophoneException(Describe(result));
            }

            _headers = new IntPtr[BufferCount];
            _data = new IntPtr[BufferCount];
            for (var i = 0; i < BufferCount; i++)
            {
                _data[i] = Marshal.AllocHGlobal(BufferSamples * 2);
                var header = new WaveHeader { Data = _data[i], BufferLength = BufferSamples * 2 };
                _headers[i] = Marshal.AllocHGlobal(Marshal.SizeOf<WaveHeader>());
                Marshal.StructureToPtr(header, _headers[i], false);
                waveInPrepareHeader(_device, _headers[i], Marshal.SizeOf<WaveHeader>());
                waveInAddBuffer(_device, _headers[i], Marshal.SizeOf<WaveHeader>());
            }

            if (waveInStart(_device) != 0)
            {
                Release();
                throw new MicrophoneException("The microphone would not start.");
            }

            _thread = new Thread(Pump) { IsBackground = true, Name = "BYOA microphone" };
            _thread.Start();
        }
    }

    public float[] Stop()
    {
        var samples = Finish();
        if (samples.Length > 0 && samples.All(s => s == 0f))
            throw new MicrophoneException("The microphone gave only silence. Check that it is the default recording device, and that Windows lets desktop programs use it (Settings, Privacy, Microphone).");
        return samples;
    }

    public void Cancel() => Finish();

    /// <summary>Ends the recording and returns what was recorded (an empty array when none was running).</summary>
    float[] Finish()
    {
        Thread? thread;
        lock (_gate)
        {
            if (_device == IntPtr.Zero) return [];
            _stopping = true;
            thread = _thread;
            _thread = null;
            waveInStop(_device);
            waveInReset(_device);                 // hands every buffer back, the half-filled one too
            _signal?.Set();
        }

        thread?.Join(TimeSpan.FromSeconds(2));
        lock (_gate)
        {
            Drain();
            var result = _samples.AsSpan(0, _count).ToArray();
            Release();
            return result;
        }
    }

    void Pump()
    {
        while (!_stopping)
        {
            _signal!.Wait(100);
            _signal.Reset();
            bool alive;
            lock (_gate)
            {
                if (_stopping || _device == IntPtr.Zero) return;
                alive = Drain();
                if (!alive) _stopping = true;
            }

            if (alive) continue;
            Failed?.Invoke("the device stopped answering.");
            return;
        }
    }

    /// <summary>Takes the samples out of every buffer Windows has filled and hands the buffer back. False when the device stopped working.</summary>
    bool Drain()
    {
        for (var i = 0; i < _headers.Length; i++)
        {
            var header = Marshal.PtrToStructure<WaveHeader>(_headers[i]);
            if ((header.Flags & HeaderDone) == 0) continue;
            var samples = (int)header.BytesRecorded / 2;
            for (var s = 0; s < samples && _count < _samples.Length; s++)
                _samples[_count++] = Marshal.ReadInt16(header.Data, s * 2) / 32768f;
            header.Flags &= ~HeaderDone;
            header.BytesRecorded = 0;
            Marshal.StructureToPtr(header, _headers[i], false);
            if (!_stopping && waveInAddBuffer(_device, _headers[i], Marshal.SizeOf<WaveHeader>()) != 0) return false;
        }

        return true;
    }

    void Release()
    {
        for (var i = 0; i < _headers.Length; i++)
        {
            if (_device != IntPtr.Zero) waveInUnprepareHeader(_device, _headers[i], Marshal.SizeOf<WaveHeader>());
            Marshal.FreeHGlobal(_headers[i]);
            Marshal.FreeHGlobal(_data[i]);
        }

        _headers = [];
        _data = [];
        if (_device != IntPtr.Zero) waveInClose(_device);
        _device = IntPtr.Zero;
        _signal?.Dispose();
        _signal = null;
    }

    static string Describe(uint error) => error switch
    {
        2 or 6 or 7 => "No microphone was found. Plug one in, or set a default recording device in Windows.",
        4 => "The microphone is in use by another program.",
        _ => "The microphone could not be opened (" + ErrorText(error) + ").",
    };

    static string ErrorText(uint error)
    {
        var text = new System.Text.StringBuilder(256);
        return waveInGetErrorText(error, text, text.Capacity) == 0 ? text.ToString() : "error " + error;
    }

    internal static int HeaderSize => Marshal.SizeOf<WaveHeader>();

    internal static int FormatSize => Marshal.SizeOf<WaveFormat>();

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    struct WaveFormat
    {
        public ushort Tag, Channels;
        public uint SamplesPerSec, AvgBytesPerSec;
        public ushort BlockAlign, BitsPerSample, Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WaveHeader
    {
        public IntPtr Data;
        public uint BufferLength, BytesRecorded;
        public IntPtr User;
        public uint Flags, Loops;
        public IntPtr Next, Reserved;
    }

    [DllImport("winmm.dll")] static extern uint waveInOpen(out IntPtr device, uint deviceId, ref WaveFormat format, IntPtr callback, IntPtr instance, uint flags);
    [DllImport("winmm.dll")] static extern uint waveInClose(IntPtr device);
    [DllImport("winmm.dll")] static extern uint waveInPrepareHeader(IntPtr device, IntPtr header, int size);
    [DllImport("winmm.dll")] static extern uint waveInUnprepareHeader(IntPtr device, IntPtr header, int size);
    [DllImport("winmm.dll")] static extern uint waveInAddBuffer(IntPtr device, IntPtr header, int size);
    [DllImport("winmm.dll")] static extern uint waveInStart(IntPtr device);
    [DllImport("winmm.dll")] static extern uint waveInStop(IntPtr device);
    [DllImport("winmm.dll")] static extern uint waveInReset(IntPtr device);
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] static extern uint waveInGetErrorText(uint error, System.Text.StringBuilder text, int size);
}
