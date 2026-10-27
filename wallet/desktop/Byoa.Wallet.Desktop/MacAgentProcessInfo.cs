using System.Runtime.InteropServices;
using System.Text;
using Byoa.Wallet.Registry;
using Microsoft.Win32.SafeHandles;

namespace Byoa.Wallet.Desktop;

/// <summary>
/// Reads the management pipe client's image and its direct parent from macOS. A pipe is a Unix socket there: the system tells the server
/// the process id of the program on the other end of a connected socket (<c>LOCAL_PEERPID</c>), and <c>libproc</c> gives that process's
/// path, its parent and its start time.
/// </summary>
sealed class MacAgentProcessInfo : IAgentProcessInfo
{
    public ObservedAgentProcess? Read(SafePipeHandle pipe)
    {
        try
        {
            if (pipe.IsInvalid || pipe.IsClosed) return null;
            var clientPid = PeerProcessId((int)pipe.DangerousGetHandle());
            if (clientPid is null or <= 0) return null;
            return Describe(clientPid.Value);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or ObjectDisposedException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The facts of one process (also the wallet's own tests ask about a process they started).</summary>
    internal static ObservedAgentProcess? Describe(int pid)
    {
        var path = ImagePath(pid);
        if (path is null) return null;
        var (parent, started) = BsdInfo(pid);
        var parentPath = parent is > 0 ? ImagePath(parent.Value) : null;
        return new ObservedAgentProcess(path, parentPath, (uint)pid, started);
    }

    /// <summary>The pid of the program on the other end of a connected Unix socket.</summary>
    internal static int? PeerProcessId(int socket)
    {
        uint size = sizeof(int);
        return getsockopt(socket, SolLocal, LocalPeerPid, out var pid, ref size) == 0 && size == sizeof(int) ? pid : null;
    }

    internal static string? ImagePath(int pid)
    {
        var buffer = new byte[ProcPidPathInfoMaxSize];
        var length = proc_pidpath(pid, buffer, (uint)buffer.Length);
        return length > 0 ? Encoding.UTF8.GetString(buffer, 0, length) : null;
    }

    /// <summary>The parent pid and the start time of a process (<c>struct proc_bsdinfo</c>: ppid at byte 16, start seconds at 120, microseconds at 128).</summary>
    static (int? Parent, DateTimeOffset? Started) BsdInfo(int pid)
    {
        var buffer = Marshal.AllocHGlobal(BsdInfoSize);
        try
        {
            if (proc_pidinfo(pid, ProcPidTbsdInfo, 0, buffer, BsdInfoSize) != BsdInfoSize) return (null, null);
            var parent = (int)(uint)Marshal.ReadInt32(buffer, 16);
            var seconds = Marshal.ReadInt64(buffer, 120);
            var micros = Marshal.ReadInt64(buffer, 128);
            return (parent, seconds > 0 ? DateTimeOffset.FromUnixTimeSeconds(seconds).AddTicks(micros * 10) : null);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    const int SolLocal = 0;
    const int LocalPeerPid = 2;
    const int ProcPidTbsdInfo = 3;
    const int BsdInfoSize = 136;
    const int ProcPidPathInfoMaxSize = 4096;

    [DllImport("libSystem", SetLastError = true)]
    static extern int getsockopt(int socket, int level, int name, out int value, ref uint length);

    [DllImport("libSystem", SetLastError = true)]
    static extern int proc_pidpath(int pid, byte[] buffer, uint size);

    [DllImport("libSystem", SetLastError = true)]
    static extern int proc_pidinfo(int pid, int flavor, ulong argument, IntPtr buffer, int size);
}
