namespace Byoa.Wallet.Everywhere;

/// <summary>A window of another program, as the wallet remembers it between a hotkey and the answer.</summary>
/// <param name="Handle">The window handle.</param>
/// <param name="IsTerminal">A console or terminal window: Ctrl+C there is a break, not a copy.</param>
public sealed record TargetWindow(long Handle, bool IsTerminal);

/// <summary>Which window had the focus when the visitor pressed a hotkey, and getting it back after the wallet's own window had it.</summary>
public interface ITargetWindow
{
    /// <summary>The window of another program that has the focus; null when it is one of the wallet's own windows or none.</summary>
    TargetWindow? Capture();

    /// <summary>Gives the focus back to the window; false when it is gone or would not take it.</summary>
    Task<bool> RestoreAsync(TargetWindow window, CancellationToken ct);
}
