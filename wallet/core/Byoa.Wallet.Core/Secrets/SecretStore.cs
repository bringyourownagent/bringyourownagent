using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Byoa.Wallet.Secrets;

/// <summary>
/// API keys, encrypted with Windows DPAPI for the current user (<c>secrets.dat</c> in the data folder).
/// The file never holds plain text; keys are decrypted only for the moment a request needs them, and are never
/// logged, shown or sent anywhere except to their own provider.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SecretStore
{
    // Extra entropy. It is a constant in this public source, so it is not a secret: DPAPI keeps the file from other accounts, but not from a program running as this user.
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("BYOA agent wallet secrets v1");

    readonly string _path;
    readonly object _gate = new();

    public SecretStore(string path) => _path = path;

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

    Dictionary<string, string> Load()
    {
        if (!File.Exists(_path)) return new Dictionary<string, string>(StringComparer.Ordinal);
        var plain = ProtectedData.Unprotect(File.ReadAllBytes(_path), Entropy, DataProtectionScope.CurrentUser);
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(plain) ?? new(StringComparer.Ordinal);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    void Save(Dictionary<string, string> all)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var plain = JsonSerializer.SerializeToUtf8Bytes(all);
        try
        {
            var sealedBytes = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            var temp = _path + ".tmp";
            File.WriteAllBytes(temp, sealedBytes);
            File.Move(temp, _path, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }
}
