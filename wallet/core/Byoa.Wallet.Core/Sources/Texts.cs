using System.Text;
using System.Text.RegularExpressions;

namespace Byoa.Wallet.Sources;

/// <summary>
/// Shared hygiene for text.generate answers: hidden reasoning is never shown to a page, and a "choices" answer
/// is matched against the page's list no matter how the model dressed it up. The wallet's text sources and the
/// engine both use this, so a fake or a real model passes through the same rules.
/// </summary>
public static partial class Texts
{
    // <think>…</think>, and a <think> that was never closed (everything after it is reasoning too).
    [GeneratedRegex(@"<think>[\s\S]*?</think>|<think>[\s\S]*$", RegexOptions.IgnoreCase)]
    private static partial Regex Think();

    [GeneratedRegex(@"<reasoning>[\s\S]*?</reasoning>|<reasoning>[\s\S]*$", RegexOptions.IgnoreCase)]
    private static partial Regex Reasoning();

    [GeneratedRegex(@"<\|channel\|>(?:thought|analysis|reasoning)(?:<\|message\|>)?[\s\S]*?(?=<\|channel\|>(?:final|answer)(?:<\|message\|>)?|$)", RegexOptions.IgnoreCase)]
    private static partial Regex HiddenChannel();

    [GeneratedRegex(@"^\s*(?:thinking|reasoning|thought process)\s*:?[^\r\n]*(?:\r?\n|$)[\s\S]*?\r?\n\s*(?:answer|final)\s*:\s*", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingReasoning();

    [GeneratedRegex(@"^\s*(?:thinking|reasoning|thought process)\s*:?[^\r\n]*(?:\r?\n|$)[\s\S]*?\r?\n\s*final\s+answer\s*:\s*", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingFinalAnswerReasoning();

    /// <summary>The answer only: reasoning tags removed, trimmed. Never throws.</summary>
    public static string CleanAnswer(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var cleaned = Think().Replace(text, "");
        cleaned = Reasoning().Replace(cleaned, "");
        cleaned = HiddenChannel().Replace(cleaned, "");
        cleaned = LeadingFinalAnswerReasoning().Replace(cleaned, "");
        cleaned = LeadingReasoning().Replace(cleaned, "");
        cleaned = cleaned.Replace("<|channel|>final", "", StringComparison.OrdinalIgnoreCase)
            .Replace("<|channel|>answer", "", StringComparison.OrdinalIgnoreCase)
            .Replace("<|message|>", "", StringComparison.OrdinalIgnoreCase);
        return cleaned.Trim();
    }

    /// <summary>
    /// The instruction a source adds to the prompt when it cannot constrain the answer with a JSON schema, and the
    /// engine counts with the same text: the gate's <c>textChars</c> covers everything the wallet sends the provider.
    /// </summary>
    public static string ChoicesInstruction(IReadOnlyList<string> choices) =>
        "Answer with exactly one of the following and nothing else: " +
        string.Join(" | ", choices.Select(c => "\"" + c + "\"")) + ".";

    /// <summary>
    /// Matches an answer against the page's choices: trimmed, quotes and trailing punctuation off, case-insensitive,
    /// inner whitespace folded. Returns the choice as the page listed it, or null when nothing matches.
    /// </summary>
    public static string? MatchChoice(string answer, IReadOnlyList<string> choices)
    {
        var plain = NormalizeChoice(answer);
        foreach (var choice in choices)
        {
            if (NormalizeChoice(choice) == plain) return choice;
        }

        return null;
    }

    static string NormalizeChoice(string text)
    {
        var t = CleanAnswer(text);
        // Off with the dressing a model likes to add: quotes around the whole answer, and politeness at the end.
        // Repeated until it settles: `"yes".` needs two passes.
        string before;
        do
        {
            before = t;
            t = t.Trim().Trim('"', '\'', '“', '”', '«', '»').TrimEnd('.', '!', '?', ',', ';', ':', '。', '！', '？');
        }
        while (t != before);
        var builder = new StringBuilder(t.Length);
        var space = false;
        foreach (var c in t)
        {
            if (char.IsWhiteSpace(c))
            {
                space = true;
                continue;
            }

            if (space && builder.Length > 0) builder.Append(' ');
            space = false;
            builder.Append(char.ToLowerInvariant(c));         // compared without case: "YES" is the page's "yes"
        }

        return builder.ToString();
    }
}
