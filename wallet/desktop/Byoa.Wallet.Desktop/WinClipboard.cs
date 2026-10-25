using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Avalonia.Threading;
using Byoa.Wallet.Everywhere;

namespace Byoa.Wallet.Desktop;

/// <summary>
/// The Windows clipboard, with a copy of everything on it that can be copied. The clipboard has to be opened with a window as its owner (with none,
/// the data the wallet puts on it is refused), and that window must live on a thread that handles messages, or a program that takes the clipboard
/// next would wait for it: so every call runs on the wallet's UI thread, with a small invisible window of its own. A format that is a drawing handle
/// (a bitmap, a metafile, a palette) is not copied; Windows makes the device-independent bitmap (the format a picture is normally put on the clipboard
/// in) from it anyway, and that is copied. If the clipboard holds more than 128 MB it is not saved at all, and the caller is told, so that nothing
/// is lost without a word.
/// </summary>
[SupportedOSPlatform("windows")]
sealed class WinClipboard : IClipboard
{
    const uint UnicodeText = 13;
    const uint MovableMemory = 0x0002;
    const long MaxTotal = 128L * 1024 * 1024;
    const int OpenTries = 10;
    static readonly Lazy<uint> ExcludeFromMonitor = new(() => RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing"));
    static readonly Lazy<uint> CanIncludeInHistory = new(() => RegisterClipboardFormat("CanIncludeInClipboardHistory"));
    static readonly Lazy<uint> CanUploadToCloud = new(() => RegisterClipboardFormat("CanUploadToCloudClipboard"));
    IntPtr _owner;

    public long SequenceNumber => GetClipboardSequenceNumber();

    public ClipboardSnapshot Save() => OnUi(() => WithClipboard(() =>
    {
        var items = new List<ClipboardItem>();
        long total = 0;
        for (var format = EnumClipboardFormats(0); format != 0; format = EnumClipboardFormats(format))
        {
            if (!CopiesAsMemory(format)) continue;
            var handle = GetClipboardData(format);
            if (handle == IntPtr.Zero) continue;                       // a format that cannot be rendered now
            var size = (long)GlobalSize(handle);
            if (size <= 0) continue;
            total += size;
            if (total > MaxTotal) throw new ClipboardException("the clipboard holds more than 128 MB, too much to put back afterwards");
            var pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero) continue;
            try
            {
                var bytes = new byte[size];
                Marshal.Copy(pointer, bytes, 0, bytes.Length);
                items.Add(new ClipboardItem(format, bytes));
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }

        return new ClipboardSnapshot(items);
    }));

    public void Restore(ClipboardSnapshot snapshot) => OnUi(() => WithClipboard(() =>
    {
        EmptyClipboard();
        foreach (var item in snapshot.Items) Put(item.Format, item.Data);
        return 0;
    }));

    public string? GetText() => OnUi(() => WithClipboard(() =>
    {
        var handle = GetClipboardData(UnicodeText);
        if (handle == IntPtr.Zero) return null;
        var pointer = GlobalLock(handle);
        if (pointer == IntPtr.Zero) return null;
        try
        {
            return Marshal.PtrToStringUni(pointer);
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }));

    public void SetText(string text, bool keepOut) => OnUi(() => WithClipboard(() =>
    {
        EmptyClipboard();
        Put(UnicodeText, Encoding.Unicode.GetBytes(text + "\0"));
        if (keepOut)
        {
            // Windows' own flags for "not for the clipboard history, not for the cloud, not for clipboard monitors"
            Put(ExcludeFromMonitor.Value, [1, 0, 0, 0]);
            Put(CanIncludeInHistory.Value, [0, 0, 0, 0]);
            Put(CanUploadToCloud.Value, [0, 0, 0, 0]);
        }

        return 0;
    }));

    static bool CopiesAsMemory(uint format) =>
        format is not (2 or 3 or 9 or 14 or 0x80 or 0x82 or 0x83 or 0x8E)                        // bitmap, metafile, palette, enhanced metafile, owner-display and their display forms
        && !(format is >= 0x200 and <= 0x3FF);                                                    // private and GDI-object ranges: handles, not memory

    /// <summary>Puts one format on the (open) clipboard; Windows owns the memory once it has taken it.</summary>
    static void Put(uint format, byte[] data)
    {
        var memory = GlobalAlloc(MovableMemory, (UIntPtr)data.Length);
        if (memory == IntPtr.Zero) throw new ClipboardException("out of memory");
        var pointer = GlobalLock(memory);
        Marshal.Copy(data, 0, pointer, data.Length);
        GlobalUnlock(memory);
        if (SetClipboardData(format, memory) == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            GlobalFree(memory);
            throw new ClipboardException("the clipboard would not take the data (error " + error + ")");
        }
    }

    T WithClipboard<T>(Func<T> work)
    {
        if (_owner == IntPtr.Zero) _owner = CreateWindowEx(0, "STATIC", "BYOA clipboard", 0, 0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        var opened = false;
        for (var i = 0; i < OpenTries && !(opened = OpenClipboard(_owner)); i++) Thread.Sleep(15);
        if (!opened) throw new ClipboardException("another program holds the clipboard");
        try
        {
            return work();
        }
        finally
        {
            CloseClipboard();
        }
    }

    static T OnUi<T>(Func<T> work) => Dispatcher.UIThread.CheckAccess() ? work() : Dispatcher.UIThread.Invoke(work);

    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool EmptyClipboard();
    [DllImport("user32.dll")] static extern uint EnumClipboardFormats(uint format);
    [DllImport("user32.dll")] static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetClipboardData(uint format, IntPtr memory);
    [DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterClipboardFormat(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalFree(IntPtr memory);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GlobalUnlock(IntPtr memory);
    [DllImport("kernel32.dll")] static extern UIntPtr GlobalSize(IntPtr memory);
}
