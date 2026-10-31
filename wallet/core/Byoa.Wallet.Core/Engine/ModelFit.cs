using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Engine;

/// <summary>How a source stands to what a site asks of a skill.</summary>
public enum FitState
{
    /// <summary>Every property the site asked about is known and right.</summary>
    Fits,

    /// <summary>Nothing known is wrong, but a property the site asked about is not known: offered, marked, never ticked by the wallet.</summary>
    Unknown,

    /// <summary>A property the site asked about is known and wrong. The site's <c>onMismatch</c> decides: dropped, or shown with the mismatch and never ticked.</summary>
    Mismatch,

    /// <summary>The skill itself rules it out (the primary-role rule). No site's word changes that.</summary>
    Excluded,
}

/// <summary>What <see cref="ModelFit.Evaluate"/> found.</summary>
/// <param name="Reasons">In plain words, why a source is <see cref="FitState.Mismatch"/> or <see cref="FitState.Excluded"/>.</param>
/// <param name="UnknownProperties">The properties the site asked about that the wallet does not know (<c>input</c>, <c>output</c>, <c>context</c>).</param>
/// <param name="Can">One verdict per asked condition whose property is known: the site is told these and never a value (PROPOSAL-v2 3.5). At most four keys.</param>
public sealed record FitVerdict(
    FitState State, IReadOnlyList<string> Reasons, IReadOnlyList<string> UnknownProperties, IReadOnlyDictionary<string, bool> Can)
{
    public bool Fits => State == FitState.Fits;

    /// <summary>The source is in the list the visitor sees, and in a grant: all but an excluded one, and a mismatch the site wants dropped.</summary>
    public bool Offered(bool dropOnMismatch) => State is FitState.Fits or FitState.Unknown || (State == FitState.Mismatch && !dropOnMismatch);
}

/// <summary>
/// The one place that decides whether a source suits what a site asks of a skill (PROPOSAL-v2 3.1 to 3.3). The planner, the list in the
/// consent window, the grant, a stored choice that is looked at again, and every call use it; a rule that lived in each of them would diverge.
/// First the skill's own properties, which no site can widen (a picture model is not offered for text unless the visitor ticked
/// "Also use as a text model"); then the site's <c>needs</c> and <c>kind</c>, which can only narrow.
/// </summary>
public static class ModelFit
{
    /// <summary>
    /// Whether a known mismatch drops the source (the site's <c>onMismatch</c>), by the default of PROPOSAL-v2 3.1 when the site said nothing:
    /// dropped for a model, shown for an agent.
    /// </summary>
    public static bool DropsOnMismatch(RequestedModels? spec, ModelFacts facts) => (spec?.OnMismatch ?? (facts.Agent ? "show" : "drop")) == "drop";

    /// <param name="modelId">The canonical name (<c>agent/&lt;name&gt;</c> for an agent): a site that names an agent in <c>require</c> or <c>recommend</c> asks for it.</param>
    /// <param name="spec">What the site asked of the skill's models; null when it asked for none (only the skill's own properties count).</param>
    public static FitVerdict Evaluate(ModelFacts facts, string modelId, string skill, RequestedModels? spec)
    {
        var excluded = SkillRule(facts, skill);
        if (excluded is not null) return new FitVerdict(FitState.Excluded, [excluded], [], new Dictionary<string, bool>());

        var wrong = new List<string>();
        var unknown = new List<string>();
        var can = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (spec?.Needs is { } needs)
        {
            // (a source the visitor added for text surely takes text and writes text, whatever else nobody knows of it)
            var sure = skill == Vocabulary.Generate ? new[] { Modalities.Text } : [];
            CheckModalities("input", needs.Input, facts.Input, sure);
            CheckModalities("output", needs.Output, facts.Output, sure);
            if (needs.ContextThousands is { } wanted)
            {
                if (facts.ContextThousands is { } has)
                {
                    can["context"] = has >= wanted;
                    if (has < wanted) wrong.Add("reads " + has + "k at most; the site wants " + wanted + "k or more");
                }
                else unknown.Add("context");
            }

            if (needs.Runs is { } places)
            {
                if (facts.Runs is { } runs)
                {
                    can["runs"] = places.Contains(runs);
                    if (!places.Contains(runs)) wrong.Add("runs on " + Place(runs) + "; the site wants " + string.Join(" or ", places.Select(Place)));
                }
                else unknown.Add("runs");
            }
        }

        if (spec is not null && KindMismatch(facts, modelId, spec) is { } kind) wrong.Add(kind);
        var state = wrong.Count > 0 ? FitState.Mismatch : unknown.Count > 0 ? FitState.Unknown : FitState.Fits;
        return new FitVerdict(state, wrong, unknown, can);

        void CheckModalities(string key, IReadOnlyList<string>? asked, IReadOnlyList<string>? has, IReadOnlyList<string> sure)
        {
            if (asked is null) return;
            // The words nobody can vouch for: with the property known, they are missing (a mismatch); with it unknown, they are not known.
            var unvouched = asked.Where(word => !(has ?? sure).Contains(word)).ToList();
            if (has is null && unvouched.Count > 0)
            {
                unknown.Add(key);
                return;
            }

            can[key] = unvouched.Count == 0;
            wrong.AddRange(unvouched.Select(word => Lacks(key, word)));
        }
    }

    /// <summary>One missing modality, in words for a person: "cannot read pictures", "does not draw pictures".</summary>
    static string Lacks(string key, string modality) => (key, modality) switch
    {
        ("input", Modalities.Audio) => "cannot hear audio",
        ("input", _) => "cannot read " + Plain(modality),
        ("output", Modalities.Text) => "does not write text",
        ("output", Modalities.Image) => "does not draw pictures",
        _ => "does not make " + Plain(modality),
    };

    /// <summary>The skill's own properties (PROPOSAL-v2 3.3), as the reason a source is out, or null when it may serve the skill.</summary>
    static string? SkillRule(ModelFacts facts, string skill)
    {
        switch (skill)
        {
            case Vocabulary.Generate when facts.Output is { } output:
                // text.generate: writes text, and does not draw pictures unless the visitor said that is fine for this source. An unknown output is
                // offered: the visitor added the source for text, and the wallet has no reason to think it draws.
                if (!output.Contains(Modalities.Text)) return "does not write text";
                return output.Contains(Modalities.Image) && !facts.AlsoText
                    ? "draws pictures; to use it for text, tick “Also use as a text model” in its settings"
                    : null;
            case Vocabulary.ImageGenerate:
                // image.generate: known to draw pictures. An unknown output is not enough, and an agent never does.
                return facts.Output?.Contains(Modalities.Image) == true ? null : facts.Output is null ? "is not known to draw pictures" : "does not draw pictures";
            default:
                return null;
        }
    }

    /// <summary>The site's <c>kind</c> (PROPOSAL-v2 3.1): models, the visitor's agent, or either. An agent the site names (<c>agent/*</c> in <c>require</c> or <c>recommend</c>) is asked for.</summary>
    static string? KindMismatch(ModelFacts facts, string modelId, RequestedModels spec)
    {
        if (spec.Kind == RequestedModels.AnyKind) return null;
        if (facts.Agent == (spec.Kind == RequestedModels.AgentKind)) return null;
        if (facts.Agent) return NamedBySite(spec, modelId) ? null : "your agent; the site asks for models";
        return "a model; the site asks for your agent";
    }

    static bool NamedBySite(RequestedModels spec, string modelId) =>
        spec.Require.Any(wish => wish.AnyOf.Any(name => ModelNames.Matches(name, modelId))) || spec.Recommend.Any(name => ModelNames.Matches(name, modelId));

    /// <summary>
    /// What a site asked of the models beyond their names, in short plain words, one chip each: "reads pictures", "draws pictures", "hears audio",
    /// "context 64k+", "runs on this PC", "your own agent". Text is the skill's own word and is not repeated.
    /// </summary>
    public static IReadOnlyList<string> NeedsWords(RequestedModels spec)
    {
        var words = new List<string>();
        if (spec.Needs is { } needs)
        {
            foreach (var word in needs.Input ?? []) if (word != Modalities.Text) words.Add(word == Modalities.Audio ? "hears audio" : "reads " + Plain(word));
            foreach (var word in needs.Output ?? []) if (word != Modalities.Text) words.Add(word == Modalities.Image ? "draws pictures" : "makes " + Plain(word));
            if (needs.ContextThousands is { } context) words.Add("context " + context + "k+");
            if (needs.Runs is { } places) words.Add("runs on " + string.Join(" or ", places.Select(Place)));
        }

        if (spec.Kind == RequestedModels.AgentKind) words.Add("your own agent");
        else if (spec.Kind == RequestedModels.AnyKind) words.Add("a model or your agent");
        return words;
    }

    /// <summary>What is said beside a model that is in a list and does not surely fit: the mismatches, or the properties that are not known. Null when it fits.</summary>
    public static IReadOnlyList<string>? Marks(FitVerdict fit) => fit.State switch
    {
        FitState.Mismatch => fit.Reasons,
        FitState.Unknown => ["not known: " + string.Join(", ", fit.UnknownProperties.Select(Property))],
        _ => null,
    };

    static string Property(string key) => key switch
    {
        "input" => "what it reads",
        "output" => "what it makes",
        "context" => "how much it reads at once",
        "runs" => "where it runs",
        _ => key,
    };

    /// <summary>A modality or an ability the site asked about, in words for a person.</summary>
    public static string Plain(string modality) => modality switch
    {
        Modalities.Image => "pictures",
        Modalities.Audio => "audio",
        _ => modality,
    };

    /// <summary>A place in the grant's words, in words for a person.</summary>
    public static string Place(string runs) => runs switch
    {
        "local" => "this PC",
        "home-server" => "your home server",
        _ => "an API key",
    };
}
