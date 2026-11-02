using System.Security.Cryptography;
using System.Text;
using Android.Security.Keystore;
using Byoa.Wallet.Secrets;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;

namespace Byoa.Wallet.Android;

/// <summary>
/// Seals the wallet's files (<c>secrets.dat</c>, <c>pairings.dat</c>) with AES-256-GCM under a key that was made inside the Android Keystore
/// and cannot be taken out of it: the app asks the Keystore to encrypt and decrypt, and never sees the key. There is one key for each purpose
/// ("secrets", "pairings"), and the purpose is also part of what the seal covers, so that a file of one purpose does not open as the other.
/// The file is: "BYOAAK1\0", a 12-byte nonce (the Keystore makes it), the ciphertext and its 16-byte tag.
/// A key that is gone (the data was copied to another phone, or the app was reinstalled) is an error the secret store handles: the file is
/// kept aside and the wallet starts without keys. A new key is made only when something is sealed, never when something is opened.
/// </summary>
public sealed class AndroidKeystoreProtector(string purpose) : ISecretProtector
{
    const string Provider = "AndroidKeyStore";
    const int NonceSize = 12;
    const int TagBits = 128;
    static readonly byte[] Magic = Encoding.ASCII.GetBytes("BYOAAK1\0");
    static readonly object Gate = new();

    string Alias => "org.bringyourownagent.wallet." + purpose + ".v1";

    /// <summary>What the tag covers besides the data: the format and the purpose of the file.</summary>
    byte[] Associated => [.. Magic, .. Encoding.UTF8.GetBytes(purpose)];

    /// <summary>Hands the core the protector of each purpose (see <see cref="SecretProtectors.UsePlatform"/>).</summary>
    public static void Install() => SecretProtectors.UsePlatform(SecretStorage.AndroidKeystore, p => new AndroidKeystoreProtector(p));

    public byte[] Protect(byte[] plain)
    {
        try
        {
            using var cipher = Cipher.GetInstance("AES/GCM/NoPadding")!;
            cipher.Init(Javax.Crypto.CipherMode.EncryptMode, KeyOrNew());
            cipher.UpdateAAD(Associated);
            var body = cipher.DoFinal(plain)!;
            var nonce = cipher.GetIV()!;
            if (nonce.Length != NonceSize) throw new CryptographicException("the Keystore gave a nonce of an unexpected size");
            var result = new byte[Magic.Length + NonceSize + body.Length];
            Magic.CopyTo(result, 0);
            nonce.CopyTo(result, Magic.Length);
            body.CopyTo(result, Magic.Length + NonceSize);
            return result;
        }
        catch (Exception e) when (e is Java.Lang.Exception or GeneralSecurityException or InvalidOperationException)
        {
            throw new CryptographicException("the Android Keystore would not seal the data: " + e.GetType().Name, e);
        }
    }

    public byte[] Unprotect(byte[] sealedBytes)
    {
        if (sealedBytes.Length < Magic.Length + NonceSize + TagBits / 8 || !sealedBytes.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new CryptographicException("the file is not in the format of this wallet");
        try
        {
            var key = ExistingKey() ?? throw new CryptographicException("the Android Keystore has no key for this file");
            using var cipher = Cipher.GetInstance("AES/GCM/NoPadding")!;
            using var spec = new GCMParameterSpec(TagBits, sealedBytes, Magic.Length, NonceSize);
            cipher.Init(Javax.Crypto.CipherMode.DecryptMode, key, spec);
            cipher.UpdateAAD(Associated);
            return cipher.DoFinal(sealedBytes, Magic.Length + NonceSize, sealedBytes.Length - Magic.Length - NonceSize)!;
        }
        catch (Exception e) when (e is Java.Lang.Exception or GeneralSecurityException or InvalidOperationException)
        {
            // a damaged file, a file of another purpose or a key that is not the one it was sealed with all end here
            throw new CryptographicException("the Android Keystore would not open the data: " + e.GetType().Name, e);
        }
    }

    static KeyStore Store()
    {
        var store = KeyStore.GetInstance(Provider)!;
        store.Load(null);
        return store;
    }

    IKey? ExistingKey()
    {
        lock (Gate)
        {
            var store = Store();
            return store.ContainsAlias(Alias) ? store.GetKey(Alias, null) : null;
        }
    }

    /// <summary>The key of this purpose; a new one, made inside the Keystore, when there is none yet.</summary>
    IKey KeyOrNew()
    {
        lock (Gate)
        {
            if (ExistingKey() is { } key) return key;
            var spec = new KeyGenParameterSpec.Builder(Alias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
                .SetBlockModes(KeyProperties.BlockModeGcm)!
                .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)!
                .SetKeySize(256)!
                .SetRandomizedEncryptionRequired(true)!
                .Build()!;
            var generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, Provider)!;
            generator.Init(spec);
            return generator.GenerateKey()!;
        }
    }
}
