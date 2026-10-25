using System.Globalization;

namespace Byoa.Wallet.Everywhere;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Ctrl = 1,
    Alt = 2,
    Shift = 4,
    Win = 8,
}

/// <summary>
/// A global hotkey: some modifiers and one key, written the way people write them ("Ctrl+Alt+Space"). The key is a Windows virtual-key code,
/// so that the keyboard hook can compare it with what it sees without knowing about layouts.
/// </summary>
public readonly record struct Hotkey(HotkeyModifiers Modifiers, int VirtualKey)
{
    /// <summary>The text people read and the settings keep: modifiers in the order Ctrl, Alt, Shift, Win, then the key.</summary>
    public override string ToString()
    {
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(HotkeyModifiers.Ctrl)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        parts.Add(HotkeyKeys.NameOf(VirtualKey));
        return string.Join('+', parts);
    }

    /// <summary>
    /// Reads "Ctrl+Alt+Space" (case and spaces do not matter; "Control", "Esc", "Windows" and a few other spellings are understood).
    /// On failure <paramref name="error"/> says what is wrong in plain words.
    /// </summary>
    public static bool TryParse(string? text, out Hotkey hotkey, out string error)
    {
        hotkey = default;
        error = "";
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "The hotkey is empty. Write it like Ctrl+Alt+Space.";
            return false;
        }

        var modifiers = HotkeyModifiers.None;
        int? key = null;
        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries))
        {
            if (raw.Length == 0)
            {
                // "Ctrl++" would mean the plus key, but the plus key is written "Plus"; an empty part is a typing mistake.
                error = "\"" + text.Trim() + "\" has an empty part between plus signs. Write it like Ctrl+Alt+Space.";
                return false;
            }

            if (HotkeyKeys.ModifierOf(raw) is { } modifier)
            {
                if (modifiers.HasFlag(modifier))
                {
                    error = "\"" + raw + "\" is there twice.";
                    return false;
                }

                modifiers |= modifier;
                continue;
            }

            if (key is not null)
            {
                error = "A hotkey has one key besides Ctrl, Alt, Shift and Win; \"" + text.Trim() + "\" has two.";
                return false;
            }

            if (HotkeyKeys.CodeOf(raw) is not { } code)
            {
                error = "\"" + raw + "\" is not a key the wallet knows. Use a letter, a digit, F1 to F24, Space, or a named key such as Home or PageUp.";
                return false;
            }

            key = code;
        }

        if (key is null)
        {
            error = "The hotkey has only modifier keys. Add a letter, a digit or another key.";
            return false;
        }

        hotkey = new Hotkey(modifiers, key.Value);
        return true;
    }
}

/// <summary>The keys a hotkey may use, with their Windows virtual-key codes and their names.</summary>
public static class HotkeyKeys
{
    public const int Escape = 0x1B;
    public const int Space = 0x20;

    // Left and right keys of each modifier, as the low-level hook reports them, and the generic ones.
    static readonly int[] CtrlCodes = [0x11, 0xA2, 0xA3];
    static readonly int[] AltCodes = [0x12, 0xA4, 0xA5];
    static readonly int[] ShiftCodes = [0x10, 0xA0, 0xA1];
    static readonly int[] WinCodes = [0x5B, 0x5C];

    static readonly (string Name, int Code)[] Named =
    [
        ("Space", 0x20), ("Enter", 0x0D), ("Tab", 0x09), ("Backspace", 0x08), ("Escape", 0x1B), ("Insert", 0x2D), ("Delete", 0x2E),
        ("Home", 0x24), ("End", 0x23), ("PageUp", 0x21), ("PageDown", 0x22),
        ("Up", 0x26), ("Down", 0x28), ("Left", 0x25), ("Right", 0x27),
        ("Grave", 0xC0), ("Minus", 0xBD), ("Equals", 0xBB), ("Plus", 0xBB), ("Comma", 0xBC), ("Period", 0xBE),
        ("Slash", 0xBF), ("Semicolon", 0xBA), ("Quote", 0xDE), ("OpenBracket", 0xDB), ("CloseBracket", 0xDD), ("Backslash", 0xDC),
        ("Pause", 0x13), ("PrintScreen", 0x2C), ("ScrollLock", 0x91),
    ];

    static readonly Dictionary<string, int> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Return"] = 0x0D, ["Esc"] = 0x1B, ["Escape"] = 0x1B, ["Del"] = 0x2E, ["Ins"] = 0x2D, ["PgUp"] = 0x21, ["PgDn"] = 0x22, ["PgDown"] = 0x22,
        ["Spacebar"] = 0x20, ["BackQuote"] = 0xC0, ["Tilde"] = 0xC0, ["Dash"] = 0xBD, ["Apostrophe"] = 0xDE,
        ["ArrowUp"] = 0x26, ["ArrowDown"] = 0x28, ["ArrowLeft"] = 0x25, ["ArrowRight"] = 0x27,
    };

    public static HotkeyModifiers? ModifierOf(string name) => name.ToLowerInvariant() switch
    {
        "ctrl" or "control" or "ctl" => HotkeyModifiers.Ctrl,
        "alt" or "option" => HotkeyModifiers.Alt,
        "shift" => HotkeyModifiers.Shift,
        "win" or "windows" or "meta" or "super" or "cmd" => HotkeyModifiers.Win,
        _ => null,
    };

    /// <summary>The modifier a key code is (any of its left, right or generic codes), or null for an ordinary key.</summary>
    public static HotkeyModifiers? ModifierOfCode(int code)
    {
        if (CtrlCodes.Contains(code)) return HotkeyModifiers.Ctrl;
        if (AltCodes.Contains(code)) return HotkeyModifiers.Alt;
        if (ShiftCodes.Contains(code)) return HotkeyModifiers.Shift;
        if (WinCodes.Contains(code)) return HotkeyModifiers.Win;
        return null;
    }

    /// <summary>The key codes of a modifier, for a program that has to let one go (a held Ctrl before it types).</summary>
    public static IReadOnlyList<int> CodesOf(HotkeyModifiers modifier) => modifier switch
    {
        HotkeyModifiers.Ctrl => CtrlCodes,
        HotkeyModifiers.Alt => AltCodes,
        HotkeyModifiers.Shift => ShiftCodes,
        HotkeyModifiers.Win => WinCodes,
        _ => [],
    };

    public static int? CodeOf(string name)
    {
        if (name.Length == 1)
        {
            var c = char.ToUpperInvariant(name[0]);
            if (c is >= 'A' and <= 'Z') return c;
            if (c is >= '0' and <= '9') return c;
        }

        if (name.Length is 2 or 3 && (name[0] is 'F' or 'f') && int.TryParse(name.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var f) && f is >= 1 and <= 24)
            return 0x70 + f - 1;
        foreach (var (known, code) in Named)
        {
            if (string.Equals(known, name, StringComparison.OrdinalIgnoreCase)) return code;
        }

        return Aliases.TryGetValue(name, out var alias) ? alias : null;
    }

    public static string NameOf(int code)
    {
        if (code is >= 'A' and <= 'Z' or >= '0' and <= '9') return ((char)code).ToString();
        if (code is >= 0x70 and <= 0x87) return "F" + (code - 0x70 + 1).ToString(CultureInfo.InvariantCulture);
        // The first name listed for a code is the one shown ("Equals", not "Plus").
        foreach (var (name, known) in Named)
        {
            if (known == code) return name;
        }

        return "Key" + code.ToString("X2", CultureInfo.InvariantCulture);
    }

    public static bool IsFunctionKey(int code) => code is >= 0x70 and <= 0x87;
}
