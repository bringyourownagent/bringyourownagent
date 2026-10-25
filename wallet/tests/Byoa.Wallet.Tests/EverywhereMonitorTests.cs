using Byoa.Wallet.Everywhere;

namespace Byoa.Wallet.Tests;

/// <summary>The keys of the hook turned into "the hotkey went down / was let go": exact chords, swallowing, injected keys, repeats.</summary>
public class EverywhereMonitorTests
{
    const int LCtrl = FakeKeyboardHook.LCtrl, LAlt = FakeKeyboardHook.LAlt, LShift = FakeKeyboardHook.LShift, Space = FakeKeyboardHook.Space, K = FakeKeyboardHook.K;

    readonly FakeKeyboardHook _hook = new();
    readonly HotkeyMonitor _monitor;
    readonly List<string> _events = [];

    public EverywhereMonitorTests()
    {
        _monitor = new HotkeyMonitor(_hook);
        _monitor.Pressed += f => _events.Add("press:" + f);
        _monitor.Released += f => _events.Add("release:" + f);
        _monitor.EscapePressed += () => _events.Add("esc");
        _monitor.SetBindings(new Dictionary<EverywhereFeature, Hotkey>
        {
            [EverywhereFeature.Dictation] = Key("Ctrl+Alt+Space"),
            [EverywhereFeature.Check] = Key("Ctrl+Alt+K"),
        });
    }

    static Hotkey Key(string text)
    {
        Assert.True(Hotkey.TryParse(text, out var hotkey, out _));
        return hotkey;
    }

    void Hold(params int[] keys)
    {
        foreach (var key in keys) _hook.Send(key, down: true);
    }

    [Fact]
    public void TheChordGoesDownAndIsKeptFromTheProgramThatHasTheFocus()
    {
        Assert.False(_hook.Send(LCtrl, true));              // modifiers always pass: other programs need to see them
        Assert.False(_hook.Send(LAlt, true));
        Assert.True(_hook.Send(Space, true));               // the chord's key does not
        Assert.Equal(["press:Dictation"], _events);

        Assert.True(_hook.Send(Space, false));              // its "up" is kept too, or the program would see an "up" without a "down"
        Assert.False(_hook.Send(LAlt, false));
        Assert.False(_hook.Send(LCtrl, false));
        Assert.Equal(["press:Dictation", "release:Dictation"], _events);
    }

    [Fact]
    public void TheHeldKeyRepeatingIsKeptAndDoesNotPressAgain()
    {
        Hold(LCtrl, LAlt);
        Assert.True(_hook.Send(Space, true));
        Assert.True(_hook.Send(Space, true));
        Assert.True(_hook.Send(Space, true));
        Assert.Equal(["press:Dictation"], _events);
    }

    [Fact]
    public void LettingGoOfAModifierFirstEndsTheHoldAndTheKeysUpIsStillKept()
    {
        Hold(LCtrl, LAlt);
        _hook.Send(Space, true);
        Assert.False(_hook.Send(LAlt, false));
        Assert.Equal(["press:Dictation", "release:Dictation"], _events);

        Assert.True(_hook.Send(Space, false));
        Assert.Equal(2, _events.Count);                     // no second release
    }

    [Fact]
    public void ALeftAndARightCtrlCountAsOneModifier()
    {
        Hold(LCtrl, 0xA3, LAlt);
        _hook.Send(Space, true);
        Assert.Equal(["press:Dictation"], _events);
        _hook.Send(LCtrl, false);                           // the right one is still down: the chord holds
        Assert.Equal(["press:Dictation"], _events);
        _hook.Send(0xA3, false);
        Assert.Equal(["press:Dictation", "release:Dictation"], _events);
    }

    [Fact]
    public void TheCheckHotkeyIsToldApartFromTheDictationHotkey()
    {
        Hold(LCtrl, LAlt);
        Assert.True(_hook.Send(K, true));
        _hook.Send(K, false);
        Assert.Equal(["press:Check", "release:Check"], _events);
    }

    [Theory]
    [InlineData(false, false)]                              // the key alone
    [InlineData(true, false)]                               // Ctrl only
    public void TheChordNeedsItsModifiersAndOnlyThose(bool ctrl, bool alt)
    {
        if (ctrl) Hold(LCtrl);
        if (alt) Hold(LAlt);
        Assert.False(_hook.Send(Space, true));              // an ordinary space: untouched
        Assert.False(_hook.Send(Space, false));
        Assert.Empty(_events);
    }

    [Fact]
    public void AnExtraModifierIsADifferentChord()
    {
        Hold(LCtrl, LAlt, LShift);
        Assert.False(_hook.Send(Space, true));              // Ctrl+Alt+Shift+Space belongs to some other program
        Assert.Empty(_events);
    }

    [Fact]
    public void KeysTheWalletSentItselfAreNeverTakenForTheVisitors()
    {
        // the wallet's Ctrl+V while a chord's modifiers are somehow down, or a key that happens to equal the hotkey
        _hook.Send(LCtrl, true, injected: true);
        _hook.Send(LAlt, true, injected: true);
        Assert.False(_hook.Send(Space, true, injected: true));
        Assert.Empty(_events);

        // and an injected key does not disturb a real chord in progress
        Hold(LCtrl, LAlt);
        _hook.Send(Space, true);
        _hook.Send(0x56, true, injected: true);
        _hook.Send(0x56, false, injected: true);
        Assert.Equal(["press:Dictation"], _events);
    }

    [Fact]
    public void OtherKeysDuringAHoldPassThroughAndDoNotLoseTheHold()
    {
        Hold(LCtrl, LAlt);
        _hook.Send(Space, true);
        Assert.False(_hook.Send(0x41, true));
        Assert.False(_hook.Send(0x41, false));
        Assert.Equal(["press:Dictation"], _events);

        _hook.Send(Space, false);                           // the release still comes: a recording cannot stay on forever
        Assert.Equal(["press:Dictation", "release:Dictation"], _events);
    }

    [Fact]
    public void EscWhileTheChordIsStillHeldCancelsEvenThoughCtrlAndAltAreDown()
    {
        _monitor.WantsEscape = () => true;
        Hold(LCtrl, LAlt);
        _hook.Send(Space, true);
        Assert.True(_hook.Send(0x1B, true));                // hold to talk: the keys are held while the visitor decides to cancel
        Assert.True(_hook.Send(0x1B, false));
        Assert.Equal(["press:Dictation", "esc"], _events);

        _hook.Send(Space, false);                           // and letting go afterwards is still a release (the machine ignores it)
        Assert.Equal(["press:Dictation", "esc", "release:Dictation"], _events);
    }

    [Fact]
    public void AChordWhoseUpNeverCameDoesNotLockTheKeyOut()
    {
        Hold(LCtrl, LAlt);
        _hook.Send(Space, true);                            // the "up" is lost (the screen was locked, say)
        _hook.Send(LCtrl, false);
        _hook.Send(LAlt, false);
        Assert.Equal(["press:Dictation", "release:Dictation"], _events);

        // the next real press is a press, and the stale swallow of the old "up" does not eat anything else
        Hold(LCtrl, LAlt);
        Assert.True(_hook.Send(Space, true));
        Assert.Equal(["press:Dictation", "release:Dictation", "press:Dictation"], _events);
    }

    [Fact]
    public void EscIsTakenOnlyWhileTheWalletWantsIt()
    {
        var wanted = false;
        _monitor.WantsEscape = () => wanted;
        Assert.False(_hook.Send(0x1B, true));
        Assert.False(_hook.Send(0x1B, false));
        Assert.Empty(_events);

        wanted = true;
        Assert.True(_hook.Send(0x1B, true));
        Assert.True(_hook.Send(0x1B, false));
        Assert.Equal(["esc"], _events);
    }

    [Fact]
    public void EscWithAModifierBelongsToTheProgram()
    {
        _monitor.WantsEscape = () => true;
        Hold(LCtrl);
        Assert.False(_hook.Send(0x1B, true));
        Assert.Empty(_events);
    }

    // ------------------------------------------------------------------ the hook runs only when there is something to listen for

    [Fact]
    public void TheHookIsInstalledOnlyWhileAHotkeyIsSet()
    {
        var hook = new FakeKeyboardHook();
        var monitor = new HotkeyMonitor(hook);
        Assert.False(monitor.Listening);
        Assert.Equal(0, hook.Starts);                       // nothing is listened to until the visitor turns a feature on

        monitor.SetBindings(new Dictionary<EverywhereFeature, Hotkey> { [EverywhereFeature.Check] = Key("Ctrl+Alt+K") });
        Assert.True(monitor.Listening);
        Assert.Equal(1, hook.Starts);

        monitor.SetBindings(new Dictionary<EverywhereFeature, Hotkey>
        {
            [EverywhereFeature.Check] = Key("Ctrl+Alt+K"),
            [EverywhereFeature.Dictation] = Key("Ctrl+Alt+Space"),
        });
        Assert.Equal(1, hook.Starts);                       // one hook for both

        monitor.SetBindings(new Dictionary<EverywhereFeature, Hotkey>());
        Assert.False(monitor.Listening);
        Assert.Equal(1, hook.Stops);
        Assert.False(hook.Running);
    }

    [Fact]
    public void ChangingTheHotkeyAtOnceUsesTheNewOne()
    {
        _monitor.SetBindings(new Dictionary<EverywhereFeature, Hotkey> { [EverywhereFeature.Dictation] = Key("Ctrl+Alt+J") });
        Hold(LCtrl, LAlt);
        Assert.False(_hook.Send(Space, true));              // the old chord is a plain space now
        _hook.Send(Space, false);
        Assert.True(_hook.Send(0x4A, true));
        Assert.Equal(["press:Dictation"], _events);
    }

    [Fact]
    public void DisposingStopsTheHook()
    {
        _monitor.Dispose();
        Assert.False(_hook.Running);
    }
}
