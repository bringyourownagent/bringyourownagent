namespace Byoa.Wallet.Everywhere;

/// <summary>One format of what was on the clipboard, with its bytes.</summary>
public sealed record ClipboardItem(uint Format, byte[] Data);

/// <summary>What was on the clipboard, kept so that it can be put back (a copy of every format that can be copied; an empty clipboard is a snapshot too).</summary>
public sealed record ClipboardSnapshot(IReadOnlyList<ClipboardItem> Items);

/// <summary>The clipboard could not be opened or written (another program holds it).</summary>
public sealed class ClipboardException(string message) : Exception(message);

/// <summary>The clipboard of Windows, as far as the wallet needs it.</summary>
public interface IClipboard
{
    /// <summary>Changes with every write by any program: how the wallet knows that a copy happened.</summary>
    long SequenceNumber { get; }

    /// <exception cref="ClipboardException"/>
    ClipboardSnapshot Save();

    /// <exception cref="ClipboardException"/>
    void Restore(ClipboardSnapshot snapshot);

    /// <summary>The text on the clipboard, or null when there is none.</summary>
    /// <exception cref="ClipboardException"/>
    string? GetText();

    /// <param name="text">The text to put on the clipboard.</param>
    /// <param name="keepOut">True: the text is kept out of the clipboard history (Win+V) and out of the cloud clipboard, because it is the visitor's dictation or a selection.</param>
    /// <exception cref="ClipboardException"/>
    void SetText(string text, bool keepOut);
}
