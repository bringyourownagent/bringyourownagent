using Byoa.Wallet.Engine;

namespace Byoa.Wallet.Settings;

/// <summary>What one site may use of the wallet for text.generate calls, once every layer has had its say.</summary>
/// <param name="InputChars">The most characters of messages one call may send.</param>
/// <param name="AnswerTokens">The most tokens one answer may have.</param>
/// <param name="CallSeconds">The longest the wallet waits for one call (the queue in front of a busy model included).</param>
/// <param name="ParallelCalls">The most calls of the site in flight at once.</param>
/// <param name="CallsPerMinute">The most calls of the site in a minute.</param>
public sealed record TextLimitSet(int InputChars, int AnswerTokens, int CallSeconds, int ParallelCalls, int CallsPerMinute);

/// <summary>
/// The three layers of the text limits. The visitor's defaults for every site (Limits page), the visitor's own value for one
/// site (its card in Sites), and what the site asked for in its manifest (<c>purpose.limits</c>): the tightest wins, and
/// nothing goes above the wallet's ceilings, which protect the computer and the protocol, not a choice of the visitor.
/// </summary>
public static class TextLimits
{
    public const int DefaultInputChars = 100_000;
    public const int DefaultAnswerTokens = 20_000;
    public const int DefaultCallSeconds = 300;
    public const int DefaultParallelCalls = 8;
    public const int DefaultCallsPerMinute = 300;

    public const int MaxInputChars = 400_000;
    public const int MaxAnswerTokens = 32_000;
    public const int MaxCallSeconds = 600;
    public const int MaxParallelCalls = 32;
    public const int MaxCallsPerMinute = 600;

    /// <summary>The longest answer the wallet hands back, whatever the token limit (a frame to a site stays under 1 MB).</summary>
    public const int MaxAnswerChars = 128_000;

    /// <summary>What a page gets when it asks for no particular length: the wallet's old default, kept.</summary>
    public const int DefaultMaxTokens = 512;

    public static readonly TextLimitSet Defaults =
        new(DefaultInputChars, DefaultAnswerTokens, DefaultCallSeconds, DefaultParallelCalls, DefaultCallsPerMinute);

    public static readonly TextLimitSet Ceilings =
        new(MaxInputChars, MaxAnswerTokens, MaxCallSeconds, MaxParallelCalls, MaxCallsPerMinute);

    /// <summary>The visitor's defaults from the settings, each kept inside its floor and its ceiling.</summary>
    public static TextLimitSet Global(LimitSettings limits) => new(
        Clamp(limits.TextInputChars, 1_000, MaxInputChars),
        Clamp(limits.TextAnswerTokens, 16, MaxAnswerTokens),
        Clamp(limits.TextCallSeconds, 5, MaxCallSeconds),
        Clamp(limits.ParallelCalls, 1, MaxParallelCalls),
        Clamp(limits.CallsPerMinute, 1, MaxCallsPerMinute));

    /// <summary>The visitor's limits for one site: its own values over the defaults.</summary>
    public static TextLimitSet ForSite(LimitSettings limits, SiteRecord? site)
    {
        var global = Global(limits);
        if (site is null) return global;
        return new TextLimitSet(
            Clamp(site.TextInputChars ?? global.InputChars, 1_000, MaxInputChars),
            Clamp(site.TextAnswerTokens ?? global.AnswerTokens, 16, MaxAnswerTokens),
            Clamp(site.TextCallSeconds ?? global.CallSeconds, 5, MaxCallSeconds),
            Clamp(site.ParallelCalls ?? global.ParallelCalls, 1, MaxParallelCalls),
            Clamp(site.CallsPerMinute ?? global.CallsPerMinute, 1, MaxCallsPerMinute));
    }

    /// <summary>
    /// The visitor's limits for one site, tightened by what the site declared itself (it can only hold itself back). The size of the
    /// input and the calls a minute stay the visitor's own here: the gate holds the site to its declared values for those two (the
    /// draft rules' "the stricter of the two wins"), and says so with its own reason when it does.
    /// </summary>
    public static TextLimitSet Effective(LimitSettings limits, SiteRecord? site, RequestedLimits? declared)
    {
        var set = ForSite(limits, site);
        if (declared is null) return set;
        return set with
        {
            AnswerTokens = Math.Min(set.AnswerTokens, declared.AnswerTokens ?? int.MaxValue),
            CallSeconds = Math.Min(set.CallSeconds, declared.CallSeconds ?? int.MaxValue),
            ParallelCalls = Math.Min(set.ParallelCalls, declared.ParallelCalls ?? int.MaxValue),
        };
    }

    /// <summary>
    /// What a site asks for beyond the visitor's limits for it, as the values the visitor may accept in one click (each at most the
    /// ceiling). Null when the site asks for nothing above them.
    /// </summary>
    public static RequestedLimits? Beyond(TextLimitSet current, RequestedLimits? asked)
    {
        if (asked is null) return null;
        int? Above(int? want, int have, int ceiling) => want is { } w && w > have ? Math.Min(w, ceiling) : null;
        var beyond = new RequestedLimits(
            Above(asked.InputChars, current.InputChars, MaxInputChars),
            Above(asked.AnswerTokens, current.AnswerTokens, MaxAnswerTokens),
            Above(asked.CallSeconds, current.CallSeconds, MaxCallSeconds),
            Above(asked.ParallelCalls, current.ParallelCalls, MaxParallelCalls),
            Above(asked.CallsPerMinute, current.CallsPerMinute, MaxCallsPerMinute));
        return beyond.Any ? beyond : null;
    }

    public static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;
}
