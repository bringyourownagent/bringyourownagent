using System.Text.Json.Nodes;

namespace Byoa.Wallet.Engine;

/// <summary>One skill a manifest asks for.</summary>
/// <param name="Models">What the skill asks about its models (several, named, recommended); null asks for the skill only.</param>
public sealed record RequestedSkill(string Id, bool Required, IReadOnlyList<string> Languages, RequestedModels? Models = null);

/// <summary>
/// One thing the site requires of its models: any of these names, or masks ending in <c>*</c> (<c>openai/gpt-5*</c>). A single
/// name is a list of one.
/// </summary>
public sealed record ModelWish(IReadOnlyList<string> AnyOf);

/// <summary>
/// The conditions of a site's <c>needs</c> (PROPOSAL-v2 3.1): all of these input and output modalities, at least this many thousands of
/// tokens of context, and one of these places the source may run. A condition on a property the wallet does not know is never a failure:
/// the fit engine marks the source unknown instead. The four known keys are read and anything else in the block is ignored.
/// </summary>
/// <param name="Input">Every one of these modalities must go in (<c>text</c>, <c>image</c>, <c>audio</c>); null when not asked.</param>
/// <param name="Output">Every one of these modalities must come out; null when not asked.</param>
/// <param name="ContextThousands">At least this many thousands of tokens of context; null when not asked.</param>
/// <param name="Runs">One of these places (<c>local</c>, <c>home-server</c>, <c>api-key</c> — the grant's own words); null when not asked.</param>
public sealed record ModelNeeds(
    IReadOnlyList<string>? Input, IReadOnlyList<string>? Output, int? ContextThousands, IReadOnlyList<string>? Runs)
{
    public const int MaxValues = 8;
    public const int MaxContextThousands = 200_000;

    public static readonly ModelNeeds Empty = new(null, null, null, null);

    public bool Any => Input is not null || Output is not null || ContextThousands is not null || Runs is not null;

    /// <summary>The places a source may run, in the grant's own vocabulary (PROPOSAL-v2 3.2); a site that writes another word gets no condition.</summary>
    public static readonly IReadOnlySet<string> KnownRuns = new HashSet<string>(StringComparer.Ordinal) { "local", "home-server", "api-key" };

    public static ModelNeeds? From(JsonNode? node)
    {
        if (node is not JsonObject needs) return null;                 // a `needs` that is not an object is not a set of conditions
        return new ModelNeeds(Words(needs["input"], Sources.Modalities.Known), Words(needs["output"], Sources.Modalities.Known),
            Whole(needs["context"]) ?? Whole(needs["context"] is JsonObject form ? form["min"] : null),
            Words(needs["runs"], KnownRuns));
    }

    /// <summary>An array of words (<c>["text","image"]</c>) or the long form <c>{"includes":[...]}</c> / <c>{"oneOf":[...]}</c>: the known words, or null when none is.</summary>
    static IReadOnlyList<string>? Words(JsonNode? node, IReadOnlySet<string> known)
    {
        var list = node as JsonArray;
        if (list is null && node is JsonObject form) list = form["includes"] as JsonArray ?? form["oneOf"] as JsonArray;
        if (list is null) return null;
        var words = new List<string>();
        foreach (var item in list)
        {
            if (item is not JsonValue v || !v.TryGetValue<string>(out var word)) continue;
            var clean = word.Trim().ToLowerInvariant();
            if (clean.Length is 0 or > 16 || !known.Contains(clean)) continue;      // a word the wallet does not know is not a condition
            if (!words.Contains(clean)) words.Add(clean);
            if (words.Count >= MaxValues) break;
        }

        return words.Count == 0 ? null : words;
    }

    static int? Whole(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<int>(out var i) && i >= 1) return Math.Min(i, MaxContextThousands);
        return v.TryGetValue<double>(out var d) && double.IsFinite(d) && d is >= 1 and <= MaxContextThousands ? (int)d : null;
    }

    /// <summary>The conditions in one comparable line, so a change in them is seen as the site asking for more.</summary>
    public string ToAsk() =>
        (Input is { } input ? "input=" + string.Join("+", input) + ";" : "")
        + (Output is { } output ? "output=" + string.Join("+", output) + ";" : "")
        + (ContextThousands is { } context ? "context=" + context.ToString(System.Globalization.CultureInfo.InvariantCulture) + ";" : "")
        + (Runs is { } runs ? "runs=" + string.Join("|", runs) + ";" : "");
}

/// <summary>
/// The models a skill asks for (manifest: <c>skills.&lt;skill&gt;.models</c>): how many different ones, which are required and which
/// only recommended, what properties they must have, and why, in a line and at length. The wallet reads it itself; the draft rules do
/// not check it. The block is read even without a <c>count</c> (its other keys were lost there once): a block that says nothing about
/// the count asks for one to five, as a range without its <c>max</c> does.
/// </summary>
/// <param name="Min">The fewest models the site can work with.</param>
/// <param name="Max">The most it will use.</param>
/// <param name="Distinct"><c>vendor</c>: from different companies where possible; <c>model</c>: just different models.</param>
/// <param name="Require">Models without which the site cannot work (each a wish; any one of its names will do).</param>
/// <param name="Recommend">Models the site likes, in the site's order (names or masks).</param>
/// <param name="Why">One line for the consent window.</param>
/// <param name="WhyLong">The longer explanation behind the "More" button.</param>
/// <param name="Names">The site asks to see the names of the models it gets (the visitor may hide them).</param>
/// <param name="Needs">The conditions on the models' properties (<see cref="ModelNeeds"/>); null when the site asked for none.</param>
/// <param name="Kind"><c>model</c> (the default), <c>agent</c> or <c>any</c>: which sources may fill the skill. It filters; it never ticks.</param>
/// <param name="OnMismatch"><c>drop</c> or <c>show</c>; null when the site said nothing, and then a model that fails a known condition is
/// dropped and an agent is shown with its mismatch (PROPOSAL-v2 3.1).</param>
/// <param name="Abilities">The abilities the site hopes the visitor's agent has (<c>web.search</c>, <c>web.read</c>...); it never changes what the agent may do.</param>
public sealed record RequestedModels(
    int Min, int Max, string Distinct, IReadOnlyList<ModelWish> Require, IReadOnlyList<string> Recommend,
    string? Why, string? WhyLong, bool Names,
    ModelNeeds? Needs = null, string Kind = "model", string? OnMismatch = null, IReadOnlyList<string>? Abilities = null)
{
    public const int MaxModels = 64;
    public const int MaxRequire = 16;
    public const int MaxRecommend = 64;
    public const int MaxWhy = 160;
    public const int MaxWhyLong = 2000;
    public const int MaxAbilities = 16;
    const int MaxNameChars = 100;

    public bool ByVendor => Distinct == "vendor";

    /// <summary>The words of the site's <c>kind</c> (PROPOSAL-v2 3.1): which sources may fill the skill. Anything else reads as <see cref="ModelKind"/>.</summary>
    public const string ModelKind = "model";
    public const string AgentKind = "agent";
    public const string AnyKind = "any";

    /// <summary>What the site asked of the models beyond their names and count, in one comparable line: a change in it is the site asking
    /// for more, never something the wallet decides on its own (PROPOSAL-v2 3.1). Empty while it asks nothing more than names and a count,
    /// which is also what a site the visitor answered before wallet 0.36 stored.</summary>
    public string Ask => string.Join(';', new[]
    {
        Needs is { Any: true } needs ? "needs:" + needs.ToAsk() : null,
        Kind != ModelKind ? "kind:" + Kind : null,
        OnMismatch is { } mismatch ? "onMismatch:" + mismatch : null,
        Abilities is { Count: > 0 } abilities ? "abilities:" + string.Join(",", abilities) : null,
    }.Where(part => part is not null));

    public static RequestedModels? From(JsonNode? node)
    {
        if (node is not JsonObject block) return null;
        var min = 1;
        var max = 5;
        switch (block["count"])
        {
            case JsonValue single when Whole(single) is { } n:
                min = max = Math.Clamp(n, 1, MaxModels);
                break;
            case JsonObject range:
                min = Math.Clamp(Whole(range["min"]) ?? 1, 1, MaxModels);
                max = Math.Clamp(Whole(range["max"]) ?? Math.Max(min, 5), 1, MaxModels);
                if (max < min) max = min;
                break;
            default:
                break;                           // a block without a count still asks: its needs, kind and names are read
        }

        var distinct = Text(block["distinct"], 16)?.ToLowerInvariant() == "vendor" ? "vendor" : "model";
        var require = new List<ModelWish>();
        if (block["require"] is JsonArray required)
        {
            foreach (var item in required)
            {
                if (require.Count >= MaxRequire) break;
                var names = new List<string>();
                if (item is JsonArray any) names.AddRange(any.Select(x => Name(x)).OfType<string>());
                else if (Name(item) is { } one) names.Add(one);
                if (names.Count > 0) require.Add(new ModelWish(names.Distinct(StringComparer.Ordinal).Take(MaxRequire).ToList()));
            }
        }

        var recommend = new List<string>();
        if (block["recommend"] is JsonArray recommended)
        {
            foreach (var item in recommended)
            {
                if (recommend.Count >= MaxRecommend) break;
                if (Name(item) is { } name && !recommend.Contains(name)) recommend.Add(name);
            }
        }

        var names2 = block["names"] is JsonValue flag && flag.TryGetValue<bool>(out var wants) && wants;
        // The properties, the kind of source and the mismatch rule (PROPOSAL-v2 3.1): a word the wallet does not know is the default, never an error.
        var needs = ModelNeeds.From(block["needs"]);
        var kind = Text(block["kind"], 8)?.ToLowerInvariant() is { Length: > 0 } word && word is AgentKind or AnyKind ? word : ModelKind;
        var onMismatch = Text(block["onMismatch"], 8)?.ToLowerInvariant() == "show" ? "show" : Text(block["onMismatch"], 8)?.ToLowerInvariant() == "drop" ? "drop" : null;
        var abilities = new List<string>();
        if (block["abilities"] is JsonArray hoped)
        {
            foreach (var item in hoped)
            {
                if (abilities.Count >= MaxAbilities) break;
                if (Name(item) is { } ability && !abilities.Contains(ability)) abilities.Add(ability);
            }
        }

        // A site that requires more models than it will use is asking for something it cannot use: the wishes win.
        if (require.Count > max) max = Math.Min(require.Count, MaxModels);
        return new RequestedModels(min, max, distinct, require, recommend, Text(block["why"], MaxWhy), Text(block["whyLong"], MaxWhyLong), names2,
            needs, kind, onMismatch, abilities);
    }

    /// <summary>A model name or mask as the matcher wants it: trimmed, lower case, printable, at most 100 characters.</summary>
    static string? Name(JsonNode? node)
    {
        var text = Text(node, MaxNameChars)?.ToLowerInvariant();
        return string.IsNullOrWhiteSpace(text) || text.Any(char.IsWhiteSpace) ? null : text;
    }

    static string? Text(JsonNode? node, int max)
    {
        if (node is not JsonValue v || !v.TryGetValue<string>(out var s)) return null;
        var clean = new string(s.Where(c => !char.IsControl(c) || c == '\n').ToArray()).Trim();
        if (clean.Length == 0) return null;
        return clean.Length > max ? clean[..max] : clean;
    }

    static int? Whole(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<int>(out var i)) return i;
        return v.TryGetValue<double>(out var d) && double.IsFinite(d) && d == Math.Floor(d) && d is >= int.MinValue and <= int.MaxValue ? (int)d : null;
    }
}

/// <summary>
/// The limits a manifest declares for the text skill (<c>purpose.limits</c>): what the site says it needs. They hold the site
/// back (the tightest limit wins) and they are what the consent window offers the visitor to accept when they are above his own.
/// </summary>
public sealed record RequestedLimits(int? InputChars, int? AnswerTokens, int? CallSeconds, int? ParallelCalls, int? CallsPerMinute)
{
    public bool Any => InputChars is not null || AnswerTokens is not null || CallSeconds is not null || ParallelCalls is not null || CallsPerMinute is not null;

    public static RequestedLimits? From(JsonNode? node)
    {
        if (node is not JsonObject limits) return null;
        var result = new RequestedLimits(
            Count(limits["maxTextChars"]), Count(limits["maxAnswerTokens"]), Count(limits["maxCallSeconds"]),
            Count(limits["maxParallelCalls"]), Count(limits["maxCallsPerMinute"]));
        return result.Any ? result : null;
    }

    static int? Count(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<int>(out var i)) return i >= 1 ? i : null;
        return v.TryGetValue<double>(out var d) && double.IsFinite(d) && d == Math.Floor(d) && d is >= 1 and <= int.MaxValue ? (int)d : null;
    }
}

/// <summary>
/// The parts of a manifest (already valid under spec/rules-0.1-draft.md) the wallet uses: the purpose, the skills
/// with their languages and models, the privacy class, the expected cost per session and the limits the site declared.
/// </summary>
public sealed record RequestedManifest(string Json, string Summary, IReadOnlyList<RequestedSkill> Skills, string Privacy, decimal? ExpectedPerSession,
    RequestedLimits? Limits = null, bool ConnectCheck = false)
{
    public static RequestedManifest From(JsonObject manifest)
    {
        var summary = manifest["purpose"]?["summary"]?.GetValue<string>()?.Trim() ?? "";
        var skills = new List<RequestedSkill>();
        if (manifest["skills"] is JsonObject s)
        {
            foreach (var (id, value) in s.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                var required = value?["required"] is JsonValue r && r.TryGetValue<bool>(out var b) && b;
                var languages = new List<string>();
                if (value?["languages"] is JsonArray langs)
                {
                    foreach (var l in langs)
                    {
                        if (l is JsonValue lv && lv.TryGetValue<string>(out var tag) && Sources.Vocabulary.PrimaryLanguage(tag) is { } p && !languages.Contains(p))
                        {
                            languages.Add(p);
                        }
                    }
                }

                skills.Add(new RequestedSkill(id, required, languages, RequestedModels.From(value?["models"])));
            }
        }

        var privacy = manifest["privacy"] is JsonValue pv && pv.TryGetValue<string>(out var ps) ? ps : "";
        decimal? expected = manifest["expectedCost"]?["perSession"] is JsonValue ev && ev.TryGetValue<string>(out var es) && Money.TryParse(es, out var e) ? e : null;
        // the site says it shows the connect check code on its page (CONTRACT-DIRECT.md section 3a)
        var check = manifest["connect"]?["check"] is JsonValue cv && cv.TryGetValue<bool>(out var cb) && cb;
        return new RequestedManifest(manifest.ToJsonString(), summary, skills, privacy, expected, RequestedLimits.From(manifest["purpose"]?["limits"]), check);
    }

    public RequestedSkill? Skill(string id) => Skills.FirstOrDefault(s => s.Id == id);
}

/// <summary>The origins the wallet accepts: https sites, and http on this computer (for tests).</summary>
public static class Origins
{
    public static bool TryParse(string? origin, out string host, out bool secure)
    {
        host = "";
        secure = false;
        if (string.IsNullOrEmpty(origin) || origin.Length > 300) return false;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
        if (uri.GetLeftPart(UriPartial.Authority) != origin) return false;      // exactly scheme://host[:port], no path
        if (!string.IsNullOrEmpty(uri.UserInfo)) return false;
        if (uri.Scheme == Uri.UriSchemeHttps)
        {
            secure = true;
        }
        else if (uri.Scheme == Uri.UriSchemeHttp && (uri.Host is "localhost" or "127.0.0.1" or "[::1]"))
        {
            secure = false;
        }
        else
        {
            return false;
        }

        host = uri.IsDefaultPort ? uri.Host : uri.Host + ":" + uri.Port;
        return true;
    }
}
