using Byoa.Wallet.Everywhere;

namespace Byoa.Wallet.Tests;

/// <summary>Reading, writing and judging hotkeys: what is a hotkey, what may not be one, and when two clash.</summary>
public class EverywhereHotkeyTests
{
    static Hotkey Parse(string text)
    {
        Assert.True(Hotkey.TryParse(text, out var hotkey, out var error), error);
        return hotkey;
    }

    // ------------------------------------------------------------------ parsing

    [Fact]
    public void TheDefaultsAreReadAndWrittenBack()
    {
        var dictation = Parse("Ctrl+Alt+Space");
        Assert.Equal(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, dictation.Modifiers);
        Assert.Equal(0x20, dictation.VirtualKey);
        Assert.Equal("Ctrl+Alt+Space", dictation.ToString());

        var check = Parse("Ctrl+Alt+K");
        Assert.Equal(0x4B, check.VirtualKey);
        Assert.Equal("Ctrl+Alt+K", check.ToString());
    }

    [Theory]
    [InlineData("ctrl+alt+space", "Ctrl+Alt+Space")]
    [InlineData("  Control + Alt + K ", "Ctrl+Alt+K")]
    [InlineData("alt+ctrl+k", "Ctrl+Alt+K")]
    [InlineData("Shift+Ctrl+F5", "Ctrl+Shift+F5")]
    [InlineData("Ctrl+Esc", "Ctrl+Escape")]
    [InlineData("ctrl+alt+pgup", "Ctrl+Alt+PageUp")]
    [InlineData("Ctrl+Alt+8", "Ctrl+Alt+8")]
    [InlineData("Win+Shift+Z", "Shift+Win+Z")]
    [InlineData("F13", "F13")]
    [InlineData("Ctrl+Alt+Return", "Ctrl+Alt+Enter")]
    public void SpellingAndOrderDoNotMatterAndTheCanonicalFormComesBack(string written, string canonical)
    {
        var hotkey = Parse(written);
        Assert.Equal(canonical, hotkey.ToString());
        Assert.Equal(hotkey, Parse(canonical));            // what is shown can be read again
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("Ctrl+Alt")]
    [InlineData("Ctrl+Alt+")]
    [InlineData("Ctrl++K")]
    [InlineData("Ctrl+Ctrl+K")]
    [InlineData("Ctrl+K+L")]
    [InlineData("Ctrl+Banana")]
    [InlineData("Ctrl+F25")]
    [InlineData("Ctrl+F0")]
    public void ATextThatIsNotAHotkeyIsRefusedWithAReason(string? text)
    {
        Assert.False(Hotkey.TryParse(text, out var hotkey, out var error));
        Assert.Equal(default, hotkey);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void EveryFunctionKeyAndLetterAndDigitHasItsWindowsCode()
    {
        for (var f = 1; f <= 24; f++) Assert.Equal(0x70 + f - 1, Parse("F" + f).VirtualKey);
        for (var c = 'A'; c <= 'Z'; c++) Assert.Equal(c, Parse("Ctrl+" + c).VirtualKey);
        for (var d = '0'; d <= '9'; d++) Assert.Equal(d, Parse("Ctrl+" + d).VirtualKey);
    }

    [Fact]
    public void ModifierKeysAreToldApartFromOrdinaryKeysWhetherLeftRightOrGeneric()
    {
        Assert.Equal(HotkeyModifiers.Ctrl, HotkeyKeys.ModifierOfCode(0xA2));
        Assert.Equal(HotkeyModifiers.Ctrl, HotkeyKeys.ModifierOfCode(0xA3));
        Assert.Equal(HotkeyModifiers.Alt, HotkeyKeys.ModifierOfCode(0xA5));
        Assert.Equal(HotkeyModifiers.Shift, HotkeyKeys.ModifierOfCode(0x10));
        Assert.Equal(HotkeyModifiers.Win, HotkeyKeys.ModifierOfCode(0x5B));
        Assert.Null(HotkeyKeys.ModifierOfCode(0x20));
    }

    // ------------------------------------------------------------------ what may not be a hotkey

    [Theory]
    [InlineData("Ctrl+Alt+Space")]
    [InlineData("Ctrl+Alt+K")]
    [InlineData("Ctrl+Shift+Space")]
    [InlineData("Alt+F9")]
    [InlineData("Ctrl+Alt+Shift+J")]
    [InlineData("F13")]
    [InlineData("F24")]
    public void SensibleHotkeysPass(string text)
    {
        Assert.Null(HotkeyRules.Problem(Parse(text), EverywhereFeature.Dictation, null));
    }

    [Theory]
    [InlineData("K", "Ctrl or Alt")]                       // a bare letter would stop the visitor typing it
    [InlineData("Shift+K", "Ctrl or Alt")]
    [InlineData("Space", "Ctrl or Alt")]
    [InlineData("F5", "F13 to F24")]
    [InlineData("Shift+F5", "F13 to F24")]
    [InlineData("Escape", "Esc")]
    [InlineData("Ctrl+Escape", "Esc")]
    [InlineData("Win+K", "Win key")]
    [InlineData("Ctrl+Alt+Win+K", "Win key")]
    public void AHotkeyThatWouldBreakTypingOrSomethingElseIsRefusedInPlainWords(string text, string mentions)
    {
        var problem = HotkeyRules.Problem(Parse(text), EverywhereFeature.Dictation, null);
        Assert.NotNull(problem);
        Assert.Contains(mentions, problem);
    }

    [Theory]
    [InlineData("Ctrl+C")]
    [InlineData("Ctrl+V")]
    [InlineData("Ctrl+Z")]
    [InlineData("Alt+F4")]
    [InlineData("Alt+Tab")]
    [InlineData("Ctrl+Shift+Escape")]
    [InlineData("Ctrl+Alt+Delete")]
    public void ShortcutsThatWindowsAndEveryProgramUseAreRefused(string text)
    {
        // (Ctrl+Escape and Ctrl+Shift+Escape are refused for the Esc key first; the rest for being in use)
        var problem = HotkeyRules.Problem(Parse(text), EverywhereFeature.Check, null);
        Assert.NotNull(problem);
    }

    [Fact]
    public void ReservedShortcutsSayThatTheyAreInUse()
    {
        var problem = HotkeyRules.Problem(Parse("Ctrl+V"), EverywhereFeature.Dictation, null);
        Assert.Contains("Windows and most programs", problem);
    }

    // ------------------------------------------------------------------ clashes

    [Fact]
    public void TheTwoFeaturesCannotShareAHotkey()
    {
        var same = Parse("Ctrl+Alt+K");
        var problem = HotkeyRules.Problem(same, EverywhereFeature.Dictation, same);
        Assert.NotNull(problem);
        Assert.Contains("check of selected text", problem);

        var other = HotkeyRules.Problem(same, EverywhereFeature.Check, same);
        Assert.Contains("dictation", other);

        Assert.Null(HotkeyRules.Problem(same, EverywhereFeature.Dictation, Parse("Ctrl+Alt+Space")));
    }

    [Fact]
    public void AHotkeyAnotherProgramHoldsIsReportedPlainlyNotSilently()
    {
        var probe = new FakeHotkeyProbe();
        probe.Taken.Add("Ctrl+Alt+Space");

        var (hotkey, message) = HotkeyRules.Validate("Ctrl+Alt+Space", EverywhereFeature.Dictation, "Ctrl+Alt+K", probe);
        Assert.Null(hotkey);
        Assert.NotNull(message);
        Assert.Contains("Ctrl+Alt+Space", message);
        Assert.Contains("already used by another program", message);
        Assert.Contains("Handy", message);                   // the likeliest holder of a dictation hotkey
        Assert.Contains("Choose another", message);
        Assert.Contains("already registered", message);      // what Windows said is kept

        // the check's hotkey is not blamed on a dictation program
        probe.Taken.Add("Ctrl+Alt+K");
        var (none, checkMessage) = HotkeyRules.Validate("Ctrl+Alt+K", EverywhereFeature.Check, "Ctrl+Alt+Space", probe);
        Assert.Null(none);
        Assert.DoesNotContain("Handy", checkMessage);
    }

    [Fact]
    public void AFreeHotkeyIsAcceptedAndProbedOnce()
    {
        var probe = new FakeHotkeyProbe();
        var (hotkey, message) = HotkeyRules.Validate("ctrl+alt+j", EverywhereFeature.Dictation, "Ctrl+Alt+K", probe);
        Assert.Null(message);
        Assert.Equal("Ctrl+Alt+J", hotkey!.Value.ToString());
        Assert.Equal(["Ctrl+Alt+J"], probe.Probed);
    }

    [Fact]
    public void TheProbeIsNotAskedAboutAHotkeyThatFailsEarlier()
    {
        var probe = new FakeHotkeyProbe();
        Assert.NotNull(HotkeyRules.Validate("K", EverywhereFeature.Dictation, null, probe).Message);
        Assert.NotNull(HotkeyRules.Validate("Ctrl+Banana", EverywhereFeature.Dictation, null, probe).Message);
        Assert.NotNull(HotkeyRules.Validate("Ctrl+V", EverywhereFeature.Dictation, null, probe).Message);
        Assert.Empty(probe.Probed);
    }

    [Fact]
    public void WithoutAProbeOnlyTheRulesApply()
    {
        Assert.NotNull(HotkeyRules.Validate("Ctrl+Alt+K", EverywhereFeature.Dictation, "ctrl+alt+k", null).Message);
        Assert.Null(HotkeyRules.Validate("Ctrl+Alt+K", EverywhereFeature.Dictation, "garbage", null).Message);   // an unreadable other hotkey clashes with nothing
    }
}
