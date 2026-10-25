namespace Byoa.Wallet.Everywhere;

/// <summary>One key going down or up, as the system's low-level keyboard hook reports it.</summary>
/// <param name="VirtualKey">The Windows virtual-key code (left and right Ctrl, Alt, Shift are told apart).</param>
/// <param name="Down">True for a key going down (a held key repeats), false for a key going up.</param>
/// <param name="Injected">True when a program sent it (SendInput), not the keyboard: the wallet's own keystrokes are never taken for the visitor's.</param>
public readonly record struct KeyEvent(int VirtualKey, bool Down, bool Injected);

/// <summary>
/// A listener for every key the visitor presses, in every program (<c>WH_KEYBOARD_LL</c>). The handler is called on the hook's own thread and must
/// return at once: Windows drops a hook that is slow. It answers true to keep the key from the program that has the focus.
/// </summary>
public interface IKeyboardHook : IDisposable
{
    void Start(Func<KeyEvent, bool> handler);

    void Stop();
}
