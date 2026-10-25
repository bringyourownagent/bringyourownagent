namespace Byoa.Wallet.Everywhere;

/// <summary>What the system said when the wallet asked for a hotkey to see whether another program already holds it.</summary>
/// <param name="Free">True when nobody holds it (the wallet let it go again at once).</param>
/// <param name="Detail">When not free: what the system said, for the plain message; null when it said nothing useful.</param>
public sealed record HotkeyProbeResult(bool Free, string? Detail = null);

/// <summary>
/// Asks the system whether a hotkey is already taken by another program (a dictation program, a launcher, a screenshot tool). The wallet listens
/// with a keyboard hook, which is never told "taken", so it asks the system's own registry of hotkeys once, when the visitor chooses one. A program
/// that listens with a hook of its own cannot be found this way.
/// </summary>
public interface IHotkeyProbe
{
    HotkeyProbeResult Probe(Hotkey hotkey);
}
