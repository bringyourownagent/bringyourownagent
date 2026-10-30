using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Byoa.Wallet.Secrets;

/// <summary>The Keychain could not be read or written (locked, no access from this session, an item the app may not use).</summary>
public sealed class KeychainException(int status, string message) : IOException(message)
{
    /// <summary>The OSStatus of Security.framework.</summary>
    public int Status { get; } = status;
}

/// <summary>The OSStatus values of Security.framework that matter here.</summary>
public static class KeychainStatus
{
    public const int Success = 0;
    public const int AuthFailed = -25293;
    public const int NoSuchKeychain = -25294;
    public const int DuplicateItem = -25299;
    public const int ItemNotFound = -25300;
    public const int InteractionNotAllowed = -25308;
}

/// <summary>One secret value the wallet keeps in a place of its own (a Keychain item, a Secret Service item, a key file).</summary>
public interface ISecretKeyItem
{
    /// <summary>The stored bytes; null when there is none. Throws <see cref="IOException"/> when the store cannot be read.</summary>
    byte[]? Get();

    /// <summary>Stores the bytes, replacing an earlier value. Never deletes anything.</summary>
    void Set(byte[] value);
}

/// <summary>One secret value of the macOS Keychain.</summary>
public interface IKeychainItem : ISecretKeyItem
{
}

/// <summary>
/// One generic-password item of the user's Keychain, identified by (service, account). With a keychain file path every call is confined
/// to that file (the tests use their own, so the login keychain of the person who runs them is never touched). There is deliberately
/// no delete: the wallet only adds or replaces.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacKeychainItem : IKeychainItem
{
    readonly string _service;
    readonly string _account;
    readonly string _label;
    readonly string? _keychainPath;
    readonly object _gate = new();
    nint _keychain;

    public MacKeychainItem(string service, string account, string label, string? keychainPath = null)
    {
        MacLibs.Require();
        _service = service;
        _account = account;
        _label = label;
        _keychainPath = keychainPath;
    }

    public byte[]? Get()
    {
        var status = Copy(out var data);
        if (status == KeychainStatus.ItemNotFound) return null;
        if (status != KeychainStatus.Success) throw Fail("could not read the key from the Keychain", status);
        return data;
    }

    public void Set(byte[] value)
    {
        var status = Update(value);
        if (status == KeychainStatus.ItemNotFound) status = Add(value);
        if (status != KeychainStatus.Success) throw Fail("could not save the key in the Keychain", status);
    }

    int Add(byte[] data)
    {
        using var scope = new CfScope();
        var keychain = OpenKeychain(out var openStatus);
        if (openStatus != KeychainStatus.Success) return openStatus;
        var attributes = scope.NewDictionary();
        CF.CFDictionarySetValue(attributes, Sec.kSecClass, Sec.kSecClassGenericPassword);
        CF.CFDictionarySetValue(attributes, Sec.kSecAttrService, scope.NewString(_service));
        CF.CFDictionarySetValue(attributes, Sec.kSecAttrAccount, scope.NewString(_account));
        CF.CFDictionarySetValue(attributes, Sec.kSecAttrLabel, scope.NewString(_label));
        var value = scope.NewData(data);
        CF.CFDictionarySetValue(attributes, Sec.kSecValueData, value);
        if (keychain != 0) CF.CFDictionarySetValue(attributes, Sec.kSecUseKeychain, keychain);
        try
        {
            return Sec.SecItemAdd(attributes, 0);
        }
        finally
        {
            CfScope.ZeroData(value);
        }
    }

    int Update(byte[] data)
    {
        using var scope = new CfScope();
        var query = Query(scope, out var status);
        if (status != KeychainStatus.Success) return status;
        var changes = scope.NewDictionary();
        var value = scope.NewData(data);
        CF.CFDictionarySetValue(changes, Sec.kSecValueData, value);
        try
        {
            return Sec.SecItemUpdate(query, changes);
        }
        finally
        {
            CfScope.ZeroData(value);
        }
    }

    int Copy(out byte[]? data)
    {
        data = null;
        using var scope = new CfScope();
        var query = Query(scope, out var status);
        if (status != KeychainStatus.Success) return status;
        CF.CFDictionarySetValue(query, Sec.kSecReturnData, CF.True);
        CF.CFDictionarySetValue(query, Sec.kSecMatchLimit, Sec.kSecMatchLimitOne);
        status = Sec.SecItemCopyMatching(query, out var result);
        if (status != KeychainStatus.Success) return status;
        scope.Own(result);
        if (result == 0 || CF.CFGetTypeID(result) != CF.CFDataGetTypeID()) return -50;   // errSecParam
        var length = (int)CF.CFDataGetLength(result);
        var copy = new byte[length];
        if (length > 0) Marshal.Copy(CF.CFDataGetBytePtr(result), copy, 0, length);
        data = copy;
        return KeychainStatus.Success;
    }

    nint Query(CfScope scope, out int status)
    {
        var keychain = OpenKeychain(out status);
        var query = scope.NewDictionary();
        CF.CFDictionarySetValue(query, Sec.kSecClass, Sec.kSecClassGenericPassword);
        CF.CFDictionarySetValue(query, Sec.kSecAttrService, scope.NewString(_service));
        CF.CFDictionarySetValue(query, Sec.kSecAttrAccount, scope.NewString(_account));
        if (keychain != 0) CF.CFDictionarySetValue(query, Sec.kSecMatchSearchList, scope.NewArray(keychain));
        return query;
    }

    /// <summary>The confining keychain (opened once), or zero for the default one. A path that does not exist is an error.</summary>
    nint OpenKeychain(out int status)
    {
        status = KeychainStatus.Success;
        if (_keychainPath is null) return 0;
        lock (_gate)
        {
            if (_keychain != 0) return _keychain;
            // SecKeychainOpen succeeds for a path that does not exist and the first item call then fails in a confusing way
            if (!File.Exists(_keychainPath))
            {
                status = KeychainStatus.NoSuchKeychain;
                return 0;
            }

            status = Sec.SecKeychainOpen(Sec.CPath(_keychainPath), out _keychain);
            if (status != KeychainStatus.Success) _keychain = 0;
            return _keychain;
        }
    }

    /// <summary>Creates a keychain file of its own (for tests): an existing path is an error, nothing is overwritten.</summary>
    public static void CreateKeychainFile(string path, string password)
    {
        MacLibs.Require();
        if (File.Exists(path)) throw new IOException("the keychain file exists: " + path);
        var bytes = System.Text.Encoding.UTF8.GetBytes(password);
        var status = Sec.SecKeychainCreate(Sec.CPath(path), (uint)bytes.Length, bytes, false, 0, out var keychain);
        if (keychain != 0) CF.CFRelease(keychain);
        if (status != KeychainStatus.Success) throw Fail("could not create the keychain file", status);
    }

    static KeychainException Fail(string what, int status)
    {
        var text = Sec.Describe(status);
        return new KeychainException(status, $"{what} (code {status}{(text.Length > 0 ? ": " + text : "")})");
    }
}
