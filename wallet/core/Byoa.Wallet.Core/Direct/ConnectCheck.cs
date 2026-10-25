using System.Security.Cryptography;
using System.Text;

namespace Byoa.Wallet.Direct;

/// <summary>
/// The connect check code of CONTRACT-DIRECT.md section 3a: four characters that the visitor compares between the page where he pressed Connect
/// and the consent window, so that a link made for somebody else's page cannot pair his wallet with that stranger's session unnoticed.
/// </summary>
public static class ConnectCheck
{
    /// <summary>No I, L, O, 0 or 1: they are read wrongly.</summary>
    const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    /// <summary>The code of a link's <c>session</c>: character i is <c>Alphabet[SHA-256("BYOA-CHECK-1:" + session)[i] mod 31]</c>.</summary>
    public static string Compute(string session)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes("BYOA-CHECK-1:" + session));
        return string.Create(4, digest, static (chars, bytes) =>
        {
            for (var i = 0; i < chars.Length; i++) chars[i] = Alphabet[bytes[i] % Alphabet.Length];
        });
    }
}
