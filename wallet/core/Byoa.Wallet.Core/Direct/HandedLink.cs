using System.Text;

namespace Byoa.Wallet.Direct;

/// <summary>
/// A link that reached the wallet by hand: the visitor pasted it, or it came from another device (CONTRACT-PAIR.md section 1). This
/// turns what he pasted into the one link it holds, tolerating what messengers and mail clients add: spaces and quotes around it,
/// angle brackets, a line wrapped inside it (line breaks, not spaces: a space means more text). It does not look inside the link;
/// <see cref="DirectLink.TryParse"/> does that.
/// </summary>
public static class HandedLink
{
    const string Scheme = DirectLink.Scheme + ":";

    /// <summary>Room for a link that a messenger wrapped into many short lines.</summary>
    const int MaxPasted = DirectLink.MaxLength * 2;

    /// <summary>CR, LF, next line, line separator, paragraph separator.</summary>
    static readonly char[] LineBreaks = [(char)0x0D, (char)0x0A, (char)0x85, (char)0x2028, (char)0x2029];

    /// <summary>Zero-width space, non-joiner, joiner, word joiner and byte order mark: some messengers put them into text (they are not white space, and a link has none).</summary>
    static readonly char[] ZeroWidth = [(char)0x200B, (char)0x200C, (char)0x200D, (char)0x2060, (char)0xFEFF];

    /// <summary>The link in the text, or false with a plain reason ("that is not a single link"). The reason is a sentence for the visitor.</summary>
    public static bool TryNormalize(string? text, out string link, out string why)
    {
        link = "";
        var t = text?.Trim() ?? "";
        if (t.Length == 0) return Fail("Nothing was pasted.", out why);
        if (t.Length > MaxPasted) return Fail("That text is too long to be a link.", out why);
        t = Unwrap(t).Trim();

        // A link has no white space. A line break inside it (with the spaces next to it) was put there on its way here, by a messenger or a
        // mail client; a space in the middle of a line is more text, and a link with other text around it is not one link.
        var joined = new StringBuilder(t.Length);
        foreach (var raw in t.Split(LineBreaks, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var c in raw.Trim())
            {
                if (char.IsWhiteSpace(c)) return Fail("That is not a single link. Paste only the one link the site gave you.", out why);
                if (Array.IndexOf(ZeroWidth, c) < 0) joined.Append(c);
            }
        }

        var one = joined.ToString();
        var first = one.IndexOf(Scheme, StringComparison.OrdinalIgnoreCase);
        if (first < 0) return Fail("That is not a link from a site. A site's link starts with " + DirectLink.Scheme + "://", out why);
        if (first > 0 || one.IndexOf(Scheme, first + 1, StringComparison.OrdinalIgnoreCase) >= 0) return Fail("That is not a single link. Paste only the one link the site gave you.", out why);
        link = one;
        why = "";
        return true;
    }

    /// <summary>One pair of quotes (straight or curly) or brackets around the whole text.</summary>
    static string Unwrap(string t)
    {
        if (t.Length < 2) return t;
        var pair = (t[0], t[^1]) is ('"', '"') or ('\'', '\'') or ('<', '>') or ('(', ')')
            or ((char)0x201C, (char)0x201D) or ((char)0x2018, (char)0x2019);
        return pair ? t[1..^1] : t;
    }

    static bool Fail(string reason, out string why)
    {
        why = reason;
        return false;
    }
}
