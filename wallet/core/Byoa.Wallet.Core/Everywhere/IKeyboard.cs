namespace Byoa.Wallet.Everywhere;

/// <summary>Keystrokes sent to the program that has the focus (<c>SendInput</c>), and what is held down now.</summary>
public interface IKeyboard
{
    /// <summary>Presses the modifiers and the key together, then lets them go: Ctrl+V.</summary>
    void Chord(HotkeyModifiers modifiers, int virtualKey);

    /// <summary>Types the text as characters (Unicode key events), whatever the keyboard layout is.</summary>
    void TypeText(string text);

    /// <summary>True while the visitor holds the key down.</summary>
    bool IsDown(int virtualKey);

    /// <summary>Sends "key up" for a key the visitor still holds, so that the next keystroke does not carry it.</summary>
    void KeyUp(int virtualKey);

    /// <summary>A press of a key that does nothing (an unassigned code), so that Alt held and let go is not taken for a tap that opens a menu.</summary>
    void MaskMenuKey();
}

public static class KeyboardExtensions
{
    static readonly HotkeyModifiers[] Held = [HotkeyModifiers.Ctrl, HotkeyModifiers.Alt, HotkeyModifiers.Shift, HotkeyModifiers.Win];

    /// <summary>
    /// Waits until the visitor lets go of Ctrl, Alt, Shift and Win (the hotkey he just pressed holds them), so that the wallet's Ctrl+V or Ctrl+C
    /// does not arrive as Ctrl+Alt+V. A key still held when the time is up is released for the program that receives the keystroke.
    /// </summary>
    public static async Task WaitForModifiersReleasedAsync(this IKeyboard keyboard, TimeSpan wait, TimeSpan step, CancellationToken ct)
    {
        var waited = TimeSpan.Zero;
        while (Pressed(keyboard).Count > 0 && waited < wait)
        {
            await Task.Delay(step, ct).ConfigureAwait(false);
            waited += step;
        }

        foreach (var code in Pressed(keyboard)) keyboard.KeyUp(code);
    }

    // The left and right keys: the generic codes (0x10, 0x11, 0x12) are what a program asks about, not what a hook sees.
    static List<int> Pressed(IKeyboard keyboard) =>
        Held.SelectMany(HotkeyKeys.CodesOf).Where(code => code is not (0x10 or 0x11 or 0x12) && keyboard.IsDown(code)).ToList();
}
