using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Byoa.Wallet.Everywhere;

namespace Byoa.Wallet.Desktop;

/// <summary>
/// Asks Windows whether a hotkey is already registered by another program: the wallet registers it for an instant (<c>RegisterHotKey</c>) and lets it
/// go again. This is what finds a dictation program such as Handy that took the same combination. It finds only programs that register their hotkey
/// the usual way; the wallet itself listens with a keyboard hook, which Windows never tells about a clash.
/// </summary>
[SupportedOSPlatform("windows")]
sealed class WinHotkeyProbe : IHotkeyProbe
{
    const int ErrorHotkeyAlreadyRegistered = 1409;
    const uint ModAlt = 1, ModControl = 2, ModShift = 4, ModWin = 8, ModNoRepeat = 0x4000;
    const int ProbeId = 0x4259;

    public HotkeyProbeResult Probe(Hotkey hotkey)
    {
        var modifiers = ModNoRepeat;
        if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Ctrl)) modifiers |= ModControl;
        if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Alt)) modifiers |= ModAlt;
        if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Shift)) modifiers |= ModShift;
        if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Win)) modifiers |= ModWin;
        try
        {
            // Registration belongs to the thread that makes it, so it is undone on the same one, in the same call.
            if (RegisterHotKey(IntPtr.Zero, ProbeId, modifiers, (uint)hotkey.VirtualKey))
            {
                UnregisterHotKey(IntPtr.Zero, ProbeId);
                return new HotkeyProbeResult(true);
            }

            var error = Marshal.GetLastWin32Error();
            return error == ErrorHotkeyAlreadyRegistered
                ? new HotkeyProbeResult(false, "another program has registered it")
                : new HotkeyProbeResult(true);        // Windows gave no answer about a clash: not a reason to refuse
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return new HotkeyProbeResult(true);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool UnregisterHotKey(IntPtr window, int id);
}
