using System;
using System.Collections.Generic;

namespace BringYourOwnAgent;

/// <summary>
/// Vocabulary of the BYOA 0.1-draft rules (spec/rules-0.1-draft.md in
/// https://github.com/bringyourownagent/bringyourownagent). Everything here is a draft
/// for discussion and may change.
/// </summary>
public static class Rules
{
    /// <summary>Protocol versions this library understands (the <c>byoa</c> field).</summary>
    public static IReadOnlyList<string> SupportedVersions { get; } = Array.AsReadOnly(new[] { "0.1-draft" });

    /// <summary>The first skill vocabulary. Well-formed ids outside it only produce a warning.</summary>
    public static IReadOnlyList<string> KnownSkills { get; } = Array.AsReadOnly(new[]
    {
        "text.generate",
        "text.understand",
        "text.embed",
        "audio.transcribe",
        "audio.speak",
        "image.understand",
        "image.generate",
        "video.understand",
        "video.generate",
    });

    /// <summary>How sensitive the requester's data is (the manifest <c>privacy</c> field).</summary>
    public static IReadOnlyList<string> PrivacyClasses { get; } = Array.AsReadOnly(new[]
    {
        "public",
        "personal",
        "personal-voice",
        "sensitive",
        "local-only",
    });

    /// <summary>Hard limits a manifest (<c>purpose.limits</c>) or a grant (<c>limits</c>) may set.</summary>
    public static IReadOnlyList<string> KnownLimits { get; } =
        Array.AsReadOnly(new[] { "maxCallsPerMinute", "maxClipSeconds", "maxTextChars" });

    /// <summary>Periods allowed in the manifest <c>expectedCost</c> object.</summary>
    public static IReadOnlyList<string> CostPeriods { get; } = Array.AsReadOnly(new[] { "perCall", "perSession", "perHour" });

    /// <summary>Kinds of provider the agent wallet can route a call to.</summary>
    public static IReadOnlyList<string> ProviderKinds { get; } = Array.AsReadOnly(new[]
    {
        "subscription",
        "api-key",
        "connected-account",
        "local-model",
        "home-server",
    });

    /// <summary>Providers that keep the data on the visitor's computer or in their home.</summary>
    public static IReadOnlyList<string> LocalProviders { get; } = Array.AsReadOnly(new[] { "local-model", "home-server" });

    /// <summary>
    /// The four decisions of the call gate as they are written in JSON, mildest first.
    /// <c>Decisions[(int)decision]</c> is the name of a <see cref="Decision"/>.
    /// </summary>
    public static IReadOnlyList<string> Decisions { get; } = Array.AsReadOnly(new[] { "allowed", "warn", "ask", "blocked" });

    /// <summary>Limits that count things and therefore must be whole numbers.</summary>
    internal static readonly string[] CountLimits = { "maxCallsPerMinute", "maxTextChars" };

    /// <summary>Longest allowed <c>purpose.summary</c>, in Unicode code points.</summary>
    internal const int SummaryMaxLength = 200;

    /// <summary>Longest allowed skill id, in characters.</summary>
    internal const int SkillIdMaxLength = 64;

    /// <summary>The rate limit window, in milliseconds.</summary>
    internal const long RateWindowMs = 60000;

    /// <summary>The largest number accepted anywhere: 2^53 - 1.</summary>
    internal const double MaxNumber = 9007199254740991;

    internal static bool Has(IReadOnlyList<string> list, string? value)
    {
        if (value is null)
        {
            return false;
        }

        for (var i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i], value, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
