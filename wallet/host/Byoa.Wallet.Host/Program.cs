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
catch (Exception e) when (e is IOException or InvalidDataException or EndOfStreamException or ObjectDisposedException)
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
            if (string.IsNullOrWhiteSpace(exe)) exe = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Byoa.Wallet.exe" : "Byoa.Wallet");
            if (!File.Exists(exe)) return false;
            if (OperatingSystem.IsWindows() && Native.StartDetached(exe, "--background")) return true;
            try
            {
                if (OperatingSystem.IsMacOS()) return StartOnMac(exe);
                using var p = Process.Start(new ProcessStartInfo(exe, "--background") { UseShellExecute = true });
                return p is not null;
            }
            catch (Win32Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// macOS: an application bundle is opened through the system (<c>open -g</c>: in the background, as the user's own program, not as a
        /// child of the browser); a bare program (a build run from a folder) is started detached from this host and from its pipes, because
        /// this program's standard output is the extension's channel.
        /// </summary>
        static bool StartOnMac(string exe)
        {
            var bundle = BundleOf(exe);
            ProcessStartInfo info;
            if (bundle is not null)
            {
                info = new ProcessStartInfo("/usr/bin/open") { ArgumentList = { "-g", "-a", bundle } };
                // a program the system starts gets the system's environment, not this one: the data folder of a wallet run apart (a test) is handed on
                foreach (var name in new[] { WalletPaths.HomeVariable, NativeHostManifests.RootVariable })
                    if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value) { info.ArgumentList.Add("--env"); info.ArgumentList.Add(name + "=" + value); }
                info.ArgumentList.Add("--args");
                info.ArgumentList.Add("--background");
            }
            else
            {
                info = new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", "nohup \"$0\" --background >/dev/null 2>&1 </dev/null &", exe } };
            }

            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            using var p = Process.Start(info);
            if (p is null) return false;
            p.WaitForExit(5000);
            return true;
        }

        /// <summary>The <c>.app</c> folder a program inside <c>Contents/MacOS</c> belongs to; null for a program outside a bundle.</summary>
        internal static string? BundleOf(string exe)
        {
            for (var folder = Path.GetDirectoryName(exe); !string.IsNullOrEmpty(folder); folder = Path.GetDirectoryName(folder))
                if (folder.EndsWith(".app", StringComparison.Ordinal)) return folder;
            return null;
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
