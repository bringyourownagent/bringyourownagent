using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Engine;

/// <summary>Why a model is in the list the consent window shows.</summary>
public enum ModelRole
{
    /// <summary>One of the visitor's own models the site did not name.</summary>
    Yours,

    /// <summary>One the site recommended.</summary>
    Recommended,

    /// <summary>One the site cannot work without.</summary>
    Required,
}

/// <summary>How a model that is not among the visitor's models yet would be added: behind a key he already gave.</summary>
/// <param name="Kind"><see cref="SourceKinds.OpenRouter"/> (the only road a plain name can be followed on without guessing the vendor's own spelling).</param>
public sealed record ModelRoad(string Kind, string Model);

/// <summary>One model the consent window offers a tick for.</summary>
/// <param name="Key">The source id for a model the visitor has; <c>new:openrouter:&lt;model&gt;</c> for one the wallet would add behind his key.</param>
/// <param name="ModelId">The model's name as everyone knows it ("google/gemma-3-1b-it"); empty if the wallet cannot tell.</param>
/// <param name="Where">"this PC", "OpenRouter key", "OpenAI key"...</param>
/// <param name="Cost">"free" or "about $0.002 an answer".</param>
/// <param name="Ready">It can answer now; a model that is off, not running or without its key is shown, unticked, with the reason in <paramref name="Note"/>.</param>
/// <param name="Fit">How it stands to what the site asked (<see cref="ModelFit"/>): only a model that <see cref="FitState.Fits"/> is ticked by the wallet.</param>
/// <param name="Marks">What the window says beside a model that does not fit: why it falls short, or which properties are not known.</param>
public sealed record ModelChoice(
    string Key, string? SourceId, string Name, string ModelId, string Where, bool Paid, string Cost,
    ModelRole Role, bool Checked, bool Ready, string? Note, ModelRoad? Road = null,
    FitState Fit = FitState.Fits, IReadOnlyList<string>? Marks = null)
{
    /// <summary>The visitor does not have it yet: ticking it adds it behind his key.</summary>
    public bool IsNew => SourceId is null;

    public const string NewPrefix = "new:" + SourceKinds.OpenRouter + ":";

    public static string NewKey(string model) => NewPrefix + model;

    public static bool TryParseNew(string key, out string model)
    {
        model = "";
        if (!key.StartsWith(NewPrefix, StringComparison.Ordinal)) return false;
        model = key[NewPrefix.Length..];
        return IsPlainModelName(model);
    }

    /// <summary>A model id the wallet will put in a request: letters, digits and . _ - : / only, as long as OpenRouter's ids are.</summary>
    public static bool IsPlainModelName(string model) =>
        model.Length is > 0 and <= 120 && model.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or ':' or '/');
}

/// <summary>A model of the visitor's that is not in a skill's list, and why ("draws pictures; ...", "cannot read pictures").</summary>
public sealed record ModelMiss(string Name, string Why);

/// <summary>What the consent window shows for the models of one skill, and what it needs back.</summary>
/// <param name="MissingRequired">The wishes no model of the visitor's (and none behind his keys) satisfies, in words.</param>
/// <param name="Shortfall">Set when fewer models can answer than the site needs at least ("needs at least 3 different models; 1 can answer").</param>
/// <param name="NotFitting">The visitor's models left out of the list because they do not suit the skill or what the site asked: the window counts them and says why.</param>
public sealed record ModelSection(
    string Skill, RequestedModels Spec, IReadOnlyList<ModelChoice> Choices, IReadOnlyList<string> MissingRequired, string? Shortfall,
    IReadOnlyList<ModelMiss>? NotFitting = null)
{
    public int Chosen => Choices.Count(c => c.Checked);

    /// <summary>The visitor's models that were left out, with the reasons (never null).</summary>
    public IReadOnlyList<ModelMiss> Misses => NotFitting ?? [];

    /// <summary>The site can be given what it needs: every required model is there and so are enough models.</summary>
    public bool CanServe => MissingRequired.Count == 0 && Shortfall is null;
}

/// <summary>One of the visitor's text models, as the planner sees it.</summary>
/// <param name="ModelId">The canonical name (<see cref="ModelNames"/>).</param>
/// <param name="Offer">In the visitor's default set.</param>
/// <param name="NotReady">Why it cannot answer now ("turned off", "not running", "no API key"...), or null.</param>
/// <param name="Facts">What the wallet knows of it (<see cref="ModelFacts"/>); null when it knows nothing, and then only the skill's own rule can speak.
/// His own AI agent, not a model, is the one whose facts say so: it is ticked for a site only when the site cannot work without it, or when he ticks it himself.</param>
public sealed record ShelfModel(string SourceId, string Name, string ModelId, string Where, bool Paid, string Cost, bool Offer, string? NotReady, ModelFacts? Facts = null)
{
    public bool Ready => NotReady is null;

    public bool Agent => Facts?.Agent == true;
}

/// <summary>What the planner works from.</summary>
/// <param name="Shelf">The visitor's text models in his order.</param>
/// <param name="Previous">The source ids he gave this site last time, in order.</param>
/// <param name="RoadFor">Whether a model that is not on the shelf can be added behind a key he gave, and how; null when it cannot.</param>
/// <param name="CostOf">What an answer of a model not yet on the shelf costs, in words.</param>
/// <param name="FactsOf">What the wallet knows of a model not yet on the shelf (the catalog's word); null: nothing.</param>
public sealed record ModelPlanContext(
    IReadOnlyList<ShelfModel> Shelf, IReadOnlyList<string> Previous, Func<string, ModelRoad?> RoadFor, Func<string, string> CostOf,
    Func<string, ModelFacts>? FactsOf = null);
