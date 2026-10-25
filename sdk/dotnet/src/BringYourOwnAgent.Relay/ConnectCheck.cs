using System;
using System.Security.Cryptography;
using System.Text;

namespace BringYourOwnAgent.Relay;

/// <summary>
/// The connect check code of CONTRACT-DIRECT.md §3a: four characters that the page and the wallet both show, so the visitor can see
/// that the connection he started is the one his wallet is being asked to allow. A link can be sent to a visitor by somebody else; a
/// visitor who did not press Connect on that site a moment ago has no page showing the code, and declines.
/// </summary>
/// <remarks>
/// The code is derived from data already in the link — the site computes it from the session it issued, the wallet from the session
/// the link carries — so no traffic is added, and nothing is fetched before Allow. It proves the visitor started this connection; it
/// is not a second secret: the session it is derived from is itself public in the link.
/// </remarks>
public static class ConnectCheck
{
    /// <summary>The alphabet the code is written in: no <c>I</c>, <c>L</c>, <c>O</c>, <c>0</c> or <c>1</c>, which are told apart badly by eye.</summary>
    public const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    /// <summary>The prefix of the string that is hashed, so this use of SHA-256 cannot be confused with another.</summary>
    const string Prefix = "BYOA-CHECK-1:";

    /// <summary>
    /// The code of one session: <c>d = SHA-256(UTF-8("BYOA-CHECK-1:" + session))</c>, character <c>i</c> (0 to 3) is
    /// <c>Alphabet[d[i] mod 31]</c>. Always four characters, in capitals.
    /// </summary>
    /// <param name="session">The one-time session code the site put in the link, exactly as it issued it.</param>
    public static string Compute(string session)
    {
        if (session is null) throw new ArgumentNullException(nameof(session));
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(Prefix + session));
        Span<char> code = stackalloc char[4];
        for (var i = 0; i < code.Length; i++) code[i] = Alphabet[digest[i] % Alphabet.Length];
        return new string(code);
    }
}