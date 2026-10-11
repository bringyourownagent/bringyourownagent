using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Byoa.Wallet.Host;
using Byoa.Wallet.Protocol;

// Chrome starts this program for the extension with the caller's origin as the first argument
// ("chrome-extension://<id>/") and talks to it over stdin/stdout. Nothing but frames may go to stdout.
var relay = new HostRelay(Console.OpenStandardInput(), Console.OpenStandardOutput(), new PipeConnector());
try
{
    await relay.RunAsync();
    return 0;
}
catch (Exception e) when (e is IOException or InvalidDataException or EndOfStreamException)
{
    Console.Error.WriteLine("byoa-host: " + e.GetType().Name);
    return 1;
}

namespace Byoa.Wallet.Host
{
    /// <summary>Connects to the wallet's per-user pipe; starts <c>Byoa.Wallet.exe</c> next to this program when asked.</summary>
    sealed class PipeConnector : IWalletConnector
    {
        public async Task<Stream?> ConnectAsync(bool startIfNeeded, CancellationToken ct)
        {
            var name = WalletPaths.PipeName();
            var pipe = await TryConnectAsync(name, TimeSpan.FromMilliseconds(400), ct).ConfigureAwait(false);
            if (pipe is not null || !startIfNeeded) return pipe;
            if (!StartWallet()) return null;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                pipe = await TryConnectAsync(name, TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
                if (pipe is not null) return pipe;
                await Task.Delay(250, ct).ConfigureAwait(false);
            }

            return null;
        }

        static async Task<Stream?> TryConnectAsync(string name, TimeSpan timeout, CancellationToken ct)
        {
            var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.ConnectAsync((int)timeout.TotalMilliseconds, ct).ConfigureAwait(false);
                return pipe;
            }
            catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                return null;
            }
        }

        /// <summary>
        /// Starts the wallet outside Chrome's job object when the job allows it, so that it keeps running after the
        /// extension's port closes; otherwise a plain start.
        /// </summary>
        static bool StartWallet()
        {
            var exe = Environment.GetEnvironmentVariable("BYOA_WALLET_EXE");
            if (string.IsNullOrWhiteSpace(exe)) exe = Path.Combine(AppContext.BaseDirectory, "Byoa.Wallet.exe");
            if (!File.Exists(exe)) return false;
            if (OperatingSystem.IsWindows() && Native.StartDetached(exe, "--background")) return true;
            try
            {
                using var p = Process.Start(new ProcessStartInfo(exe, "--background") { UseShellExecute = true });
                return p is not null;
            }
            catch (Win32Exception)
            {
                return false;
            }
        }
    }

    static class Native
    {
        const uint CreateBreakawayFromJob = 0x01000000;
        const uint CreateUnicodeEnvironment = 0x00000400;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct StartupInfo
        {
            public int cb;
            public string? lpReserved, lpDesktop, lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct ProcessInformation
        {
            public IntPtr hProcess, hThread;
            public int dwProcessId, dwThreadId;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CreateProcessW(string? app, System.Text.StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags,
            IntPtr env, string? dir, ref StartupInfo si, out ProcessInformation pi);

        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr h);

        public static bool StartDetached(string exe, string args)
        {
            var si = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>() };
            var cmd = new System.Text.StringBuilder("\"" + exe + "\" " + args);
            if (!CreateProcessW(exe, cmd, IntPtr.Zero, IntPtr.Zero, false, CreateBreakawayFromJob | CreateUnicodeEnvironment, IntPtr.Zero,
                    Path.GetDirectoryName(exe), ref si, out var pi))
            {
                return false;
            }

            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
            return true;
        }
    }
}
