using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Byoa.Wallet.Everywhere;

namespace Byoa.Wallet.Desktop;

/// <summary>
/// The low-level keyboard hook (<c>WH_KEYBOARD_LL</c>): the only way to see a key being let go in every program, which hold to talk needs. It runs on a
/// thread of its own with its own message loop, so that a busy window of the wallet never slows the visitor's typing everywhere, and never the other
/// way round. The handler must return at once; an exception in it is swallowed (a hook that throws is removed by Windows). It is installed only
/// while a hotkey is set, and is the only place that sees keys: it keeps no text, only key codes, and hands them to <see cref="HotkeyMonitor"/>.
/// </summary>
[SupportedOSPlatform("windows")]
sealed class WinKeyboardHook : IKeyboardHook
{
    const int WhKeyboardLl = 13;
    const int WmKeyDown = 0x0100, WmKeyUp = 0x0101, WmSysKeyDown = 0x0104, WmSysKeyUp = 0x0105, WmQuit = 0x0012;
    const int LlkhfInjected = 0x10, LlkhfLowerIlInjected = 0x02;

    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    readonly object _gate = new();
    HookProc? _proc;                       // kept in a field: the system calls it for as long as the hook lives
    Thread? _thread;
    uint _threadId;
    volatile Func<KeyEvent, bool>? _handler;

    public void Start(Func<KeyEvent, bool> handler)
    {
        lock (_gate)
        {
            _handler = handler;
            if (_thread is not null) return;
            using var ready = new ManualResetEventSlim();
            Exception? failure = null;
            _thread = new Thread(() => Run(ready, e => failure = e)) { IsBackground = true, Name = "BYOA keyboard hook" };
            _thread.Start();
            if (!ready.Wait(TimeSpan.FromSeconds(3))) failure ??= new TimeoutException("the keyboard hook did not start");
            if (failure is not null)
            {
                _thread = null;
                _handler = null;
                throw failure;
            }
        }
    }

    void Run(ManualResetEventSlim ready, Action<Exception> fail)
    {
        _threadId = GetCurrentThreadId();
        _proc = Callback;
        var hook = SetWindowsHookEx(WhKeyboardLl, _proc, GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero)
        {
            fail(new Win32Exception(Marshal.GetLastWin32Error()));
            ready.Set();
            return;
        }

        ready.Set();
        while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }

        UnhookWindowsHookEx(hook);
    }

    IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            try
            {
                var message = (int)wParam;
                var down = message is WmKeyDown or WmSysKeyDown;
                if ((down || message is WmKeyUp or WmSysKeyUp) && _handler is { } handler)
                {
                    // KBDLLHOOKSTRUCT: DWORD vkCode at 0, DWORD scanCode at 4, DWORD flags at 8
                    var key = Marshal.ReadInt32(lParam, 0);
                    var flags = Marshal.ReadInt32(lParam, 8);
                    var injected = (flags & (LlkhfInjected | LlkhfLowerIlInjected)) != 0;
                    if (handler(new KeyEvent(key, down, injected))) return (IntPtr)1;
                }
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // never let an exception go back into the system
            }
        }

        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    public void Stop()
    {
        Thread? thread;
        uint id;
        lock (_gate)
        {
            _handler = null;
            thread = _thread;
            id = _threadId;
            _thread = null;
        }

        if (thread is null) return;
        PostThreadMessage(id, WmQuit, IntPtr.Zero, IntPtr.Zero);
        thread.Join(TimeSpan.FromSeconds(2));
    }

    public void Dispose() => Stop();

    [StructLayout(LayoutKind.Sequential)]
    struct Point { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    struct Msg
    {
        public IntPtr Window;
        public uint Message;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public Point Pt;
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int idHook, HookProc proc, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern int GetMessage(out Msg message, IntPtr window, uint min, uint max);

    [DllImport("user32.dll")]
    static extern bool TranslateMessage(ref Msg message);

    [DllImport("user32.dll")]
    static extern IntPtr DispatchMessage(ref Msg message);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool PostThreadMessage(uint thread, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandle(string? name);
}
