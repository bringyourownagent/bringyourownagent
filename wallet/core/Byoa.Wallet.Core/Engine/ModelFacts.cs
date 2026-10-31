using Byoa.Wallet.Settings;
using Byoa.Wallet.Sources;

namespace Byoa.Wallet.Engine;

/// <summary>
/// What the wallet knows of one source's properties (PROPOSAL-v2 3.2): what goes in, what comes out, how much it reads, where it runs.
/// A property is null when the wallet does not know it, and a site's condition on it is then neither met nor failed. Where the wallet takes
/// each one from: the visitor's word first, then the public catalog (for a model behind a key, which the catalog lists), then nothing.
/// It is the one input of <see cref="ModelFit"/>.
/// </summary>
/// <param name="Input">Modalities accepted (<c>text</c>, <c>image</c>, <c>audio</c>, as the list spells them); null if unknown.</param>
/// <param name="Output">Modalities produced; null if unknown.</param>
/// <param name="ContextThousands">Context length in thousands of tokens; null if unknown (most local models).</param>
/// <param name="Runs">Where it runs, in the grant's words (<c>local</c>, <c>home-server</c>, <c>api-key</c>); never unknown for a real source.</param>
/// <param name="Agent">The visitor's own agent, not a model: it takes and writes text, whatever program is behind it.</param>
/// <param name="AlsoText">The visitor ticked "Also use as a text model" for it.</param>
public sealed record ModelFacts(
    IReadOnlyList<string>? Input = null, IReadOnlyList<string>? Output = null, int? ContextThousands = null, string? Runs = null,
    bool Agent = false, bool AlsoText = false)
{
    /// <summary>A source the wallet knows nothing about.</summary>
    public static readonly ModelFacts Unknown = new();

    /// <summary>
    /// The facts of a source the visitor added. <paramref name="listed"/> is the catalog's entry for its model; it is used only for a model that
    /// is the catalog's own (behind an OpenRouter or an API key), never for a copy that runs on this computer, whose runtime may lack what the
    /// model has (a picture reader without its picture part).
    /// </summary>
    public static ModelFacts Of(SourceSetting setting, CatalogModel? listed, string runs)
    {
        if (string.Equals(setting.Kind, SourceKinds.Agent, StringComparison.OrdinalIgnoreCase))
            return new ModelFacts([Modalities.Text], [Modalities.Text], null, runs, Agent: true);
        var fromCatalog = setting.Kind is SourceKinds.OpenRouter or ApiServices.Kind ? listed : null;
        return new ModelFacts(
            setting.Inputs ?? fromCatalog?.InputModalities,
            setting.Outputs ?? fromCatalog?.OutputModalities,
            fromCatalog is { ContextLength: > 0 } ? fromCatalog.ContextLength / 1000 : null,
            runs, AlsoText: setting.AlsoText);
    }
}
