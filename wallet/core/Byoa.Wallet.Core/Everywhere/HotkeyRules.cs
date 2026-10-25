namespace Byoa.Wallet.Everywhere;

/// <summary>Which of the wallet's two hotkeys a rule is about.</summary>
public enum EverywhereFeature
{
    Dictation,
    Check,
}

/// <summary>
/// What may not be a hotkey of the wallet, and when two hotkeys clash. All of it is plain logic: the system's own answer ("another program holds
/// this one") comes from <see cref="IHotkeyProbe"/>. Every message is addressed to the visitor.
/// </summary>
public static class HotkeyRules
{
    /// <summary>Shortcuts that other programs and Windows use all the time; a hotkey of the wallet on one of them would take it away from everything else.</summary>
    static readonly string[] Reserved =
    [
        "Ctrl+C", "Ctrl+V", "Ctrl+X", "Ctrl+Z", "Ctrl+Y", "Ctrl+A", "Ctrl+S", "Ctrl+F", "Ctrl+P", "Ctrl+N", "Ctrl+O", "Ctrl+W", "Ctrl+T", "Ctrl+R",
        "Ctrl+Shift+Z", "Ctrl+Shift+T", "Ctrl+Shift+N", "Ctrl+Insert", "Shift+Insert", "Shift+Delete",
        "Alt+F4", "Alt+Tab", "Alt+Esc", "Alt+Space", "Ctrl+Esc", "Ctrl+Shift+Esc", "Ctrl+Alt+Delete", "Alt+Enter",
    ];

    /// <summary>
    /// The first thing wrong with <paramref name="hotkey"/> as the key of <paramref name="feature"/>, or null when it is fine. <paramref name="other"/>
    /// is the hotkey of the wallet's other feature (null when it has none).
    /// </summary>
    public static string? Problem(Hotkey hotkey, EverywhereFeature feature, Hotkey? other)
    {
        var key = hotkey.VirtualKey;
        var modifiers = hotkey.Modifiers;
        if (key == HotkeyKeys.Escape)
            return "Esc cancels what Everywhere is doing, so it cannot also start it. Choose another key.";
        if (modifiers.HasFlag(HotkeyModifiers.Win))
            return "Windows keeps the Win key for its own shortcuts. Use Ctrl, Alt or Shift instead.";
        // A key alone, or with Shift alone, is typing: the wallet would swallow letters from everything the visitor writes. Only F13 to F24
        // alone are free (hardly any keyboard has them, and no program uses them).
        if ((modifiers & (HotkeyModifiers.Ctrl | HotkeyModifiers.Alt)) == HotkeyModifiers.None)
        {
            var freeKey = modifiers == HotkeyModifiers.None && key is >= 0x7C and <= 0x87;
            if (!freeKey)
                return HotkeyKeys.IsFunctionKey(key)
                    ? "F1 to F12 are used by many programs. Add Ctrl or Alt, or use F13 to F24."
                    : "A hotkey needs Ctrl or Alt with the key (for example Ctrl+Alt+Space); without one it would stop you typing that key.";
        }

        if (Reserved.Any(r => Hotkey.TryParse(r, out var reserved, out _) && reserved == hotkey))
            return hotkey + " is a shortcut that Windows and most programs use (copy, paste, close and the like). Choose another combination.";
        if (other is { } taken && taken == hotkey)
            return hotkey + " is already the hotkey of " + (feature == EverywhereFeature.Dictation ? "the check of selected text" : "dictation") + ". Each needs its own.";
        return null;
    }

    /// <summary>
    /// The plain message for a hotkey that another program holds, or for one the wallet could not ask about. A dictation program is the likeliest
    /// holder of a dictation hotkey, so the message says so.
    /// </summary>
    public static string TakenMessage(Hotkey hotkey, EverywhereFeature feature, HotkeyProbeResult probe)
    {
        var who = feature == EverywhereFeature.Dictation ? "another program (a dictation program such as Handy, for example)" : "another program";
        return hotkey + " is already used by " + who + ", so the wallet would fight with it. Choose another combination for "
            + (feature == EverywhereFeature.Dictation ? "dictation" : "the check") + ", or change the hotkey in that program."
            + (string.IsNullOrWhiteSpace(probe.Detail) ? "" : " (Windows said: " + probe.Detail + ")");
    }

    /// <summary>
    /// The visitor's text for a hotkey, checked all the way: it is understood, it is allowed, and the system does not say another program holds it.
    /// Returns the hotkey and null, or null and what to tell him.
    /// </summary>
    public static (Hotkey? Hotkey, string? Message) Validate(string? text, EverywhereFeature feature, string? otherText, IHotkeyProbe? probe)
    {
        if (!Hotkey.TryParse(text, out var hotkey, out var error)) return (null, error);
        Hotkey? other = Hotkey.TryParse(otherText, out var parsedOther, out _) ? parsedOther : null;
        if (Problem(hotkey, feature, other) is { } problem) return (null, problem);
        if (probe?.Probe(hotkey) is { Free: false } taken) return (null, TakenMessage(hotkey, feature, taken));
        return (hotkey, null);
    }
}
