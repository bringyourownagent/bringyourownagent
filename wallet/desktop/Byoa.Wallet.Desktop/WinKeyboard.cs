using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Byoa.Wallet.Everywhere;

namespace Byoa.Wallet.Desktop;

/// <summary>
/// Keystrokes for the program in front, by <c>SendInput</c>. A chord or a piece of text goes in one call, so that nothing the visitor types lands in the
/// middle of it. The wallet's own keys carry the "injected" mark, which the keyboard hook uses to leave them alone. Windows refuses them (and says
/// so by sending none) when the program in front runs as administrator and the wallet does not; that is reported, not guessed at.
/// </summary>
[SupportedOSPlatform("windows")]
sealed class WinKeyboard : IKeyboard
{
    const uint InputKeyboard = 1;
    const uint KeyUpFlag = 0x0002, ExtendedKeyFlag = 0x0001, UnicodeFlag = 0x0004;
    const int VkReturn = 0x0D, VkTab = 0x09, VkUnassigned = 0xE8;
    const int ChunkChars = 200;

    /// <summary>The size of one INPUT the way the wallet lays it out; Windows refuses a call whose size is not its own (40 bytes on 64-bit Windows).</summary>
    internal static int InputSize => Marshal.SizeOf<Input>();

    public void Chord(HotkeyModifiers modifiers, int virtualKey)
    {
        var keys = new List<int>();
        if (modifiers.HasFlag(HotkeyModifiers.Ctrl)) keys.Add(0x11);
        if (modifiers.HasFlag(HotkeyModifiers.Alt)) keys.Add(0x12);
        if (modifiers.HasFlag(HotkeyModifiers.Shift)) keys.Add(0x10);
        if (modifiers.HasFlag(HotkeyModifiers.Win)) keys.Add(0x5B);
        var events = new List<Input>();
        foreach (var key in keys) events.Add(Key(key, up: false));
        events.Add(Key(virtualKey, up: false));
        events.Add(Key(virtualKey, up: true));
        for (var i = keys.Count - 1; i >= 0; i--) events.Add(Key(keys[i], up: true));
        Send(events);
    }

    public void TypeText(string text)
    {
        var events = new List<Input>();
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') continue;       // the pair is one Enter
            if (c is '\r' or '\n')
            {
                events.Add(Key(VkReturn, up: false));
                events.Add(Key(VkReturn, up: true));
            }
            else if (c == '\t')
            {
                events.Add(Key(VkTab, up: false));
                events.Add(Key(VkTab, up: true));
            }
            else
            {
                events.Add(Character(c, up: false));
                events.Add(Character(c, up: true));
            }

            if (events.Count >= ChunkChars * 2 && !char.IsHighSurrogate(c))
            {
                Send(events);
                events.Clear();
            }
        }

        if (events.Count > 0) Send(events);
    }

    public bool IsDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    public void KeyUp(int virtualKey) => Send([Key(virtualKey, up: true)]);

    public void MaskMenuKey() => Send([Key(VkUnassigned, up: false), Key(VkUnassigned, up: true)]);

    static Input Key(int virtualKey, bool up)
    {
        var flags = up ? KeyUpFlag : 0u;
        if (virtualKey is 0xA3 or 0xA5 or 0x5B or 0x5C) flags |= ExtendedKeyFlag;
        return new Input { Type = InputKeyboard, Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = (ushort)virtualKey, Flags = flags } } };
    }

    static Input Character(char c, bool up) => new()
    {
        Type = InputKeyboard,
        Data = new InputUnion { Keyboard = new KeyboardInput { Scan = c, Flags = UnicodeFlag | (up ? KeyUpFlag : 0u) } },
    };

    static void Send(List<Input> events)
    {
        var array = events.ToArray();
        var sent = SendInput((uint)array.Length, array, Marshal.SizeOf<Input>());
        if (sent != array.Length)
            throw new KeyboardException("Windows would not let the wallet type into that program. If it runs as administrator, start the wallet the same way, or use another program.");
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;                 // not used; it gives the union the size Windows expects
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MouseInput
    {
        public int X, Y;
        public uint MouseData, Flags, Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct KeyboardInput
    {
        public ushort VirtualKey, Scan;
        public uint Flags, Time;
        public UIntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint count, Input[] inputs, int size);

    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int virtualKey);
}
