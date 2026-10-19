using System.Text;

namespace Byoa.Wallet.Sources;

/// <summary>
/// Takes a model's hidden reasoning out of an answer that arrives in pieces: <c>&lt;think&gt;…&lt;/think&gt;</c> and <c>&lt;reasoning&gt;…&lt;/reasoning&gt;</c>, even
/// when a tag is cut in two by the pieces. What it lets through is a preview for the site; the final answer is cleaned again as a whole
/// (<see cref="Texts.CleanAnswer"/>), which also knows the channel markers of other models.
/// </summary>
public sealed class ThinkFilter
{
    static readonly string[] Opens = ["<think>", "<reasoning>"];
    static readonly string[] Closes = ["</think>", "</reasoning>"];

    readonly StringBuilder _buffer = new();
    string? _closing;
    bool _started;

    /// <summary>One more piece of the answer; returns the part of what has arrived that is safe to show.</summary>
    public string Push(string piece)
    {
        _buffer.Append(piece);
        var visible = new StringBuilder();
        while (true)
        {
            var text = _buffer.ToString();
            if (_closing is null)
            {
                var at = Earliest(text, Opens, out var which);
                if (at >= 0)
                {
                    visible.Append(text, 0, at);
                    _buffer.Remove(0, at + Opens[which].Length);
                    _closing = Closes[which];
                    continue;
                }

                // A tail that could be the start of an opening tag waits for the next piece.
                var keep = TagPrefixAtEnd(text, Opens);
                visible.Append(text, 0, text.Length - keep);
                _buffer.Remove(0, text.Length - keep);
                break;
            }

            var close = text.IndexOf(_closing, StringComparison.OrdinalIgnoreCase);
            if (close >= 0)
            {
                _buffer.Remove(0, close + _closing.Length);
                _closing = null;
                continue;
            }

            _buffer.Remove(0, text.Length - TagPrefixAtEnd(text, [_closing]));
            break;
        }

        return Visible(visible.ToString());
    }

    /// <summary>The end of the answer: what was held back as a possible tag was text after all, unless the answer ended inside the reasoning.</summary>
    public string Flush()
    {
        var rest = _closing is null ? _buffer.ToString() : "";
        _buffer.Clear();
        return Visible(rest);
    }

    /// <summary>The answer starts at its first word: the blank lines a model leaves after its reasoning are not shown.</summary>
    string Visible(string text)
    {
        if (_started) return text;
        var trimmed = text.TrimStart();
        if (trimmed.Length > 0) _started = true;
        return trimmed;
    }

    static int Earliest(string text, string[] tags, out int which)
    {
        var best = -1;
        which = 0;
        for (var i = 0; i < tags.Length; i++)
        {
            var at = text.IndexOf(tags[i], StringComparison.OrdinalIgnoreCase);
            if (at >= 0 && (best < 0 || at < best))
            {
                best = at;
                which = i;
            }
        }

        return best;
    }

    static int TagPrefixAtEnd(string text, string[] tags)
    {
        var most = 0;
        foreach (var tag in tags)
        {
            for (var length = Math.Min(tag.Length - 1, text.Length); length > most; length--)
            {
                if (text.EndsWith(tag[..length], StringComparison.OrdinalIgnoreCase))
                {
                    most = length;
                    break;
                }
            }
        }

        return most;
    }
}
