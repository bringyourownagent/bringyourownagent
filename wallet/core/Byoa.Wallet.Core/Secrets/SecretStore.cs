using System.Security.Cryptography;
using System.Text.Json;

namespace Byoa.Wallet.Secrets;

/// <summary>
/// API keys, sealed for the current user (<c>secrets.dat</c> in the data folder): with Windows DPAPI, or on macOS under a key kept in
/// the user's Keychain (see <see cref="SecretProtectors"/>). The file never holds plain text; keys are decrypted only for the moment a
/// request needs them, and are never logged, shown or sent anywhere except to their own provider.
/// </summary>
public sealed class SecretStore
{
    readonly string _path;
    readonly ISecretProtector _protector;
    readonly object _gate = new();

    /// <param name="protector">How the file is sealed; the protector of this operating system when omitted (tests give their own).</param>
    public SecretStore(string path, ISecretProtector? protector = null)
    {
        _path = path;
        _protector = protector ?? SecretProtectors.CreateDefault();
    }

    public event Action? Changed;

    public bool Has(string name) => Get(name) is { Length: > 0 };

    /// <summary>The secret, or null. Callers keep it only as long as one request needs it.</summary>
    public string? Get(string name)
    {
        lock (_gate)
        {
            return Load().TryGetValue(name, out var value) ? value : null;
        }
    }

    public void Set(string name, string value)
    {
        value = value.Trim();
        if (value.Length == 0) throw new ArgumentException("empty secret", nameof(value));
        lock (_gate)
        {
            var all = Load();
            all[name] = value;
            Save(all);
        }

        Changed?.Invoke();
    }

    public void Remove(string name)
    {
        lock (_gate)
        {
            var all = Load();
            if (!all.Remove(name)) return;
            Save(all);
        }

        Changed?.Invoke();
    }

    /// <summary>Reads a key from a text file (its first non-empty line) and stores it. Returns false if there was none.</summary>
    public bool ImportFromFile(string name, string file)
    {
        var line = File.ReadLines(file).Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        if (line is null) return false;
        Set(name, line);
        return true;
    }

    /// <summary>
    /// True while the file exists but cannot be read: it was made by another account, or its key is gone, or it is damaged. The wallet then
    /// works as if it held no keys (free local models need none), and the first key the visitor enters keeps the unreadable
    /// file next to the new one (<c>secrets.dat.unreadable-&lt;time&gt;</c>) instead of writing over it.
    /// </summary>
    public bool Unreadable { get; private set; }

    Dictionary<string, string> Load()
    {
        if (!File.Exists(_path))
        {
            Unreadable = false;
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        try
        {
            var plain = _protector.Unprotect(File.ReadAllBytes(_path));
            try
            {
                var all = JsonSerializer.Deserialize<Dictionary<string, string>>(plain) ?? new(StringComparer.Ordinal);
                Unreadable = false;
                return all;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
            }
        }
        catch (Exception e) when (e is CryptographicException or JsonException)
        {
            // A key file the wallet cannot read must not take the free local use down with it: no keys, not a crash.
            Unreadable = true;
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    void Save(Dictionary<string, string> all)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        if (Unreadable && File.Exists(_path))
        {
            // Keep what cannot be read: another account's file, or a damaged one that a repair tool might still open.
            File.Copy(_path, _path + ".unreadable-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"), overwrite: false);
        }

        var plain = JsonSerializer.SerializeToUtf8Bytes(all);
        try
        {
            var sealedBytes = _protector.Protect(plain);
            var temp = _path + ".tmp";
            File.WriteAllBytes(temp, sealedBytes);
            File.Move(temp, _path, overwrite: true);
            Unreadable = false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }
}
