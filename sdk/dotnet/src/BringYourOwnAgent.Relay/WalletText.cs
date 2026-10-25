using System;
using System.Globalization;
using System.Text;

namespace BringYourOwnAgent.Relay;

/// <summary>
/// The wallet's answer is the visitor's input, exactly like text a visitor types (CONTRACT.md §3.2, CONTRACT-DIRECT.md §5 «Trust»).
/// It is cleaned and cut on the server before it is ever handed to a page: no control characters, no invisible formatting characters
/// (bidi overrides, zero-width marks), no more than two new lines in a row, never more than the cap, never half of a two-part character.
/// A page must still show it with <c>textContent</c> and safe DOM building only: markup stays text.
/// </summary>
public static class WalletText
{
    /// <summary>Characters that must never reach a page from a model's answer.</summary>
    static bool Drop(char c) => c != '\t' && c != '\n' && (char.IsControl(c) || char.GetUnicodeCategory(c) is UnicodeCategory.Format);

    /// <summary>The text of an answer, with its lines kept, cut at <paramref name="max"/> characters.</summary>
    public static string CleanBlock(string? text, int max) => Cut(Keep(text, max, lines: true).Trim(), max);

    /// <summary>One line of text from a wallet — a model's name, its id, where it runs: no tabs, no new lines, no runs of spaces,
    /// cut at <paramref name="max"/> characters. A name lands in a card and in a chat bubble, so it must stay one plain line.</summary>
    public static string CleanLine(string? text, int max)
    {
        if (string.IsNullOrEmpty(text) || max <= 0) return "";
        var sb = new StringBuilder(Math.Min(text.Length, max));
        var space = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c) || char.GetUnicodeCategory(c) is UnicodeCategory.Format)
            {
                space = sb.Length > 0;
                continue;
            }
            if (space) { sb.Append(' '); space = false; }
            if (sb.Length >= max) break;
            sb.Append(c);
        }
        return Cut(sb.ToString().Trim(), max);
    }

    /// <summary>One streamed piece as the wallet sent it, without trimming: the pieces are glued together, so a piece that ends in a space
    /// must keep it. Only what must never reach a page is removed (<see cref="Drop"/>).</summary>
    public static string CleanPiece(string? text, int max) => Keep(text, max, lines: false);

    static string Keep(string? text, int max, bool lines)
    {
        if (string.IsNullOrEmpty(text) || max <= 0) return "";
        var sb = new StringBuilder(Math.Min(text.Length, max));
        var newlines = 0;
        for (var i = 0; i < text.Length && sb.Length < max; i++)
        {
            var c = text[i];
            if (c == '\r') { if (i + 1 < text.Length && text[i + 1] == '\n') continue; c = '\n'; }
            if (c == '\n')
            {
                if (lines && ++newlines > 2) continue;                 // never three new lines in a row
                sb.Append('\n');
                continue;
            }
            if (Drop(c)) continue;
            if (lines) newlines = 0;
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Cut at <paramref name="max"/> characters, never in the middle of a two-part character.</summary>
    static string Cut(string s, int max)
    {
        if (s.Length > max) s = s[..max];
        if (s.Length > 0 && char.IsHighSurrogate(s[^1])) s = s[..^1];
        return s;
    }
}