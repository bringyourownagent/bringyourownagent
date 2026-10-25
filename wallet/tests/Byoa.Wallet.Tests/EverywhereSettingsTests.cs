using Byoa.Wallet.Everywhere;
using Byoa.Wallet.Help;
using Byoa.Wallet.Settings;

namespace Byoa.Wallet.Tests;

/// <summary>The Everywhere settings (defaults, round trip, an old file), the service that applies them to the keyboard, and the help page.</summary>
public class EverywhereSettingsTests
{
    /// <summary>A settings file of the given text, in a folder of the tests' own; the same file name each time, so nothing piles up.</summary>
    static string FileWith(string name, string text)
    {
        var folder = Path.Combine(Path.GetTempPath(), "byoa-everywhere-tests");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name + ".json");
        File.WriteAllText(path, text);
        return path;
    }

    // ------------------------------------------------------------------ defaults

    [Fact]
    public void BothFeaturesAreOffByDefaultWithTheDefaultHotkeys()
    {
        var s = new WalletSettings().Everywhere;
        Assert.False(s.DictationOn);
        Assert.False(s.CheckOn);
        Assert.False(s.DictationExplained);
        Assert.False(s.CheckExplained);
        Assert.Equal("Ctrl+Alt+Space", s.DictationHotkey);
        Assert.Equal("Ctrl+Alt+K", s.CheckHotkey);
        Assert.Equal(EverywhereSettings.HoldToTalk, s.DictationMode);
        Assert.Null(s.DictationLanguage);                   // automatic
        Assert.False(s.AllowPaid);
        Assert.False(s.PaidWithoutAsking);
    }

    [Fact]
    public void TheDefaultHotkeysAreHotkeysTheRulesAccept()
    {
        var s = new EverywhereSettings();
        Assert.True(Hotkey.TryParse(s.DictationHotkey, out var dictation, out _));
        Assert.True(Hotkey.TryParse(s.CheckHotkey, out var check, out _));
        Assert.Null(HotkeyRules.Problem(dictation, EverywhereFeature.Dictation, check));
        Assert.Null(HotkeyRules.Problem(check, EverywhereFeature.Check, dictation));
    }

    [Fact]
    public void TwoActionsComeWithTheWallet()
    {
        var actions = new EverywhereSettings().EffectiveActions();
        Assert.Equal(["Check facts and wording", "Translate to English"], actions.Select(a => a.Name).ToArray());
        Assert.All(actions, a => Assert.False(string.IsNullOrWhiteSpace(a.Prompt)));
    }

    // ------------------------------------------------------------------ round trip

    [Fact]
    public void EverythingTheVisitorSetSurvivesTheJsonRoundTrip()
    {
        var store = new SettingsStore(null);
        store.Update(s =>
        {
            s.Everywhere.DictationOn = true;
            s.Everywhere.DictationExplained = true;
            s.Everywhere.DictationHotkey = "Ctrl+Shift+J";
            s.Everywhere.DictationMode = EverywhereSettings.PressToggle;
            s.Everywhere.DictationLanguage = "sk";
            s.Everywhere.CheckOn = true;
            s.Everywhere.CheckExplained = true;
            s.Everywhere.CheckHotkey = "Alt+F9";
            s.Everywhere.AllowPaid = true;
            s.Everywhere.PaidWithoutAsking = true;
            s.Everywhere.Actions =
            [
                new CheckAction { Name = "Shorter", Prompt = "Make it \"shorter\".\nKeep the facts." },
                new CheckAction { Name = "Politer", Prompt = "Make it politer." },
            ];
        });

        var copy = store.Snapshot().Everywhere;                // the snapshot is a deep copy made through the same JSON
        Assert.True(copy.DictationOn);
        Assert.True(copy.DictationExplained);
        Assert.Equal("Ctrl+Shift+J", copy.DictationHotkey);
        Assert.Equal(EverywhereSettings.PressToggle, copy.DictationMode);
        Assert.Equal("sk", copy.DictationLanguage);
        Assert.True(copy.CheckOn);
        Assert.True(copy.CheckExplained);
        Assert.Equal("Alt+F9", copy.CheckHotkey);
        Assert.True(copy.AllowPaid);
        Assert.True(copy.PaidWithoutAsking);
        Assert.Equal(["Shorter", "Politer"], copy.Actions.Select(a => a.Name).ToArray());
        Assert.Equal("Make it \"shorter\".\nKeep the facts.", copy.Actions[0].Prompt);
    }

    [Fact]
    public void TheSettingsFileKeepsAndReloadsTheEverywhereSection()
    {
        var path = FileWith("roundtrip", "{}");
        var store = new SettingsStore(path);
        store.Update(s =>
        {
            s.Everywhere.CheckOn = true;
            s.Everywhere.CheckHotkey = "Ctrl+Alt+J";
            s.Everywhere.Actions = [new CheckAction { Name = "Only one", Prompt = "Do the one thing." }];
        });

        var reloaded = new SettingsStore(path).Read(s => s.Everywhere);
        Assert.True(reloaded.CheckOn);
        Assert.Equal("Ctrl+Alt+J", reloaded.CheckHotkey);
        var only = Assert.Single(reloaded.Actions);              // the visitor's list replaces the wallet's two, it is not added to them
        Assert.Equal("Only one", only.Name);
        Assert.False(reloaded.DictationOn);
    }

    // ------------------------------------------------------------------ an old file

    [Fact]
    public void AnOldSettingsFileLoadsUnchangedWithEverywhereOffAndAtItsDefaults()
    {
        const string old = """
            {
              "version": 1,
              "sites": [ { "origin": "https://quiz.example.org", "status": "allowed", "skills": ["audio.transcribe"], "budget": "0.30 USD" } ],
              "limits": { "sitePerDay": "0.50 USD", "siteCallsPerDay": 77 },
              "guard": { "strictness": "strict" },
              "warmUpOnConnect": false
            }
            """;
        var store = new SettingsStore(FileWith("old", old));

        var site = store.Read(s => s.Site("https://quiz.example.org"))!;
        Assert.Equal(SiteStatus.Allowed, site.Status);
        Assert.Equal("0.30 USD", site.Budget);
        Assert.Equal("0.50 USD", store.Read(s => s.Limits.SitePerDay));
        Assert.Equal(77, store.Read(s => s.Limits.SiteCallsPerDay));
        Assert.Equal("strict", store.Read(s => s.Guard.Strictness));
        Assert.False(store.Read(s => s.WarmUpOnConnect));

        var e = store.Read(s => s.Everywhere);
        Assert.False(e.DictationOn);
        Assert.False(e.CheckOn);
        Assert.Equal("Ctrl+Alt+Space", e.DictationHotkey);
        Assert.Equal("Ctrl+Alt+K", e.CheckHotkey);
        Assert.Equal(2, e.EffectiveActions().Count);
        Assert.False(e.AllowPaid);
    }

    [Fact]
    public void ASectionWithOnlySomeFieldsKeepsTheDefaultsOfTheRest()
    {
        var store = new SettingsStore(FileWith("partial", """{ "everywhere": { "dictationOn": true, "dictationLanguage": "ru" } }"""));
        var e = store.Read(s => s.Everywhere);
        Assert.True(e.DictationOn);
        Assert.Equal("ru", e.DictationLanguage);
        Assert.False(e.CheckOn);
        Assert.Equal("Ctrl+Alt+Space", e.DictationHotkey);
        Assert.Equal(EverywhereSettings.HoldToTalk, e.DictationMode);
        Assert.Equal(2, e.Actions.Count);
    }

    [Fact]
    public void AFileWithAnUnknownFieldInTheSectionStillLoads()
    {
        var store = new SettingsStore(FileWith("future", """{ "everywhere": { "checkOn": true, "somethingFromALaterVersion": 5 } }"""));
        Assert.True(store.Read(s => s.Everywhere.CheckOn));
    }

    [Fact]
    public void AListWithNothingUsableFallsBackToTheWalletsActions()
    {
        var e = new EverywhereSettings
        {
            Actions = [new CheckAction { Name = " ", Prompt = "x" }, new CheckAction { Name = "No prompt", Prompt = "" }],
        };
        Assert.Equal(2, e.EffectiveActions().Count);
        Assert.Equal("Check facts and wording", e.EffectiveActions()[0].Name);

        e.Actions = [];
        Assert.Equal(2, e.EffectiveActions().Count);
    }

    // ------------------------------------------------------------------ the service: settings to keyboard

    sealed class Rig
    {
        public readonly SettingsStore Store = new(null);
        public readonly FakeKeyboardHook Hook = new();
        public readonly FakeHotkeyProbe Probe = new();
        public readonly FakeKeyboard Keyboard = new();
        public readonly FakeMicrophone Mic = new();
        public readonly FakeOwn Own = new();
        public readonly RecordingInserter Inserter = new();
        public readonly RecordingIndicator Indicator = new();
        public readonly FakePopup Popup = new();
        public readonly FakeSelectionReader Reader = new();
        public readonly FakeTargetWindow Windows = new();
        public readonly EverywhereService Service;
        public readonly DictationController Dictation;
        public readonly SelectionCheckController Check;
        public int StatusChanges;

        public Rig()
        {
            Dictation = new DictationController(Mic, Own, Inserter, Indicator, () => Store.Read(s => Store_Copy(s.Everywhere)));
            Check = new SelectionCheckController(Reader, Own, Inserter, new FakeClipboard(), Windows, Popup, () => Store.Read(s => Store_Copy(s.Everywhere)));
            Service = new EverywhereService(Store, new HotkeyMonitor(Hook), Dictation, Check, Probe, Keyboard);
            Service.StatusChanged += () => StatusChanges++;
            Service.Start();
        }

        // the controllers read the settings on every press; a copy keeps them from holding the store's lock-protected object
        static EverywhereSettings Store_Copy(EverywhereSettings s) => new()
        {
            DictationOn = s.DictationOn, DictationMode = s.DictationMode, DictationLanguage = s.DictationLanguage, DictationHotkey = s.DictationHotkey,
            CheckOn = s.CheckOn, CheckHotkey = s.CheckHotkey, Actions = s.Actions,
        };

        public void Chord(int key, params int[] modifiers)
        {
            foreach (var m in modifiers) Hook.Send(m, true);
            Hook.Send(key, true);
        }

        public void Let(int key, params int[] modifiers)
        {
            Hook.Send(key, false);
            foreach (var m in modifiers) Hook.Send(m, false);
        }
    }

    const int Ctrl = FakeKeyboardHook.LCtrl, Alt = FakeKeyboardHook.LAlt, Space = FakeKeyboardHook.Space, K = FakeKeyboardHook.K;

    [Fact]
    public void WithBothFeaturesOffNoKeyIsListenedTo()
    {
        var rig = new Rig();
        Assert.False(rig.Hook.Running);
        Assert.Equal(0, rig.Hook.Starts);
        Assert.Equal(EverywhereStatus.Off, rig.Service.Status);
        Assert.Empty(rig.Probe.Probed);
        Assert.False(rig.Hook.Send(Space, true));
    }

    [Fact]
    public void TurningDictationOnListensForItsHotkeyOnly()
    {
        var rig = new Rig();
        rig.Store.Update(s => s.Everywhere.DictationOn = true);
        Assert.True(rig.Hook.Running);
        Assert.Equal(new FeatureStatus(FeatureState.On), rig.Service.Status.Dictation);
        Assert.Equal(FeatureState.Off, rig.Service.Status.Check.State);

        rig.Chord(K, Ctrl, Alt);                            // the check's chord is not taken while the check is off
        Assert.Equal(CheckPhaseOrNull(rig), null);
    }

    static CheckPhase? CheckPhaseOrNull(Rig rig) => rig.Check.Current;

    [Fact]
    public async Task TheHotkeyDrivesDictationEndToEndThroughTheHookAndHoldToTalk()
    {
        var rig = new Rig();
        rig.Store.Update(s => s.Everywhere.DictationOn = true);

        Assert.True(rig.Hook.Send(Ctrl, true) is false);
        rig.Hook.Send(Alt, true);
        Assert.True(rig.Hook.Send(Space, true));            // press: recording starts
        await Wait.Until(() => rig.Mic.Starts == 1);
        rig.Hook.Send(Space, false);                        // release: it is written
        await Wait.Until(() => rig.Inserter.Inserted.Count == 1);
        Assert.Equal(["hello world"], rig.Inserter.Inserted);
        rig.Hook.Send(Alt, false);
        rig.Hook.Send(Ctrl, false);
    }

    [Fact]
    public async Task AnAltHotkeyMasksTheMenuKeyAfterwards()
    {
        var rig = new Rig();
        rig.Store.Update(s => s.Everywhere.DictationOn = true);
        rig.Chord(Space, Ctrl, Alt);
        await Wait.Until(() => rig.Keyboard.Sent.Contains("mask"));
    }

    [Fact]
    public async Task TheHotkeyOfTheCheckOpensItsPopup()
    {
        var rig = new Rig();
        rig.Store.Update(s => s.Everywhere.CheckOn = true);
        rig.Chord(K, Ctrl, Alt);
        await Wait.Until(() => rig.Check.Current == CheckPhase.Choosing);
    }

    [Fact]
    public void AHotkeyAnotherProgramHoldsIsReportedAndNotListenedTo()
    {
        var rig = new Rig();
        rig.Probe.Taken.Add("Ctrl+Alt+Space");              // the owner's other dictation program

        rig.Store.Update(s => s.Everywhere.DictationOn = true);

        var status = rig.Service.Status.Dictation;
        Assert.Equal(FeatureState.Problem, status.State);
        Assert.Contains("Ctrl+Alt+Space", status.Message);
        Assert.Contains("already used by another program", status.Message);
        Assert.False(rig.Hook.Running);                     // not bound: the two programs would both react
        Assert.True(rig.StatusChanges > 0);
    }

    [Fact]
    public void ChangingTheHotkeyToAFreeOneClearsTheProblem()
    {
        var rig = new Rig();
        rig.Probe.Taken.Add("Ctrl+Alt+Space");
        rig.Store.Update(s => s.Everywhere.DictationOn = true);
        Assert.Equal(FeatureState.Problem, rig.Service.Status.Dictation.State);

        rig.Store.Update(s => s.Everywhere.DictationHotkey = "Ctrl+Alt+J");

        Assert.Equal(FeatureState.On, rig.Service.Status.Dictation.State);
        Assert.True(rig.Hook.Running);
    }

    [Fact]
    public void TwoFeaturesOnTheSameHotkeyAreBothHeldBackWithAReason()
    {
        var rig = new Rig();
        rig.Store.Update(s =>
        {
            s.Everywhere.DictationOn = true;
            s.Everywhere.CheckOn = true;
            s.Everywhere.CheckHotkey = s.Everywhere.DictationHotkey;
        });

        Assert.Equal(FeatureState.Problem, rig.Service.Status.Dictation.State);
        Assert.Equal(FeatureState.Problem, rig.Service.Status.Check.State);
        Assert.Contains("already the hotkey of", rig.Service.Status.Check.Message);
        Assert.False(rig.Hook.Running);
    }

    [Fact]
    public void AHotkeyTheRulesRefuseIsReportedEvenFromAHandEditedFile()
    {
        var rig = new Rig();
        rig.Store.Update(s =>
        {
            s.Everywhere.CheckOn = true;
            s.Everywhere.CheckHotkey = "Ctrl+V";
        });
        Assert.Equal(FeatureState.Problem, rig.Service.Status.Check.State);
        Assert.False(rig.Hook.Running);

        rig.Store.Update(s => s.Everywhere.CheckHotkey = "this is not a hotkey");
        Assert.Equal(FeatureState.Problem, rig.Service.Status.Check.State);
    }

    [Fact]
    public void OneFeatureCanWorkWhileTheOtherHasAProblem()
    {
        var rig = new Rig();
        rig.Probe.Taken.Add("Ctrl+Alt+K");
        rig.Store.Update(s =>
        {
            s.Everywhere.DictationOn = true;
            s.Everywhere.CheckOn = true;
        });
        Assert.Equal(FeatureState.On, rig.Service.Status.Dictation.State);
        Assert.Equal(FeatureState.Problem, rig.Service.Status.Check.State);
        Assert.True(rig.Hook.Running);
    }

    [Fact]
    public async Task TurningDictationOffStopsListeningAndDropsARecording()
    {
        var rig = new Rig();
        rig.Store.Update(s => s.Everywhere.DictationOn = true);
        rig.Chord(Space, Ctrl, Alt);
        await Wait.Until(() => rig.Dictation.Current == DictationController.Phase.Listening);

        rig.Store.Update(s => s.Everywhere.DictationOn = false);

        await Wait.Until(() => rig.Dictation.Current == DictationController.Phase.Idle);
        Assert.False(rig.Hook.Running);
        Assert.Equal(1, rig.Mic.Cancels);
        Assert.Equal(FeatureState.Off, rig.Service.Status.Dictation.State);
    }

    [Fact]
    public async Task TurningTheCheckOffClosesItsPopup()
    {
        var rig = new Rig();
        rig.Store.Update(s => s.Everywhere.CheckOn = true);
        rig.Chord(K, Ctrl, Alt);
        await Wait.Until(() => rig.Check.Current == CheckPhase.Choosing);

        rig.Store.Update(s => s.Everywhere.CheckOn = false);

        await Wait.Until(() => rig.Check.Current is null);
        Assert.Equal(1, rig.Popup.Closed);
    }

    [Fact]
    public void UnrelatedSettingsChangesDoNotAskTheSystemAgain()
    {
        var rig = new Rig();
        rig.Store.Update(s => s.Everywhere.DictationOn = true);
        var probed = rig.Probe.Probed.Count;
        var starts = rig.Hook.Starts;

        rig.Store.Update(s => s.Limits.SitePerDay = "0.40 USD");
        rig.Store.Update(s => s.WarmUpOnConnect = false);

        Assert.Equal(probed, rig.Probe.Probed.Count);
        Assert.Equal(starts, rig.Hook.Starts);
    }

    [Fact]
    public async Task EscCancelsADictationInProgressAndIsNotPassedOn()
    {
        var rig = new Rig();
        rig.Store.Update(s => s.Everywhere.DictationOn = true);
        rig.Chord(Space, Ctrl, Alt);
        await Wait.Until(() => rig.Dictation.Current == DictationController.Phase.Listening);

        Assert.True(rig.Hook.Send(0x1B, true));             // the program in front does not get this Esc
        await Wait.Until(() => rig.Dictation.Current == DictationController.Phase.Idle);
        Assert.Equal(1, rig.Mic.Cancels);

        Assert.False(rig.Hook.Send(0x1B, true));            // and when nothing is going on, Esc is the program's again
    }

    // ------------------------------------------------------------------ the help page

    [Fact]
    public void TheHelpPageIsRegisteredAndNamesTheFeaturesAndTheirDefaults()
    {
        Assert.Contains("everywhere", HelpCatalog.Topics);
        Assert.Equal("Everywhere: dictation and the check of selected text", HelpCatalog.Title("everywhere"));
        var text = HelpCatalog.Get("everywhere");
        Assert.Contains("Ctrl+Alt+Space", text);
        Assert.Contains("Ctrl+Alt+K", text);
        Assert.Contains("off", text);
        Assert.Contains("Esc", text);
        Assert.Contains("60 seconds", text);
        Assert.Contains("never one of your agents", text);
        Assert.Contains("never logged", text);
    }

    [Fact]
    public void TheHelpPageIsPlainEnglishWithNoNonAsciiLettersOfAnotherAlphabet()
    {
        var text = HelpCatalog.Get("everywhere");
        Assert.DoesNotContain(text, c => c >= 0x400 && c <= 0x4FF);
    }
}
