namespace Byoa.Wallet.Everywhere;

public enum IndicatorKind
{
    Listening,
    Writing,

    /// <summary>Something the visitor should know that is not a failure ("Heard nothing"); it goes away on its own.</summary>
    Notice,

    /// <summary>A failure in plain words; it stays a few seconds.</summary>
    Error,
}

/// <summary>The small floating indicator of dictation.</summary>
public interface IIndicator
{
    void Show(IndicatorKind kind, string text);

    void Hide();
}

public enum CheckPhase
{
    /// <summary>The selection is read; the visitor picks an action (the first one runs on a second press of the hotkey).</summary>
    Choosing,
    Working,
    Answer,

    /// <summary>Nothing to show but a message: no selection, too long, no model, a failure.</summary>
    Message,
}

/// <summary>What the check's popup shows now.</summary>
/// <param name="Phase">Which of the popup's states to draw.</param>
/// <param name="Actions">The names of the actions: a row of buttons while choosing, the menu of the answer after it.</param>
/// <param name="Running">The action that is running or ran, or null.</param>
/// <param name="Text">The answer, or the message.</param>
/// <param name="CanReplace">The answer can be pasted over the selection (the program the text came from is still there).</param>
/// <param name="Status">A short word about what the visitor just did ("Copied") or why it did not work; null when there is nothing to say.</param>
public sealed record CheckView(CheckPhase Phase, IReadOnlyList<string> Actions, string? Running, string? Text, bool CanReplace, string? Status = null);

/// <summary>The check's popup window, near the cursor. Its buttons call the controller (<see cref="SelectionCheckController"/>).</summary>
public interface ICheckPopup
{
    void Show(CheckView view);

    void Close();
}
