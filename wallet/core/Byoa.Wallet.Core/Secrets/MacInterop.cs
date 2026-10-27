using System.Runtime.InteropServices;
using System.Text;

namespace Byoa.Wallet.Secrets;

/// <summary>
/// The macOS system libraries the secret store calls (CoreFoundation and Security). The declarations are resolved by the runtime at the
/// first call, so the assembly loads and compiles on any OS; <see cref="Require"/> turns a call on another OS into a clear exception.
/// </summary>
internal static class MacLibs
{
    public const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    public const string Security = "/System/Library/Frameworks/Security.framework/Security";

    public static void Require()
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("This code calls macOS system libraries and runs on macOS only.");
    }

    static readonly Dictionary<string, nint> Handles = new(StringComparer.Ordinal);

    static nint Handle(string library)
    {
        lock (Handles)
        {
            if (!Handles.TryGetValue(library, out var handle)) Handles[library] = handle = NativeLibrary.Load(library);
            return handle;
        }
    }

    /// <summary>The address of an exported symbol (for a struct such as <c>kCFTypeDictionaryKeyCallBacks</c>).</summary>
    public static nint Export(string library, string symbol) => NativeLibrary.GetExport(Handle(library), symbol);

    /// <summary>The value of an exported constant such as <c>extern const CFStringRef kSecClass</c>: the symbol holds the object pointer.</summary>
    public static nint Constant(string library, string symbol) => Marshal.ReadIntPtr(Export(library, symbol));
}

/// <summary>The few CoreFoundation calls the Keychain code needs.</summary>
internal static class CF
{
    public const uint Utf8 = 0x08000100;

    [DllImport(MacLibs.CoreFoundation)] public static extern void CFRelease(nint cf);
    [DllImport(MacLibs.CoreFoundation)] public static extern nuint CFGetTypeID(nint cf);
    [DllImport(MacLibs.CoreFoundation)] public static extern nuint CFDataGetTypeID();

    [DllImport(MacLibs.CoreFoundation)]
    public static extern nint CFStringCreateWithBytes(nint allocator, byte[] bytes, nint numBytes, uint encoding,
        [MarshalAs(UnmanagedType.U1)] bool isExternalRepresentation);

    [DllImport(MacLibs.CoreFoundation)] public static extern nint CFStringGetLength(nint theString);
    [DllImport(MacLibs.CoreFoundation)] public static extern nint CFStringGetMaximumSizeForEncoding(nint length, uint encoding);

    [DllImport(MacLibs.CoreFoundation)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool CFStringGetCString(nint theString, byte[] buffer, nint bufferSize, uint encoding);

    [DllImport(MacLibs.CoreFoundation)]
    public static extern nint CFDictionaryCreateMutable(nint allocator, nint capacity, nint keyCallBacks, nint valueCallBacks);

    [DllImport(MacLibs.CoreFoundation)] public static extern void CFDictionarySetValue(nint dictionary, nint key, nint value);
    [DllImport(MacLibs.CoreFoundation)] public static extern nint CFArrayCreateMutable(nint allocator, nint capacity, nint callBacks);
    [DllImport(MacLibs.CoreFoundation)] public static extern void CFArrayAppendValue(nint array, nint value);
    [DllImport(MacLibs.CoreFoundation)] public static extern nint CFDataCreateMutable(nint allocator, nint capacity);
    [DllImport(MacLibs.CoreFoundation)] public static extern void CFDataAppendBytes(nint data, byte[] bytes, nint length);
    [DllImport(MacLibs.CoreFoundation)] public static extern nint CFDataGetLength(nint data);
    [DllImport(MacLibs.CoreFoundation)] public static extern nint CFDataGetBytePtr(nint data);
    [DllImport(MacLibs.CoreFoundation)] public static extern nint CFDataGetMutableBytePtr(nint data);

    public static nint True => MacLibs.Constant(MacLibs.CoreFoundation, "kCFBooleanTrue");
    public static nint DictionaryKeyCallbacks => MacLibs.Export(MacLibs.CoreFoundation, "kCFTypeDictionaryKeyCallBacks");
    public static nint DictionaryValueCallbacks => MacLibs.Export(MacLibs.CoreFoundation, "kCFTypeDictionaryValueCallBacks");
    public static nint ArrayCallbacks => MacLibs.Export(MacLibs.CoreFoundation, "kCFTypeArrayCallBacks");

    /// <summary>A new CFString (the caller releases it) from a non-empty managed string.</summary>
    public static nint CreateString(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        var bytes = Encoding.UTF8.GetBytes(value);
        var result = CFStringCreateWithBytes(0, bytes, bytes.Length, Utf8, false);
        return result != 0 ? result : throw new InvalidOperationException("CoreFoundation could not create a string");
    }

    /// <summary>A managed copy of a CFString; null for a null reference or an unconvertible string.</summary>
    public static string? ToManaged(nint cfString)
    {
        if (cfString == 0) return null;
        var size = CFStringGetMaximumSizeForEncoding(CFStringGetLength(cfString), Utf8) + 1;
        var buffer = new byte[size];
        if (!CFStringGetCString(cfString, buffer, size, Utf8)) return null;
        var end = Array.IndexOf(buffer, (byte)0);
        return Encoding.UTF8.GetString(buffer, 0, end < 0 ? buffer.Length : end);
    }
}

/// <summary>Owns the CoreFoundation objects created for one native call and releases them together.</summary>
internal sealed class CfScope : IDisposable
{
    readonly List<nint> _owned = [];

    /// <summary>Takes ownership of an object that was returned by a Create/Copy function.</summary>
    public nint Own(nint cf)
    {
        if (cf != 0) _owned.Add(cf);
        return cf;
    }

    public nint NewString(string value) => Own(CF.CreateString(value));

    public nint NewDictionary() => Own(CF.CFDictionaryCreateMutable(0, 0, CF.DictionaryKeyCallbacks, CF.DictionaryValueCallbacks));

    public nint NewArray(params nint[] values)
    {
        var array = Own(CF.CFArrayCreateMutable(0, 0, CF.ArrayCallbacks));
        foreach (var value in values) CF.CFArrayAppendValue(array, value);
        return array;
    }

    /// <summary>A mutable CFData holding a copy of <paramref name="bytes"/>; <see cref="ZeroData"/> wipes it before it is released.</summary>
    public nint NewData(byte[] bytes)
    {
        var data = Own(CF.CFDataCreateMutable(0, bytes.Length));
        CF.CFDataAppendBytes(data, bytes, bytes.Length);
        return data;
    }

    /// <summary>Overwrites the bytes of a mutable CFData made by <see cref="NewData"/> (a secret does not stay in freed memory).</summary>
    public static void ZeroData(nint mutableData)
    {
        var length = CF.CFDataGetLength(mutableData);
        if (length <= 0) return;
        var pointer = CF.CFDataGetMutableBytePtr(mutableData);
        if (pointer != 0) Marshal.Copy(new byte[length], 0, pointer, (int)length);
    }

    public void Dispose()
    {
        for (var i = _owned.Count - 1; i >= 0; i--) CF.CFRelease(_owned[i]);
        _owned.Clear();
    }
}

/// <summary>Security.framework: generic-password Keychain items, and the file keychains the tests use.</summary>
internal static class Sec
{
    [DllImport(MacLibs.Security)] public static extern int SecItemAdd(nint attributes, nint result);
    [DllImport(MacLibs.Security)] public static extern int SecItemCopyMatching(nint query, out nint result);
    [DllImport(MacLibs.Security)] public static extern int SecItemUpdate(nint query, nint attributesToUpdate);
    [DllImport(MacLibs.Security)] public static extern int SecKeychainOpen(byte[] pathName, out nint keychain);

    [DllImport(MacLibs.Security)]
    public static extern int SecKeychainCreate(byte[] pathName, uint passwordLength, byte[]? password,
        [MarshalAs(UnmanagedType.U1)] bool promptUser, nint initialAccess, out nint keychain);

    [DllImport(MacLibs.Security)] public static extern nint SecCopyErrorMessageString(int status, nint reserved);

    static nint Const(string name) => MacLibs.Constant(MacLibs.Security, name);

    public static nint kSecClass => Const("kSecClass");
    public static nint kSecClassGenericPassword => Const("kSecClassGenericPassword");
    public static nint kSecAttrService => Const("kSecAttrService");
    public static nint kSecAttrAccount => Const("kSecAttrAccount");
    public static nint kSecAttrLabel => Const("kSecAttrLabel");
    public static nint kSecValueData => Const("kSecValueData");
    public static nint kSecReturnData => Const("kSecReturnData");
    public static nint kSecMatchLimit => Const("kSecMatchLimit");
    public static nint kSecMatchLimitOne => Const("kSecMatchLimitOne");
    public static nint kSecMatchSearchList => Const("kSecMatchSearchList");
    public static nint kSecUseKeychain => Const("kSecUseKeychain");

    /// <summary>The system's text for an OSStatus (empty when it has none).</summary>
    public static string Describe(int status)
    {
        try
        {
            var message = SecCopyErrorMessageString(status, 0);
            if (message == 0) return "";
            try { return CF.ToManaged(message) ?? ""; }
            finally { CF.CFRelease(message); }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException)
        {
            return "";
        }
    }

    /// <summary>A NUL-terminated UTF-8 path for the C functions that take <c>const char*</c>.</summary>
    public static byte[] CPath(string path) => Encoding.UTF8.GetBytes(path + "\0");
}
