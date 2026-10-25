namespace Byoa.Wallet.Everywhere;

/// <summary>Why the selected text could not be read; the message is for the visitor.</summary>
public sealed class SelectionException(string message) : Exception(message);

public sealed record SelectionOptions
{
    /// <summary>How long to wait for the visitor to let go of the hotkey's keys before Ctrl+C is sent.</summary>
    public TimeSpan ModifierWait { get; init; } = TimeSpan.FromMilliseconds(1200);

    public TimeSpan ModifierStep { get; init; } = TimeSpan.FromMilliseconds(15);

    /// <summary>How long the program may take to put the selection on the clipboard (a browser with a big selection is slow).</summary>
    public TimeSpan CopyWait { get; init; } = TimeSpan.FromMilliseconds(1000);

    public TimeSpan CopyStep { get; init; } = TimeSpan.FromMilliseconds(20);

    /// <summary>A program writes the formats one after the other: a moment after the first change, the text is surely there.</summary>
    public TimeSpan CopySettle { get; init; } = TimeSpan.FromMilliseconds(40);
}

/// <summary>
/// Reads the text the visitor selected in another program: the clipboard is saved, Ctrl+C is sent, the clipboard's change is awaited and its text read,
/// and the old clipboard comes back. A terminal is refused first: Ctrl+C there stops the program that runs in it.
/// </summary>
public sealed class SelectionReader(IClipboard clipboard, IKeyboard keyboard, SelectionOptions? options = null) : ISelectionReader
{
    const int VkC = 0x43;
    readonly SelectionOptions _options = options ?? new SelectionOptions();

    /// <summary>The selected text, or null when nothing was selected (the program copied nothing).</summary>
    /// <exception cref="SelectionException"/>
    public async Task<string?> ReadAsync(TargetWindow? target, CancellationToken ct)
    {
        if (target is { IsTerminal: true })
            throw new SelectionException("The check does not copy from a terminal: Ctrl+C there would stop the program running in it. Copy the text yourself and use it in another window.");

        await keyboard.WaitForModifiersReleasedAsync(_options.ModifierWait, _options.ModifierStep, ct).ConfigureAwait(false);
        ClipboardSnapshot saved;
        long before;
        try
        {
            saved = clipboard.Save();
            before = clipboard.SequenceNumber;
        }
        catch (ClipboardException e)
        {
            throw new SelectionException("The clipboard is busy (" + e.Message + "). Try again in a moment.");
        }

        try
        {
            keyboard.Chord(HotkeyModifiers.Ctrl, VkC);
            var waited = TimeSpan.Zero;
            while (clipboard.SequenceNumber == before && waited < _options.CopyWait)
            {
                await Task.Delay(_options.CopyStep, ct).ConfigureAwait(false);
                waited += _options.CopyStep;
            }

            if (clipboard.SequenceNumber == before) return null;
            await Task.Delay(_options.CopySettle, ct).ConfigureAwait(false);
            try
            {
                var text = clipboard.GetText();
                return string.IsNullOrWhiteSpace(text) ? null : text;
            }
            catch (ClipboardException)
            {
                return null;
            }
        }
        finally
        {
            try { clipboard.Restore(saved); }
            catch (ClipboardException)
            {
                // the old clipboard could not be put back; the selection stays on it
            }
        }
    }
}
