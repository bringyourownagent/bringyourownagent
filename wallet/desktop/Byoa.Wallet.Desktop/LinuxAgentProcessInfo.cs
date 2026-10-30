using System.Globalization;
using System.Runtime.InteropServices;
using Byoa.Wallet.Registry;
using Microsoft.Win32.SafeHandles;

namespace Byoa.Wallet.Desktop;

/// <summary>
/// Reads the management pipe client's image and its direct parent from Linux. A pipe is a Unix socket there: the kernel tells the server the
/// process id of the program on the other end (<c>SO_PEERCRED</c>), and <c>/proc</c> gives that process's path, its parent and its start time.
/// </summary>
sealed class LinuxAgentProcessInfo : IAgentProcessInfo
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
        var (parent, started) = Stat(pid);
        var parentPath = parent is > 0 ? ImagePath(parent.Value) : null;
        return new ObservedAgentProcess(path, parentPath, (uint)pid, started);
    }

    /// <summary>The pid of the program on the other end of a connected Unix socket.</summary>
    internal static int? PeerProcessId(int socket)
    {
        var credentials = default(Credentials);
        uint size = (uint)Marshal.SizeOf<Credentials>();
        return getsockopt(socket, SolSocket, SoPeerCred, ref credentials, ref size) == 0 && size >= (uint)Marshal.SizeOf<Credentials>()
            ? credentials.ProcessId
            : null;
    }

    internal static string? ImagePath(int pid)
    {
        try
        {
            var target = File.ResolveLinkTarget("/proc/" + pid.ToString(CultureInfo.InvariantCulture) + "/exe", returnFinalTarget: true);
            return target?.FullName;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The parent pid and the start time of a process (from <c>/proc/&lt;pid&gt;/stat</c>: ppid is field 4, starttime field 22).</summary>
    static (int? Parent, DateTimeOffset? Started) Stat(int pid)
    {
        string text;
        try { text = File.ReadAllText("/proc/" + pid.ToString(CultureInfo.InvariantCulture) + "/stat"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return (null, null); }

        // the command name is in parentheses and may itself contain spaces and parentheses: fields start after its last ')'
        var close = text.LastIndexOf(')');
        if (close < 0 || close + 2 >= text.Length) return (null, null);
        var fields = text[(close + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 20) return (null, null);                     // state is fields[0], ppid fields[1], starttime fields[19]
        var parent = int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ppid) ? ppid : (int?)null;
        if (!long.TryParse(fields[19], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks) || ticks <= 0) return (parent, null);
        var boot = BootTime();
        return (parent, boot?.AddSeconds((double)ticks / ClockTicksPerSecond()));
    }

    static long ClockTicksPerSecond()
    {
        try
        {
            var ticks = sysconf(ClockTicks);
            return ticks > 0 ? ticks : 100;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return 100;
        }
    }

    /// <summary>When the machine started, from <c>btime</c> in <c>/proc/stat</c>.</summary>
    static DateTimeOffset? BootTime()
    {
        try
        {
            foreach (var line in File.ReadLines("/proc/stat"))
                if (line.StartsWith("btime ", StringComparison.Ordinal)
                    && long.TryParse(line[6..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
                    return DateTimeOffset.FromUnixTimeSeconds(seconds);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
        {
            // no boot time to add: the process facts are still useful without it
        }

        return null;
    }

    const int SolSocket = 1;
    const int SoPeerCred = 17;
    const int ClockTicks = 2;                                                // _SC_CLK_TCK

    [StructLayout(LayoutKind.Sequential)]
    struct Credentials
    {
        public int ProcessId;
        public uint UserId;
        public uint GroupId;
    }

    [DllImport("libc", SetLastError = true)]
    static extern int getsockopt(int socket, int level, int name, ref Credentials value, ref uint length);

    [DllImport("libc", SetLastError = true)]
    static extern long sysconf(int name);
}