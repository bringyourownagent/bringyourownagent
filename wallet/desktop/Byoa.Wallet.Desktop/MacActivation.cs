using System.Runtime.InteropServices;

namespace Byoa.Wallet.Desktop;

/// <summary>
/// A menu-bar program (no Dock icon) is not the active application when its window opens, so the window can come up behind the browser the
/// person is looking at. A window the person asked for (the settings from the menu-bar icon, the help, the add-a-model window) brings the
/// application to the front, like any program's window does.
/// </summary>
static class MacActivation
{
    /// <summary>Makes this application the active one. Does nothing away from macOS, and nothing when the Objective-C runtime does not answer.</summary>
    public static void BringToFront()
    {
        if (!OperatingSystem.IsMacOS()) return;
        try
        {
            var app = SendPtr(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
            if (app != 0) SendBool(app, sel_registerName("activateIgnoringOtherApps:"), 1);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            // no Objective-C runtime to ask: the window opens where it opens
        }
    }

    [DllImport("/usr/lib/libobjc.A.dylib")] static extern nint objc_getClass(string name);
    [DllImport("/usr/lib/libobjc.A.dylib")] static extern nint sel_registerName(string name);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] static extern nint SendPtr(nint receiver, nint selector);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] static extern void SendBool(nint receiver, nint selector, byte value);
}
