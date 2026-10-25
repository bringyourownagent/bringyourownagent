using System;
using System.Collections.Generic;
using System.Linq;

namespace BringYourOwnAgent.Relay.Tests;

/// <summary>
/// The connect check code (CONTRACT-DIRECT.md §3a). The vectors below were computed by a second, independent implementation
/// (a small Python script over <c>hashlib.sha256</c>), not by the code under test: they are the spec, spelled out.
/// </summary>
public class ConnectCheckTests
{
    /// <summary>session → the code the spec's rule gives for it, computed outside this library.</summary>
    public static IEnumerable<object[]> Vectors() =>
    [
        ["", "G7CP"],
        ["a", "G3Y6"],
        ["test-session-0001", "PW4A"],
        ["V8lRbzS0m6b-9A0vV8lRbzS0m6b-9A0v", "88UT"],                 // a 32-character URL-safe code: the shape a site issues
        ["kQ3Zr8Yt1pLd5Nw7Xc2Vb9Mj4Hf6Gs0A", "QZTP"],
        ["0123456789abcdefghijklmnopqrstuvwxyz", "X5N7"],
        ["SESSION-é中\U0001f600", "P546"],                   // not ASCII: the digest is over the UTF-8 bytes
    ];

    [Theory]
    [MemberData(nameof(Vectors))]
    public void TheCodeIsTheOneTheSpecComputes(string session, string expected) =>
        Assert.Equal(expected, ConnectCheck.Compute(session));

    [Fact]
    public void TheAlphabetIsThirtyOneCharactersWithoutTheOnesThatLookAlike()
    {
        Assert.Equal(31, ConnectCheck.Alphabet.Length);
        Assert.Equal(ConnectCheck.Alphabet.Length, ConnectCheck.Alphabet.Distinct().Count());
        foreach (var bad in "ILO01") Assert.DoesNotContain(bad, ConnectCheck.Alphabet);
        Assert.Equal("ABCDEFGHJKMNPQRSTUVWXYZ23456789", ConnectCheck.Alphabet);
    }

    [Fact]
    public void EveryCodeIsFourCharactersOfThatAlphabetAndFollowsTheSession()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var session in Enumerable.Range(0, 500).Select(i => DirectHub.NewSecret()))
        {
            var code = ConnectCheck.Compute(session);
            Assert.Equal(4, code.Length);
            Assert.All(code, c => Assert.Contains(c, ConnectCheck.Alphabet));
            seen.Add(code);
        }
        Assert.True(seen.Count > 400, $"{seen.Count} different codes out of 500 sessions: the code follows the session");
        Assert.NotEqual(ConnectCheck.Compute("Session"), ConnectCheck.Compute("session"));      // the session is compared as it is
    }

    [Fact]
    public void ANullSessionIsRefusedRatherThanGuessed() =>
        Assert.Throws<ArgumentNullException>(() => ConnectCheck.Compute(null!));
}