using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Byoa.Wallet.Everywhere;

namespace Byoa.Wallet.Desktop;

/// <summary>
/// Which program the visitor was in when he pressed the check's hotkey, and getting the focus back to it after the wallet's popup had it (Replace
/// selection pastes into that program). A window of the wallet's own process is never a target. Terminals are marked, because Ctrl+C there is a break.
/// </summary>
[SupportedOSPlatform("windows")]
sealed class WinTargetWindow : ITargetWindow
{
    const int ShowRestore = 9;

    /// <summary>Window classes of consoles and terminal programs.</summary>
    static readonly string[] Terminals =
    [
        "ConsoleWindowClass", "CASCADIA_HOSTING_WINDOW_CLASS", "PseudoConsoleWindow", "mintty", "VirtualConsoleClass", "PuTTY", "Alacritty", "org.wezfurlong.wezterm",
    ];

    public TargetWindow? Capture()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero) return null;
        GetWindowThreadProcessId(window, out var process);
        if (process == Environment.ProcessId) return null;
        var name = new StringBuilder(256);
        GetClassName(window, name, name.Capacity);
        return new TargetWindow(window.ToInt64(), Terminals.Contains(name.ToString(), StringComparer.OrdinalIgnoreCase));
    }

    public async Task<bool> RestoreAsync(TargetWindow target, CancellationToken ct)
    {
        var window = new IntPtr(target.Handle);
        if (!IsWindow(window)) return false;
        if (IsIconic(window)) ShowWindow(window, ShowRestore);
        if (GetForegroundWindow() != window)
        {
            // A program may give the focus away when it has just been given input; if Windows says no, attach to the input of the window in front.
            if (!SetForegroundWindow(window))
            {
                var front = GetForegroundWindow();
                var frontThread = GetWindowThreadProcessId(front, out _);
                var thisThread = GetCurrentThreadId();
                var attached = frontThread != 0 && frontThread != thisThread && AttachThreadInput(thisThread, frontThread, true);
                SetForegroundWindow(window);
                BringWindowToTop(window);
                if (attached) AttachThreadInput(thisThread, frontThread, false);
            }
        }

        // The window takes a moment to be in front, and its own text field a moment longer to have the focus again.
        for (var waited = 0; waited < 500 && GetForegroundWindow() != window; waited += 20) await Task.Delay(20, ct).ConfigureAwait(false);
        if (GetForegroundWindow() != window) return false;
        await Task.Delay(120, ct).ConfigureAwait(false);
        return true;
    }

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out int process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr window, StringBuilder name, int size);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool BringWindowToTop(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool AttachThreadInput(uint from, uint to, [MarshalAs(UnmanagedType.Bool)] bool attach);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
}
