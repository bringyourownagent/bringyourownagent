using System.Text;
using Byoa.Wallet.Everywhere;

namespace Byoa.Wallet.Tests;

/// <summary>Putting text into another program (clipboard paste, typing, the fallback) and reading its selection, against a fake clipboard and keyboard.</summary>
public class EverywhereInsertTests
{
    static readonly InsertOptions Quick = new()
    {
        ModifierWait = TimeSpan.FromMilliseconds(200), ModifierStep = TimeSpan.FromMilliseconds(5), PasteSettle = TimeSpan.Zero,
    };

    static readonly SelectionOptions QuickCopy = new()
    {
        ModifierWait = TimeSpan.FromMilliseconds(200), ModifierStep = TimeSpan.FromMilliseconds(5),
        CopyWait = TimeSpan.FromMilliseconds(100), CopyStep = TimeSpan.FromMilliseconds(5), CopySettle = TimeSpan.Zero,
    };

    readonly FakeClipboard _clipboard = new();
    readonly FakeKeyboard _keyboard = new();

    // ------------------------------------------------------------------ the clipboard way

    [Fact]
    public async Task PasteSavesTheClipboardPutsTheTextSendsCtrlVAndPutsTheOldClipboardBack()
    {
        _clipboard.Text = "what the visitor had copied";
        var seenAtPaste = "";
        _keyboard.OnChord = (_, _) => seenAtPaste = _clipboard.Text!;
        var inserter = new ClipboardInserter(_clipboard, _keyboard, Quick);

        await inserter.InsertAsync("dictated words", CancellationToken.None);

        Assert.Equal(["Ctrl+V"], _keyboard.Sent);
        Assert.Equal("dictated words", seenAtPaste);        // the program saw the dictated text at the moment of the paste
        Assert.Equal("what the visitor had copied", _clipboard.Text);
        Assert.Equal(["save", "set", "restore"], _clipboard.Log);
    }

    [Fact]
    public async Task TheDictatedTextIsKeptOutOfTheClipboardHistory()
    {
        var inserter = new ClipboardInserter(_clipboard, _keyboard, Quick);
        await inserter.InsertAsync("private words", CancellationToken.None);
        Assert.True(_clipboard.LastKeepOut);
    }

    [Fact]
    public async Task AnEmptyClipboardComesBackEmptyAndEveryFormatComesBack()
    {
        var inserter = new ClipboardInserter(_clipboard, _keyboard, Quick);
        await inserter.InsertAsync("words", CancellationToken.None);
        Assert.Empty(_clipboard.Items);

        _clipboard.Items = [new ClipboardItem(13, Encoding.Unicode.GetBytes("text")), new ClipboardItem(15, [1, 2, 3]), new ClipboardItem(49300, [9])];
        await inserter.InsertAsync("words", CancellationToken.None);
        Assert.Equal(3, _clipboard.Items.Count);            // a file list, an image, a private format: not only the text
        Assert.Equal([1, 2, 3], _clipboard.Items[1].Data);
    }

    [Fact]
    public async Task ACopyTheVisitorMadeDuringThePasteIsNotOverwrittenByTheRestore()
    {
        _clipboard.Text = "old";
        _keyboard.OnChord = (_, _) =>
        {
            _clipboard.Text = "copied meanwhile";           // the visitor pressed Ctrl+C in some other window
            _clipboard.Seq++;
        };
        var inserter = new ClipboardInserter(_clipboard, _keyboard, Quick);
        await inserter.InsertAsync("words", CancellationToken.None);
        Assert.Equal("copied meanwhile", _clipboard.Text);
        Assert.DoesNotContain("restore", _clipboard.Log);
    }

    [Fact]
    public async Task EmptyTextSendsNothing()
    {
        var inserter = new ClipboardInserter(_clipboard, _keyboard, Quick);
        await inserter.InsertAsync("", CancellationToken.None);
        Assert.Empty(_keyboard.Sent);
        Assert.Empty(_clipboard.Log);
    }

    [Fact]
    public async Task ABusyClipboardIsUnavailableBeforeAnythingIsSent()
    {
        _clipboard.Busy = true;
        var inserter = new ClipboardInserter(_clipboard, _keyboard, Quick);
        await Assert.ThrowsAsync<ClipboardUnavailableException>(() => inserter.InsertAsync("words", CancellationToken.None));
        Assert.Empty(_keyboard.Sent);
    }

    [Fact]
    public async Task IfTheOldClipboardCannotBePutBackTheTextIsStillTyped()
    {
        _keyboard.OnChord = (_, _) => _clipboard.Busy = true;     // another program grabs the clipboard right after the paste
        var inserter = new ClipboardInserter(_clipboard, _keyboard, Quick);
        await inserter.InsertAsync("words", CancellationToken.None);   // no exception: the words were typed
        Assert.Equal(["Ctrl+V"], _keyboard.Sent);
    }

    [Fact]
    public async Task AProgramThatWindowsWillNotTypeIntoIsAnInsertErrorAndTheClipboardIsPutBack()
    {
        _clipboard.Text = "mine";
        _keyboard.Refuse = "Windows would not let the wallet type into that program (it may run as administrator).";
        var inserter = new ClipboardInserter(_clipboard, _keyboard, Quick);
        var e = await Assert.ThrowsAsync<InsertException>(() => inserter.InsertAsync("words", CancellationToken.None));
        Assert.IsNotType<ClipboardUnavailableException>(e);        // typing would be refused as well: no fallback
        Assert.Contains("administrator", e.Message);
        Assert.Equal("mine", _clipboard.Text);
    }

    [Fact]
    public async Task TypingThatWindowsRefusesIsAnInsertError()
    {
        _keyboard.Refuse = "refused";
        var inserter = new TypingInserter(_keyboard, Quick);
        await Assert.ThrowsAsync<InsertException>(() => inserter.InsertAsync("words", CancellationToken.None));
    }

    [Fact]
    public async Task ACopyThatWindowsRefusesIsASelectionError()
    {
        _clipboard.Text = "mine";
        _keyboard.Refuse = "refused";
        var reader = new SelectionReader(_clipboard, _keyboard, QuickCopy);
        var e = await Assert.ThrowsAsync<SelectionException>(() => reader.ReadAsync(null, CancellationToken.None));
        Assert.Equal("refused", e.Message);
        Assert.Equal("mine", _clipboard.Text);
    }

    // ------------------------------------------------------------------ the hotkey's own keys

    [Fact]
    public async Task TheWalletWaitsForCtrlAndAltToBeLetGoSoThePasteIsNotCtrlAltV()
    {
        _keyboard.Down.Add(FakeKeyboardHook.LCtrl);
        _keyboard.Down.Add(FakeKeyboardHook.LAlt);
        var inserter = new ClipboardInserter(_clipboard, _keyboard, Quick);
        var letGo = Task.Run(async () =>
        {
            await Task.Delay(40);
            _keyboard.Down.Clear();
        });

        await inserter.InsertAsync("words", CancellationToken.None);
        await letGo;

        Assert.Equal(["Ctrl+V"], _keyboard.Sent);           // no forced key-up: the visitor let go by himself
    }

    [Fact]
    public async Task AKeyStillHeldWhenTheWaitIsOverIsReleasedForTheProgram()
    {
        _keyboard.Down.Add(FakeKeyboardHook.LAlt);          // the visitor keeps Alt down
        var inserter = new ClipboardInserter(_clipboard, _keyboard, Quick);
        await inserter.InsertAsync("words", CancellationToken.None);
        Assert.Equal(["up:A4", "Ctrl+V"], _keyboard.Sent);  // Alt is let go first, then Ctrl+V arrives as Ctrl+V
    }

    // ------------------------------------------------------------------ typing and the fallback

    [Fact]
    public async Task TypingSendsTheCharactersAsUnicodeKeys()
    {
        var inserter = new TypingInserter(_keyboard, Quick);
        await inserter.InsertAsync("héllo — wörld ✓", CancellationToken.None);
        Assert.Equal(["type:héllo — wörld ✓"], _keyboard.Sent);
        Assert.Empty(_clipboard.Log);                       // the clipboard is not touched
    }

    [Fact]
    public async Task WhenTheClipboardIsBusyTheFallbackTypesInstead()
    {
        _clipboard.Busy = true;
        var inserter = new FallbackInserter(new ClipboardInserter(_clipboard, _keyboard, Quick), new TypingInserter(_keyboard, Quick));
        await inserter.InsertAsync("words", CancellationToken.None);
        Assert.Equal(["type:words"], _keyboard.Sent);
    }

    [Fact]
    public async Task WhenThePasteWasSentTheFallbackDoesNotTypeTheTextAgain()
    {
        var inserter = new FallbackInserter(new ClipboardInserter(_clipboard, _keyboard, Quick), new TypingInserter(_keyboard, Quick));
        await inserter.InsertAsync("words", CancellationToken.None);
        Assert.Equal(["Ctrl+V"], _keyboard.Sent);
    }

    [Fact]
    public async Task AnotherFailureIsNotHiddenByTheFallback()
    {
        var failing = new RecordingInserter { Fail = new InsertException("no such window") };
        var inserter = new FallbackInserter(failing, new TypingInserter(_keyboard, Quick));
        var e = await Assert.ThrowsAsync<InsertException>(() => inserter.InsertAsync("words", CancellationToken.None));
        Assert.Equal("no such window", e.Message);
        Assert.Empty(_keyboard.Sent);
    }

    // ------------------------------------------------------------------ reading the selection

    /// <summary>A program that copies its selection to the clipboard when it receives Ctrl+C.</summary>
    void ProgramWithSelection(string? selection)
    {
        _keyboard.OnChord = (modifiers, key) =>
        {
            if (modifiers == HotkeyModifiers.Ctrl && key == 0x43 && selection is not null) _clipboard.SetText(selection, keepOut: false);
        };
    }

    [Fact]
    public async Task TheSelectionIsCopiedReadAndTheClipboardRestored()
    {
        _clipboard.Text = "the visitor's own copy";
        ProgramWithSelection("selected words");
        var reader = new SelectionReader(_clipboard, _keyboard, QuickCopy);

        var text = await reader.ReadAsync(new TargetWindow(7, false), CancellationToken.None);

        Assert.Equal("selected words", text);
        Assert.Equal(["Ctrl+C"], _keyboard.Sent);
        Assert.Equal("the visitor's own copy", _clipboard.Text);
    }

    [Fact]
    public async Task NothingSelectedIsNullAndTheClipboardIsLeftAlone()
    {
        _clipboard.Text = "keep me";
        ProgramWithSelection(null);                         // the program copies nothing
        var reader = new SelectionReader(_clipboard, _keyboard, QuickCopy);
        Assert.Null(await reader.ReadAsync(null, CancellationToken.None));
        Assert.Equal("keep me", _clipboard.Text);
    }

    [Fact]
    public async Task ASelectionOfOnlySpacesCountsAsNothing()
    {
        ProgramWithSelection("   \r\n  ");
        var reader = new SelectionReader(_clipboard, _keyboard, QuickCopy);
        Assert.Null(await reader.ReadAsync(null, CancellationToken.None));
    }

    [Fact]
    public async Task ATerminalIsRefusedBeforeCtrlCIsSent()
    {
        var reader = new SelectionReader(_clipboard, _keyboard, QuickCopy);
        var e = await Assert.ThrowsAsync<SelectionException>(() => reader.ReadAsync(new TargetWindow(7, IsTerminal: true), CancellationToken.None));
        Assert.Contains("terminal", e.Message);
        Assert.Empty(_keyboard.Sent);                       // Ctrl+C would have stopped the program running there
    }

    [Fact]
    public async Task ABusyClipboardIsAPlainMessage()
    {
        _clipboard.Busy = true;
        var reader = new SelectionReader(_clipboard, _keyboard, QuickCopy);
        var e = await Assert.ThrowsAsync<SelectionException>(() => reader.ReadAsync(null, CancellationToken.None));
        Assert.Contains("clipboard is busy", e.Message);
        Assert.Empty(_keyboard.Sent);
    }

    [Fact]
    public async Task TheWalletWaitsForTheHotkeyKeysBeforeCopying()
    {
        ProgramWithSelection("selected");
        _keyboard.Down.Add(FakeKeyboardHook.LCtrl);
        _keyboard.Down.Add(FakeKeyboardHook.LAlt);
        var reader = new SelectionReader(_clipboard, _keyboard, QuickCopy);
        var sentWhileHeld = false;
        var original = _keyboard.OnChord;
        _keyboard.OnChord = (m, k) =>
        {
            sentWhileHeld = _keyboard.Down.Count > 0;
            original?.Invoke(m, k);
        };
        _ = Task.Run(async () =>
        {
            await Task.Delay(40);
            _keyboard.Down.Clear();
        });

        Assert.Equal("selected", await reader.ReadAsync(null, CancellationToken.None));
        Assert.False(sentWhileHeld);
    }
}
