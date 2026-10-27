using System.Security.Cryptography;
using System.Text.Json;
using Byoa.Wallet.Secrets;

namespace Byoa.Wallet.Direct;

/// <summary>
/// A site's pairing token (CONTRACT-DIRECT.md §4): with it the wallet reconnects to the site's endpoint later, after
/// a restart or a page reload, without a new link. Kept only for sites with "decide on my own".
/// </summary>
public sealed record Pairing(string Origin, string Endpoint, string Manifest, string Token, DateTimeOffset StoredAt);

/// <summary>
/// The stored pairings, one per origin, sealed for the current user like the keys (<c>pairings.dat</c> in the data
/// folder; see <see cref="SecretProtectors"/>): a token lets its holder use the visitor's AI on that site's server, so it is
/// kept like a key. With no path the store lives in memory only (tests).
/// </summary>
public sealed class PairingStore
{
    readonly string? _path;
    readonly ISecretProtector? _protector;
    readonly object _gate = new();
    List<Pairing>? _memory;

    /// <param name="protector">How the file is sealed; the protector of this operating system when omitted (tests give their own).</param>
    public PairingStore(string? path, ISecretProtector? protector = null)
    {
        _path = path;
        _protector = path is null ? null : protector ?? SecretProtectors.CreateDefault("pairings");
    }

    public event Action? Changed;

    public Pairing? Get(string origin)
    {
        lock (_gate) return Load().FirstOrDefault(p => p.Origin == origin);
    }

    public IReadOnlyList<Pairing> All()
    {
        lock (_gate) return Load().ToList();
    }

    /// <summary>Stores a pairing, replacing the origin's previous one.</summary>
    public void Set(Pairing pairing)
    {
        lock (_gate)
        {
            var all = Load();
            all.RemoveAll(p => p.Origin == pairing.Origin);
            all.Add(pairing);
            Save(all);
        }

        Changed?.Invoke();
    }

    /// <summary>Forgets the origin's pairing; with a token, only when it is still that one. Returns true if one was forgotten.</summary>
    public bool Forget(string origin, string? token = null)
    {
        lock (_gate)
        {
            var all = Load();
            if (all.RemoveAll(p => p.Origin == origin && (token is null || p.Token == token)) == 0) return false;
            Save(all);
        }

        Changed?.Invoke();
        return true;
    }

    List<Pairing> Load()
    {
        if (_path is null) return _memory ??= [];
        if (!File.Exists(_path)) return [];
        try
        {
            var plain = _protector!.Unprotect(File.ReadAllBytes(_path));
            try
            {
                return JsonSerializer.Deserialize<List<Pairing>>(plain) ?? [];
            }
            finally
            {
                Array.Clear(plain);
            }
        }
        catch (Exception e) when (e is CryptographicException or JsonException or IOException)
        {
            // A damaged file is the same as no pairings: the visitor follows a new link and the site pairs again.
            return [];
        }
    }

    void Save(List<Pairing> all)
    {
        if (_path is null)
        {
            _memory = all;
            return;
        }

        var plain = JsonSerializer.SerializeToUtf8Bytes(all);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllBytes(tmp, _protector!.Protect(plain));
            File.Move(tmp, _path, overwrite: true);
        }
        finally
        {
            Array.Clear(plain);
        }
    }
}
