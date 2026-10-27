using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Byoa.Wallet.Secrets;

/// <summary>Seals the secrets file's bytes for the current user and opens them again. A failure to open is a <see cref="CryptographicException"/>.</summary>
public interface ISecretProtector
{
    byte[] Protect(byte[] plain);

    /// <exception cref="CryptographicException">The bytes were sealed for another user, or are damaged, or the key is not available.</exception>
    byte[] Unprotect(byte[] sealedBytes);
}

public static class SecretProtectors
{
    /// <summary>The unit tests (only they see it) give a protector that never touches the Keychain of the person who runs them.</summary>
    internal static Func<string, ISecretProtector>? TestFactory { get; set; }

    /// <summary>
    /// The protector of this operating system: Windows DPAPI, or a key kept in the macOS Keychain. <paramref name="purpose"/> ("secrets", "pairings")
    /// is mixed in, so that a file made for one purpose does not open as the other.
    /// </summary>
    public static ISecretProtector CreateDefault(string purpose = "secrets")
    {
        if (TestFactory is { } factory) return factory(purpose);
        if (OperatingSystem.IsWindows()) return new DpapiSecretProtector(Encoding.UTF8.GetBytes("BYOA agent wallet " + purpose + " v1"));
        if (OperatingSystem.IsMacOS())
            return new KeychainSecretProtector(new MacKeychainItem(KeychainSecretProtector.Service, KeychainSecretProtector.Account, KeychainSecretProtector.Label), purpose);
        throw new PlatformNotSupportedException("The wallet keeps keys with Windows DPAPI or the macOS Keychain.");
    }
}

/// <summary>Windows DPAPI for the current user.</summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretProtector(byte[] entropy) : ISecretProtector
{
    // The entropy is a constant in this public source, so it is not a secret: DPAPI keeps the file from other accounts, but not from a program running as this user.
    public byte[] Protect(byte[] plain) => ProtectedData.Protect(plain, entropy, DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] sealedBytes) => ProtectedData.Unprotect(sealedBytes, entropy, DataProtectionScope.CurrentUser);
}

/// <summary>
/// macOS: the secrets file is sealed with AES-256-GCM under a random key that lives in the user's Keychain (one generic-password item), so
/// the file alone opens nothing, and another account on this Mac cannot read either. The file is: "BYOAKC1\0", a 12-byte nonce, a 16-byte tag,
/// the ciphertext.
/// </summary>
public sealed class KeychainSecretProtector(IKeychainItem keyItem, string purpose = "secrets") : ISecretProtector
{
    public const string Service = "org.bringyourownagent.wallet";
    public const string Account = "secrets-key";
    public const string Label = "BYOA agent wallet: key of the secrets file";

    static readonly byte[] Magic = Encoding.ASCII.GetBytes("BYOAKC1\0");

    /// <summary>What the tag covers besides the data: the format and the purpose of the file.</summary>
    byte[] Associated => [.. Magic, .. Encoding.UTF8.GetBytes(purpose)];
    const int NonceSize = 12;
    const int TagSize = 16;
    const int KeySize = 32;

    public byte[] Protect(byte[] plain)
    {
        var key = KeyOrNew();
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(NonceSize);
            var cipher = new byte[plain.Length];
            var tag = new byte[TagSize];
            using (var aes = new AesGcm(key, TagSize)) aes.Encrypt(nonce, plain, cipher, tag, Associated);
            var result = new byte[Magic.Length + NonceSize + TagSize + cipher.Length];
            Magic.CopyTo(result, 0);
            nonce.CopyTo(result, Magic.Length);
            tag.CopyTo(result, Magic.Length + NonceSize);
            cipher.CopyTo(result, Magic.Length + NonceSize + TagSize);
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public byte[] Unprotect(byte[] sealedBytes)
    {
        if (sealedBytes.Length < Magic.Length + NonceSize + TagSize || !sealedBytes.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new CryptographicException("the secrets file is not in the format of this wallet");
        byte[]? key;
        try
        {
            key = keyItem.Get();
        }
        catch (IOException e)
        {
            throw new CryptographicException("the Keychain did not give the key of the secrets file: " + e.Message, e);
        }

        if (key is null || key.Length != KeySize) throw new CryptographicException("the Keychain has no key for the secrets file");
        try
        {
            var nonce = sealedBytes.AsSpan(Magic.Length, NonceSize);
            var tag = sealedBytes.AsSpan(Magic.Length + NonceSize, TagSize);
            var cipher = sealedBytes.AsSpan(Magic.Length + NonceSize + TagSize);
            var plain = new byte[cipher.Length];
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, cipher, tag, plain, Associated);
            return plain;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>The key, or a new random one stored first. When the Keychain cannot be written the error goes up: a key that is not kept would make the file unreadable.</summary>
    byte[] KeyOrNew()
    {
        byte[]? key;
        try
        {
            key = keyItem.Get();
        }
        catch (IOException e)
        {
            throw new CryptographicException("the Keychain did not give the key of the secrets file: " + e.Message, e);
        }

        if (key is { Length: KeySize }) return key;
        key = RandomNumberGenerator.GetBytes(KeySize);
        try
        {
            keyItem.Set(key);
        }
        catch (IOException e)
        {
            CryptographicOperations.ZeroMemory(key);
            throw new CryptographicException("the Keychain would not keep the key of the secrets file: " + e.Message, e);
        }

        return key;
    }
}
