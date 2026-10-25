namespace Byoa.Wallet.Everywhere;

/// <summary>The text could not be put into the program; the message is for the visitor.</summary>
public class InsertException(string message) : Exception(message);

/// <summary>The clipboard was not available before anything was sent: nothing reached the program, so another way may still work.</summary>
public sealed class ClipboardUnavailableException(string message) : InsertException(message);

/// <summary>Puts a text into the field that has the focus, as if the visitor had typed or pasted it.</summary>
public interface ITextInserter
{
    /// <exception cref="InsertException"/>
    Task InsertAsync(string text, CancellationToken ct);
}

/// <summary>The waits of the insertion, in one place so that a test can make them zero.</summary>
public sealed record InsertOptions
{
    /// <summary>How long to wait for the visitor to let go of the hotkey's Ctrl and Alt before the wallet sends its own Ctrl+V.</summary>
    public TimeSpan ModifierWait { get; init; } = TimeSpan.FromMilliseconds(1200);

    public TimeSpan ModifierStep { get; init; } = TimeSpan.FromMilliseconds(15);

    /// <summary>
    /// After Ctrl+V the program reads the clipboard in its own time (a browser or an Electron program may take a few hundred milliseconds); the old
    /// content is put back only after this wait, or the program would paste it instead.
    /// </summary>
    public TimeSpan PasteSettle { get; init; } = TimeSpan.FromMilliseconds(350);
}

/// <summary>
/// Inserts by the clipboard: the clipboard is saved, the text is put on it (kept out of the clipboard history and the cloud clipboard), Ctrl+V is
/// sent, and the old clipboard comes back. This is the wallet's way, and it is the more robust of the two: the program receives the whole text in
/// one paste, so a long text, a line break, an emoji or a letter that the keyboard layout lacks arrives intact, and nothing in the program reacts
/// to every character as to a key press (an editor's auto-closing bracket, a chat's Enter that sends the message). Typing the characters one by
/// one fails in just those places, and it is slow for a long text. The clipboard way needs the clipboard free for a moment; when it is not, the
/// <see cref="FallbackInserter"/> types instead.
/// </summary>
public sealed class ClipboardInserter(IClipboard clipboard, IKeyboard keyboard, InsertOptions? options = null) : ITextInserter
{
    const int VkV = 0x56;
    readonly InsertOptions _options = options ?? new InsertOptions();

    public async Task InsertAsync(string text, CancellationToken ct)
    {
        if (text.Length == 0) return;
        await keyboard.WaitForModifiersReleasedAsync(_options.ModifierWait, _options.ModifierStep, ct).ConfigureAwait(false);

        ClipboardSnapshot saved;
        long ours;
        try
        {
            saved = clipboard.Save();
            clipboard.SetText(text, keepOut: true);
            ours = clipboard.SequenceNumber;
        }
        catch (ClipboardException e)
        {
            throw new ClipboardUnavailableException(e.Message);
        }

        try
        {
            keyboard.Chord(HotkeyModifiers.Ctrl, VkV);
            await Task.Delay(_options.PasteSettle, CancellationToken.None).ConfigureAwait(false);
        }
        catch (KeyboardException e)
        {
            throw new InsertException(e.Message);
        }
        finally
        {
            // If another program wrote to the clipboard meanwhile (the visitor copied something), that is newer than what was saved: leave it.
            try
            {
                if (clipboard.SequenceNumber == ours) clipboard.Restore(saved);
            }
            catch (ClipboardException)
            {
                // the old content could not be put back: the text stays on the clipboard, which is a nuisance and not a loss
            }
        }
    }
}

/// <summary>Types the text as Unicode key events: slower, and the program sees every character as a key, but it needs no clipboard.</summary>
public sealed class TypingInserter(IKeyboard keyboard, InsertOptions? options = null) : ITextInserter
{
    readonly InsertOptions _options = options ?? new InsertOptions();

    public async Task InsertAsync(string text, CancellationToken ct)
    {
        if (text.Length == 0) return;
        await keyboard.WaitForModifiersReleasedAsync(_options.ModifierWait, _options.ModifierStep, ct).ConfigureAwait(false);
        try
        {
            keyboard.TypeText(text);
        }
        catch (KeyboardException e)
        {
            throw new InsertException(e.Message);
        }
    }
}

/// <summary>The clipboard way, and typing when the clipboard cannot be used (and nothing was sent yet).</summary>
public sealed class FallbackInserter(ITextInserter primary, ITextInserter fallback) : ITextInserter
{
    public async Task InsertAsync(string text, CancellationToken ct)
    {
        try
        {
            await primary.InsertAsync(text, ct).ConfigureAwait(false);
        }
        catch (ClipboardUnavailableException)
        {
            await fallback.InsertAsync(text, ct).ConfigureAwait(false);
        }
    }
}
