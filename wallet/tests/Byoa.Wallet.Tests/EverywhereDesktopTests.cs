using System.Runtime.InteropServices;
using Avalonia.Input;
using Byoa.Wallet.Desktop;
using Byoa.Wallet.Everywhere;

namespace Byoa.Wallet.Tests;

/// <summary>
/// The parts of the desktop side that can be checked without a screen, a keyboard or a microphone: the page's key names, the sizes of the structures
/// handed to Windows, and the system's own answer about a hotkey. The hook, the keystrokes, the clipboard and the microphone are not touched here (they
/// would type into, copy from and listen to the person running the tests); they are on the list for the hand check.
/// </summary>
public class EverywhereDesktopTests
{
    [Fact]
    public void LettersDigitsAndFunctionKeysHaveTheirWalletNames()
    {
        Assert.Equal("A", SettingsWindow.KeyName(Key.A));
        Assert.Equal("K", SettingsWindow.KeyName(Key.K));
        Assert.Equal("Z", SettingsWindow.KeyName(Key.Z));
        Assert.Equal("0", SettingsWindow.KeyName(Key.D0));
        Assert.Equal("9", SettingsWindow.KeyName(Key.D9));
        Assert.Equal("F1", SettingsWindow.KeyName(Key.F1));
        Assert.Equal("F12", SettingsWindow.KeyName(Key.F12));
        Assert.Equal("F24", SettingsWindow.KeyName(Key.F24));
    }

    [Fact]
    public void NamedKeysAndUnknownKeys()
    {
        Assert.Equal("Space", SettingsWindow.KeyName(Key.Space));
        Assert.Equal("PageUp", SettingsWindow.KeyName(Key.PageUp));
        Assert.Equal("PageDown", SettingsWindow.KeyName(Key.PageDown));
        Assert.Equal("Grave", SettingsWindow.KeyName(Key.OemTilde));
        Assert.Null(SettingsWindow.KeyName(Key.CapsLock));
        Assert.Null(SettingsWindow.KeyName(Key.None));
    }

    [Fact]
    public void EveryNameThePageProducesIsAHotkeyKeyTheWalletUnderstands()
    {
        foreach (var key in Enum.GetValues<Key>())
        {
            if (SettingsWindow.KeyName(key) is not { } name) continue;
            Assert.True(Hotkey.TryParse("Ctrl+Alt+" + name, out var hotkey, out var error), key + " -> " + name + ": " + error);
            Assert.Equal(name, HotkeyKeys.NameOf(hotkey.VirtualKey));           // and the name shown is the name pressed
        }
    }

    [Fact]
    public void TheStructuresHandedToWindowsHaveTheSizesWindowsExpects()
    {
        Assert.Equal(40, WinKeyboard.InputSize);             // INPUT on 64-bit Windows
        Assert.Equal(48, WinMicrophone.HeaderSize);          // WAVEHDR on 64-bit Windows
        Assert.Equal(18, WinMicrophone.FormatSize);          // WAVEFORMATEX, packed
        Assert.Equal(8, IntPtr.Size);
    }

    [Fact]
    public void WindowsTellsUsWhenAnotherProgramHoldsAHotkey()
    {
        if (!OperatingSystem.IsWindows()) return;     // user32 and the hotkey probe are Windows only
        // An unlikely combination, held for a moment by this test and let go at once.
        var hotkey = new Hotkey(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt | HotkeyModifiers.Shift, 0x87);   // Ctrl+Alt+Shift+F24
        var probe = new WinHotkeyProbe();
        Assert.True(probe.Probe(hotkey).Free);                // nobody holds it: the probe leaves nothing behind

        Assert.True(RegisterHotKey(IntPtr.Zero, 0x4260, 2 | 1 | 4, 0x87));
        try
        {
            var taken = probe.Probe(hotkey);
            Assert.False(taken.Free);
            Assert.False(string.IsNullOrWhiteSpace(taken.Detail));
        }
        finally
        {
            UnregisterHotKey(IntPtr.Zero, 0x4260);
        }

        Assert.True(probe.Probe(hotkey).Free);                // and free again afterwards
    }

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool UnregisterHotKey(IntPtr window, int id);
}
