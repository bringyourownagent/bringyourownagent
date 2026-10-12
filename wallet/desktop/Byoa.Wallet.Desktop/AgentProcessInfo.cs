using System.Runtime.InteropServices;
using System.Text;
using Byoa.Wallet.Protocol;
using Byoa.Wallet.Registry;
using Microsoft.Win32.SafeHandles;

namespace Byoa.Wallet.Desktop;

/// <summary>Reads the management pipe client's image and its direct parent from Windows.</summary>
sealed class WindowsAgentProcessInfo : IAgentProcessInfo
{
    public ObservedAgentProcess Read(SafePipeHandle pipe)
    {
        try
        {
            if (!GetNamedPipeClientProcessId(pipe, out var clientPid) || clientPid == 0)
                return new ObservedAgentProcess(null, null);

            var clientPath = QueryPath(clientPid);
            var parentPid = ParentProcessId(clientPid);
            var parentPath = parentPid is { } pid && pid != 0 ? QueryPath(pid) : null;
            return new ObservedAgentProcess(clientPath, parentPath);
        }
        catch
        {
            return new ObservedAgentProcess(null, null);
        }
    }

    static uint? ParentProcessId(uint processId)
    {
        var snapshot = CreateToolhelp32Snapshot(SnapshotFlags.Process, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return null;
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            if (!Process32First(snapshot, ref entry)) return null;
            do
            {
                if (entry.ProcessId == processId) return entry.ParentProcessId;
            } while (Process32Next(snapshot, ref entry));
        }
        finally { CloseHandle(snapshot); }
        return null;
    }

    static string? QueryPath(uint processId)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var path = new StringBuilder(1024);
            var length = (uint)path.Capacity;
            return QueryFullProcessImageName(handle, 0, path, ref length) ? path.ToString() : null;
        }
        finally { CloseHandle(handle); }
    }

    [Flags] enum SnapshotFlags : uint { Process = 0x00000002 }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr CreateToolhelp32Snapshot(SnapshotFlags flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool Process32First(IntPtr snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref uint size);

    [DllImport("kernel32.dll", EntryPoint = "CloseHandle", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);
}
